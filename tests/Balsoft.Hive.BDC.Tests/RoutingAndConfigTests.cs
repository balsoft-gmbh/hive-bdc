using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Delivery;
using Balsoft.Hive.BDC.Documents;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class RoutingAndConfigTests
{
    [Fact]
    public void MigrationPreservesCompanyOutputAndPerTypeOverride()
    {
        var options = new BDCOptions
        {
            Companies = { new CompanyProfile
            {
                DataAreaId = "A", Input = @"C:\input", Output = @"D:\exports",
                DocumentTypes = { ["PurchaseOrder"] = new CompanyDocumentTypeOptions { Output = @"D:\orders" } },
            } },
        };
        options.Normalize();
        options.Normalize();
        Assert.Equal(@"D:\exports", options.DocumentTypes.Single(t => t.Id == "SalesInvoice").Companies.Single().Output);
        Assert.Equal(@"D:\orders", options.DocumentTypes.Single(t => t.Id == "PurchaseOrder").Companies.Single().Output);
    }

    private static DocumentRoute Route(string company, string input, bool sp = false) => new()
    {
        DataAreaId = company, Input = input,
        SharePoint = new SharePointTargetOptions { Enabled = sp, SiteUrl = "https://x.sharepoint.com/sites/a", Library = "Docs" },
    };

    [Fact]
    public void NormalizeAddsMissingBuiltInsAndDropsEntriesWithoutInput()
    {
        var o = new BDCOptions();
        o.DocumentTypes.Add(new DocumentTypeOptions { Id = "PurchaseOrder", Companies = { Route("001", ""), Route("002", @"C:\po") } });
        o.Normalize();

        Assert.Equal(6, o.DocumentTypes.Count);
        var po = o.DocumentTypes.Single(t => t.Id == "PurchaseOrder");
        Assert.Single(po.Companies);
        Assert.Equal("002", po.Companies[0].DataAreaId);
    }

    [Fact]
    public void LegacyCompanyListIsMigratedUnderEveryType()
    {
        var o = new BDCOptions();
        o.Companies.Add(new CompanyProfile
        {
            DataAreaId = "eco", Input = @"C:\in",
            SharePoint = new SharePointTargetOptions { Enabled = true, SiteUrl = "https://x.sharepoint.com/sites/a", Library = "Docs" },
            DocumentTypes = { ["PurchaseOrder"] = new CompanyDocumentTypeOptions { Input = @"C:\po", SharePoint = new CompanyDocumentSharePointOptions { Library = "Orders" } } },
        });
        o.Normalize();

        Assert.Empty(o.Companies);
        Assert.All(o.DocumentTypes, t => Assert.Single(t.Companies));
        var po = o.DocumentTypes.Single(t => t.Id == "PurchaseOrder").Companies[0];
        Assert.Equal(@"C:\po", po.Input);
        Assert.Equal("Orders", po.SharePoint.Library);
        Assert.True(po.SharePoint.Enabled);
        Assert.Equal(@"C:\in", o.DocumentTypes.Single(t => t.Id == "SalesInvoice").Companies[0].Input);
    }

    [Fact]
    public void FolderUsedByOneTypeImpliesIt_SharedFolderDoesNot()
    {
        var invoice = new DocumentTypeOptions { Id = "SalesInvoice", Companies = { Route("eco", @"C:\shared") } };
        var confirm = new DocumentTypeOptions { Id = "SalesConfirmation", Companies = { Route("eco", @"C:\shared\") } };
        var po = new DocumentTypeOptions { Id = "PurchaseOrder", Companies = { Route("eco", @"C:\po"), Route("002", @"c:\PO") } };

        var folders = ScanFolder.Build(new[] { invoice, confirm, po });

        Assert.Equal(2, folders.Count);
        Assert.Null(folders.Single(f => f.Routes.Count == 2 && f.Routes.All(r => r.Type.Id != "PurchaseOrder")).ImpliedTypeId);
        var poFolder = folders.Single(f => f.Routes.All(r => r.Type.Id == "PurchaseOrder"));
        Assert.Equal("PurchaseOrder", poFolder.ImpliedTypeId);
        Assert.Equal(2, poFolder.Routes.Count);   // same folder, two companies, case-insensitive
    }

    [Fact]
    public void FolderUsedOnlyByDisabledTypesIsNotScanned()
    {
        // Customer install 2026-10-11: five disabled types kept the installer default C:\Temp\HiveBDC and
        // the service polled it every 30 s into an access-denied error.
        var invoice = new DocumentTypeOptions { Id = "SalesInvoice", Companies = { Route("agr", @"\\share\E-Invoice") } };
        var confirm = new DocumentTypeOptions { Id = "SalesConfirmation", Enabled = false, Companies = { Route("default", @"C:\Temp\HiveBDC") } };
        var po = new DocumentTypeOptions { Id = "PurchaseOrder", Enabled = false, Companies = { Route("default", @"C:\Temp\HiveBDC") } };
        var quote = new DocumentTypeOptions { Id = "SalesQuotation", Enabled = false, Companies = { Route("agr", @"\\share\E-Invoice") } };

        var folders = ScanFolder.Build(new[] { invoice, confirm, po, quote });

        var shared = Assert.Single(folders);
        Assert.Equal(@"\\share\E-Invoice", shared.Path);
        Assert.Equal(2, shared.Routes.Count);   // the disabled type sharing the folder keeps its route (pairs go to error\)
        Assert.Null(shared.ImpliedTypeId);
    }

    [Fact]
    public void SharePointOnADisabledTypeDoesNotCountAsEnabled()
    {
        var o = new BDCOptions();
        o.DocumentTypes.Add(new DocumentTypeOptions { Id = "SalesInvoice", Companies = { Route("agr", @"C:\in") } });
        o.DocumentTypes.Add(new DocumentTypeOptions { Id = "PurchaseOrder", Enabled = false, Companies = { Route("agr", @"C:\po", sp: true) } });

        Assert.False(o.AnySharePointEnabled());
        Assert.Equal(0, o.SharePointEntryCount());
        Assert.DoesNotContain(o.ActiveRoutes(), r => r.SharePoint.Enabled);

        o.DocumentTypes[1].Enabled = true;
        Assert.True(o.AnySharePointEnabled());
        Assert.Equal(1, o.SharePointEntryCount());
    }

    [Fact]
    public void DiscoveredTypeCopiesTheSeedRouteWithoutItsOutput()
    {
        var seed = Route("eco", @"C:\in", sp: true); seed.Output = @"C:\out\SalesInvoice";
        var t = DocumentTypeCatalog.Discovered("Packing Slip!", DocumentParty.Vendor, seed);

        Assert.Equal("PackingSlip", t.Id);
        Assert.Equal("Packing slip", t.DisplayName);
        Assert.True(t.Discovered);
        Assert.Equal(DocumentHandlerKind.Pdf, t.Handler);
        var r = Assert.Single(t.Companies);
        Assert.Equal(@"C:\in", r.Input);
        Assert.Equal("", r.Output);
        Assert.True(r.SharePoint.Enabled);
        Assert.Equal(@"C:\in\out\PackingSlip", r.OutputDirFor(t));
    }

    [Theory]
    [InlineData("SalesInvoice", true)]
    [InlineData("Sales Invoice", false)]
    [InlineData("", false)]
    [InlineData("a-b_c1", true)]
    public void IdValidation(string id, bool valid) => Assert.Equal(valid, DocumentTypeCatalog.IsValidId(id));

    [Fact]
    public void ColumnTemplatesResolveTopLevelPropertiesAndDates()
    {
        const string json = """{ "documentNumber": "PO1", "dataAreaId": "002", "issueDate": "22.09.2026", "amount": 12.5 }""";
        var map = new Dictionary<string, string>
        {
            ["Title"] = "{documentNumber}", ["Mandant"] = "{DATAAREAID}", ["Date"] = "{issueDate:date}", ["Amt"] = "{amount}", ["Missing"] = "{nope}",
        };
        var warnings = new List<string>();
        var fields = SharePointFieldTemplate.Resolve(json, map, warnings.Add);

        Assert.Equal("PO1", fields["Title"]);
        Assert.Equal("002", fields["Mandant"]);
        Assert.Equal("2026-09-22", fields["Date"]);
        Assert.Equal("12.5", fields["Amt"]);
        Assert.False(fields.ContainsKey("Missing"));
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("SalesInvoices", "SalesInvoices")]
    [InlineData("https://x.sharepoint.com/sites/Site/SalesInvoices/Forms/AllItems.aspx", "SalesInvoices")]
    [InlineData("https://x.sharepoint.com/sites/Site/Freigegebene%20Dokumente/", "Freigegebene Dokumente")]
    [InlineData("  Rechnungen und Gutschriften ", "Rechnungen und Gutschriften")]
    public void LibraryKeyReducesAnyFormToTheUrlSegment(string input, string expected)
        => Assert.Equal(expected, SharePointUploader.LibraryKey(input));

    [Fact]
    public void HtmlBodiesAreDetectedAndBannerIsInsertedAfterBodyTag()
    {
        var plan = new MailPlan(new[] { "test@x.de" }, Array.Empty<string>(), true, "Test", new[] { "customer@y.de" }, Array.Empty<string>());
        var html = MailSender.BuildBody("<HTML><BODY><P class=MsoNormal>Hallo</P></BODY></HTML>", "Purchase order", "PO1", "vendor", plan);
        var text = MailSender.BuildBody("Hallo\nGruss", "Purchase order", "PO1", "vendor", plan);

        Assert.True(html.IsHtml);
        Assert.Matches("<BODY><div", html.Content);
        Assert.Contains("customer@y.de", html.Content);
        // A plain-text body is shown with the same highlighted banner when redirected.
        Assert.True(text.IsHtml);
        Assert.Matches("<body><div style=\"border:2px solid", text.Content);
        Assert.Contains("Hallo<br/>\nGruss", text.Content);
        Assert.False(MailSender.LooksLikeHtml("Sehr geehrte Damen und Herren, Preis < 100"));
    }

    [Fact]
    public void PlainTextBodyToTheCustomerIsSentUnchanged()
    {
        var live = new MailPlan(new[] { "customer@y.de" }, Array.Empty<string>(), false, "Live", new[] { "customer@y.de" }, Array.Empty<string>());
        const string body = "Sehr geehrte Damen und Herren,\n\nanbei die Rechnung.\n\nMit freundlichen Grüßen";
        var sent = MailSender.BuildBody(body, "Sales invoice", "2026000350", "customer", live);
        Assert.False(sent.IsHtml);
        Assert.Equal(body, sent.Content);
    }

    [Fact]
    public void RedirectedPlainTextIsEscapedAndKeepsItsLines()
    {
        var plan = new MailPlan(new[] { "test@x.de" }, Array.Empty<string>(), true, "Test", new[] { "invoices@muster-lieferant.de" }, new[] { "sales-copy@muster-lieferant.de" });
        var sent = MailSender.BuildBody("Preis < 100 & mehr\r\n\r\nGrüße", "Sales invoice", "2026000376", "customer", plan);
        Assert.True(sent.IsHtml);
        Assert.Contains("Preis &lt; 100 &amp; mehr<br/>\n<br/>\nGr&#252;&#223;e", sent.Content);
        Assert.Contains("Copy to: sales-copy@muster-lieferant.de", sent.Content);
        Assert.DoesNotContain("***", sent.Content);
    }

    [Fact]
    public void CashDiscountsPreferTheListOverTheFlatFields()
    {
        var flat = AxDocument.Parse("""{ "cashDiscountDate": "2026-10-02", "cashDiscountPercent": "2" }""");
        var list = AxDocument.Parse("""{ "cashDiscountPercent": "9", "cashDiscounts": [ { "date": "2026-10-02", "percent": "3" }, { "days": 20, "percent": "2" } ] }""");
        var none = AxDocument.Parse("""{ "dueDate": "2026-10-22" }""");

        Assert.Equal(2m, Assert.Single(flat.EffectiveCashDiscounts()).Percent);
        Assert.Equal(new[] { 3m, 2m }, list.EffectiveCashDiscounts().Select(d => d.Percent));
        Assert.Empty(none.EffectiveCashDiscounts());
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("  https://contoso.sharepoint.com/sites/Finance//  ")]
    public void SiteUrlSpellingsResolveToOneTarget(string site)
    {
        var sp = new SharePointTargetOptions { Enabled = true, SiteUrl = site, Library = "OrderConfirmations" };
        sp.Normalize();
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance", sp.SiteUrl);
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance", sp.ResolveTarget(out _)!.SiteUrl);
    }

    [Theory]
    [InlineData("OrderConfirmations")]
    [InlineData("/OrderConfirmations/")]
    [InlineData("/OrderConfirmations")]
    [InlineData("OrderConfirmations/")]
    [InlineData(" \\OrderConfirmations\\ ")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/OrderConfirmations/Forms/AllItems.aspx")]
    public void LibrarySpellingsResolveToTheLibraryName(string library)
        => Assert.Equal("OrderConfirmations", SharePointTargetOptions.CleanLibrary(library));

    [Fact]
    public void DisplayNameLibraryAndFolderAreKeptButTidied()
    {
        Assert.Equal("Rechnungen & Gutschriften", SharePointTargetOptions.CleanLibrary(" Rechnungen & Gutschriften "));
        Assert.Equal("2026/Orders", SharePointTargetOptions.CleanFolder("/2026//Orders/"));
        Assert.Equal("2026/Orders", SharePointTargetOptions.CleanFolder("\\2026\\Orders\\"));
        Assert.Equal("", SharePointTargetOptions.CleanFolder("/"));
    }

    [Fact]
    public void LoadedRoutesAreNormalized()
    {
        var route = new DocumentRoute
        {
            DataAreaId = "002", Input = @"C:\in",
            SharePoint = new SharePointTargetOptions { SiteUrl = "https://contoso.sharepoint.com/sites/Finance/", Library = "/OrderConfirmations/", Folder = "/" },
        };
        route.Normalize();
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance", route.SharePoint.SiteUrl);
        Assert.Equal("OrderConfirmations", route.SharePoint.Library);
        Assert.Equal("", route.SharePoint.Folder);
    }
}
