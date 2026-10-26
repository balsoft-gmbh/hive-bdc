using System.Text.Json;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class DocumentProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-tests-" + Guid.NewGuid());
    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    private DocumentRoute Route(string company)
    {
        string input = Path.Combine(_root, company);
        Directory.CreateDirectory(input);
        return new DocumentRoute { DataAreaId = company, Input = input, Output = Path.Combine(input, "out") };
    }

    private static BDCOptions Options(params DocumentRoute[] routes)
    {
        var options = new BDCOptions();
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "PurchaseOrder", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = routes.ToList(),
        });
        options.Normalize();
        return options;
    }

    private static string Pair(DocumentRoute route, string id, string? company, bool pdf = true)
    {
        string path = Path.Combine(route.Input, id + "_1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { documentType = "PurchaseOrder", dataAreaId = company }));
        if (pdf) File.WriteAllText(Path.ChangeExtension(path, ".pdf"), "Synthetic pass-through fixture");
        return path;
    }

    private DocumentProcessor.ScanResult Scan(BDCOptions options)
        => new DocumentProcessor(NullLogger.Instance,
            new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), null)
            .ScanOnce(options, License, null, null);

    [Fact]
    public void ExplicitCompanyUsesItsOwnRouteEvenFromAnotherFolder()
    {
        var a = Route("A"); var b = Route("B");
        Pair(a, "order", "B");
        Assert.Equal(1, Scan(Options(a, b)).Processed);
        Assert.False(File.Exists(Path.Combine(a.Output, "order.pdf")));
        Assert.True(File.Exists(Path.Combine(b.Output, "order.pdf")));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    public void UnknownOrAmbiguousCompanyIsHeld(string? company)
    {
        var a = Route("A"); var b = Route("B"); b.Input = a.Input;
        string json = Pair(a, "order", company);
        var result = Scan(Options(a, b));
        Assert.Equal(1, result.Held);
        Assert.Equal(0, result.Processed);
        Assert.True(File.Exists(json));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnavailableRequiredDeliveryIsHeldAndResumesAfterConfigurationRepair(bool sharePoint)
    {
        var a = Route("A"); var options = Options(a);
        a.SharePoint.Enabled = sharePoint;
        options.Mail.Enabled = !sharePoint;
        options.DocumentTypes[0].Mail = !sharePoint;
        string json = Pair(a, "order", "A");
        Assert.Equal(1, Scan(options).Held);
        Assert.True(File.Exists(json));
        Assert.False(Directory.Exists(a.Output));
        // Explicitly disabling delivery is a supported configuration repair.
        a.SharePoint.Enabled = false; options.Mail.Enabled = false;
        Assert.Equal(1, Scan(options).Processed);
    }

    [Fact]
    public void DisabledTypeFolderIsNeitherCreatedNorScanned()
    {
        // Customer install 2026-10-11: disabled types kept the installer default folder and every poll
        // failed on it. A folder only a disabled type points at must not even be touched.
        var a = Route("A"); var options = Options(a);
        string quotes = Path.Combine(_root, "quotes-not-created");
        var quoteType = new DocumentTypeOptions { Id = "SalesQuotation", Enabled = false, Mail = false };
        quoteType.Companies.Add(new DocumentRoute { DataAreaId = "A", Input = quotes });
        options.DocumentTypes.Add(quoteType);
        Pair(a, "order", "A");

        var result = Scan(options);

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Errors);
        Assert.False(Directory.Exists(quotes));
        Assert.Single(options.ScanFolders());
    }

    [Fact]
    public void DisabledTypePairInASharedFolderGoesToErrorWhileTheEnabledTypeFlows()
    {
        var a = Route("A"); var options = Options(a);
        var quoteType = new DocumentTypeOptions { Id = "SalesQuotation", Enabled = false, Mail = false };
        quoteType.Companies.Add(new DocumentRoute { DataAreaId = "A", Input = a.Input });
        options.DocumentTypes.Add(quoteType);
        Pair(a, "order", "A");
        string quote = Path.Combine(a.Input, "quote_1.json");
        File.WriteAllText(quote, JsonSerializer.Serialize(new { documentType = "SalesQuotation", dataAreaId = "A" }));
        File.WriteAllText(Path.ChangeExtension(quote, ".pdf"), "Synthetic quotation PDF");

        var result = Scan(options);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Errors);
        Assert.True(File.Exists(Path.Combine(a.Input, "error", "quote_1.json")));
        Assert.False(File.Exists(quote));
        Assert.False(File.Exists(Path.Combine(a.Input, "out", "SalesQuotation", "quote.pdf")));
    }

    /// <summary>Customer report 2026-10-26: removed built-ins came back; one the ERP still sends must not register again.</summary>
    [Fact]
    public void RemovedBuiltInTypeGoesToErrorAndIsNotRegisteredAgain()
    {
        var a = Route("A"); var options = Options(a);
        Assert.DoesNotContain(options.DocumentTypes, t => t.Id == "SalesQuotation");
        Pair(a, "order", "A");
        RawPair(a.Input, "quote_1", new { documentType = "SalesQuotation", dataAreaId = "A" });

        var result = Scan(options);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Errors);
        Assert.True(File.Exists(Path.Combine(a.Input, "error", "quote_1.json")));
        Assert.False(Directory.Exists(Path.Combine(a.Input, "out", "SalesQuotation")));
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
        Assert.DoesNotContain(options.DocumentTypes, t => t.Id == "SalesQuotation");
    }

    [Fact]
    public void LatePdfIsProcessedOnNextScan()
    {
        var a = Route("A"); var options = Options(a);
        string json = Pair(a, "order", "A", pdf: false);
        Assert.Equal(1, Scan(options).Held);
        Assert.True(File.Exists(json));
        File.WriteAllText(Path.ChangeExtension(json, ".pdf"), "Synthetic PDF");
        Assert.Equal(1, Scan(options).Processed);
    }

    [Fact]
    public void IncompleteJsonIsHeldUntilTheWriterFinishes()
    {
        var a = Route("A"); var options = Options(a);
        string json = Pair(a, "order", "A");
        File.WriteAllText(json, "{");
        Assert.Equal(1, Scan(options).Held);
        Pair(a, "order", "A");
        Assert.Equal(1, Scan(options).Processed);
    }

    [Fact]
    public void LockedPdfIsHeldUntilReleased()
    {
        var a = Route("A"); var options = Options(a);
        string json = Pair(a, "order", "A");
        using (var writer = new FileStream(Path.ChangeExtension(json, ".pdf"), FileMode.Open, FileAccess.Write, FileShare.None))
            Assert.Equal(1, Scan(options).Held);
        Assert.Equal(1, Scan(options).Processed);
    }

    [Fact]
    public void BrokenQuarantineDoesNotBlockOtherDocumentsOrFolders()
    {
        var a = Route("A"); var b = Route("B");
        string broken = Pair(a, "a-broken", "A");
        File.WriteAllText(broken, "{");
        File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(Path.Combine(a.Input, "error"), "Blocks creation of the error directory");
        Pair(a, "z-healthy", "A"); Pair(b, "healthy", "B");
        var result = Scan(Options(a, b));
        Assert.Equal(1, result.Errors);
        Assert.Equal(2, result.Processed);
    }

    [Fact]
    public void UnusableFolderDoesNotBlockHealthyFolder()
    {
        var a = Route("A"); var b = Route("B");
        a.Input = Path.Combine(_root, "blocked"); File.WriteAllText(a.Input, "Not a directory");
        Pair(b, "healthy", "B");
        var result = Scan(Options(a, b));
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Processed);
    }

    private static void RawPair(string folder, string fileStem, object json)
    {
        string path = Path.Combine(folder, fileStem + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(json));
        File.WriteAllText(Path.ChangeExtension(path, ".pdf"), "Synthetic pass-through fixture");
    }

    [Fact]
    public void DocumentsSharingAnIdKeepTheirOwnFileByDocumentNumber()
    {
        var a = Route("A");
        RawPair(a.Input, "4500001234_1", new { documentType = "PurchaseOrder", documentNumber = "4500001234-1" });
        RawPair(a.Input, "4500001234_2", new { documentType = "PurchaseOrder", documentNumber = "4500001234-2" });
        Assert.Equal(2, Scan(Options(a)).Processed);
        Assert.True(File.Exists(Path.Combine(a.Output, "4500001234-1.pdf")));
        Assert.True(File.Exists(Path.Combine(a.Output, "4500001234-2.pdf")));
        Assert.False(File.Exists(Path.Combine(a.Output, "4500001234.pdf")));
    }

    [Fact]
    public void OutputNameIsMadeSafeAndFallsBackToTheId()
    {
        Assert.Equal("AB_12_3", OutputFileNames.Stem("AB/12:3", "id"));
        Assert.Equal("id", OutputFileNames.Stem("  ", "id"));
        Assert.Equal("id", OutputFileNames.Stem("...", "id"));
    }

    /// <summary>Customer case 2026-10-06: a company B order whose type was auto-registered from company A's folder.</summary>
    private (DocumentRoute A, DocumentRoute B, BDCOptions Options) DiscoveredForAOnly(bool discovered)
    {
        var a = Route("A"); var b = Route("B");
        var options = Options(b);
        var german = new DocumentTypeOptions { Id = "Bestellung", Handler = DocumentHandlerKind.Pdf, Mail = false, Discovered = discovered };
        german.Companies.Add(a.Clone());
        options.DocumentTypes.Add(german);
        options.Normalize();
        RawPair(b.Input, "9000000123_1", new { documentType = "Bestellung", documentNumber = "4500001234-2" });
        return (a, b, options);
    }

    [Fact]
    public void WithoutDataAreaIdTheFolderCompanyWinsAndADiscoveredTypeGetsThatCompany()
    {
        var (a, b, options) = DiscoveredForAOnly(discovered: true);
        Assert.Equal(1, Scan(options).Processed);
        Assert.True(File.Exists(Path.Combine(b.Input, "out", "Bestellung", "4500001234-2.pdf")));
        Assert.False(Directory.Exists(Path.Combine(a.Input, "out", "Bestellung")));
        Assert.Contains(options.DocumentTypes.Single(t => t.Id == "Bestellung").Companies, r => r.DataAreaId == "B");
    }

    [Fact]
    public void WithoutDataAreaIdAConfiguredTypeMissingTheFolderCompanyIsHeld()
    {
        var (a, b, options) = DiscoveredForAOnly(discovered: false);
        var result = Scan(options);
        Assert.Equal(1, result.Held);
        Assert.True(File.Exists(Path.Combine(b.Input, "9000000123_1.json")));
        Assert.False(Directory.Exists(Path.Combine(a.Input, "out", "Bestellung")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
