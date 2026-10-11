using Balsoft.Hive.BDC.Configuration;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class SharePointFieldImportTests
{
    private sealed class FakeRoute(string dataAreaId) : ISharePointFieldImportRoute
    {
        public string DataAreaId { get; } = dataAreaId;
        public bool Enabled { get; set; }
        public string SiteUrl { get; set; } = "https://old.sharepoint.com/sites/old";
        public string Library { get; set; } = "OldLibrary";
        public string Folder { get; set; } = "keep/me";
        public Dictionary<string, string> Fields { get; set; } = new() { ["Title"] = "{DocumentNumber}", ["Stale"] = "x" };

        public void ApplySharePoint(SharePointFieldImportTarget t)
        {
            if (t.Enabled is bool on) Enabled = on;
            if (t.SiteUrl is not null) SiteUrl = t.SiteUrl;
            if (t.Library is not null) Library = t.Library;
            if (t.Folder is not null) Folder = t.Folder;
            if (t.Fields is not null) Fields = SharePointFieldImport.CleanFields(t.Fields);
        }
    }

    private const string ValidFile = """
        {
          "format": "HiveBDC.SharePointFields",
          "version": 1,
          "description": "test",
          "documentTypes": [
            { "id": "salesinvoice",
              "companies": [
                { "dataAreaId": "001",
                  "sharePoint": { "siteUrl": "https://contoso.sharepoint.com/sites/A", "library": "SalesInvoices",
                                  "fields": { " Title ": "{invoiceNumber}", "CompanyCode": "001" } } },
                { "dataAreaId": "002", "sharePoint": { "library": "X", "fields": {} } }
              ] },
            { "id": "PaymentAdvice", "companies": [ { "dataAreaId": "001", "sharePoint": { "fields": { "Title": "{documentNumber}" } } } ] }
          ]
        }
        """;

    [Fact]
    public void ValidFileParsesAndAppliesToMatchingEntriesOnly()
    {
        Assert.True(SharePointFieldImport.TryParse(ValidFile, out var file, out var errors), string.Join("\n", errors));

        var r001 = new FakeRoute("001");
        var scopes = new[] { new SharePointFieldImportScope("SalesInvoice", new[] { r001 }) };
        var result = SharePointFieldImport.Apply(file!, scopes);

        Assert.Equal("https://contoso.sharepoint.com/sites/A", r001.SiteUrl);
        Assert.Equal("SalesInvoices", r001.Library);
        Assert.Equal("keep/me", r001.Folder);              // absent in the file => kept
        Assert.False(r001.Enabled);                        // absent in the file => kept
        Assert.Equal(new Dictionary<string, string> { ["Title"] = "{invoiceNumber}", ["CompanyCode"] = "001" }, r001.Fields);

        Assert.Single(result.Applied);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Contains("company entry '002'"));
        Assert.Contains(result.Skipped, s => s.Contains("'PaymentAdvice' is not configured"));
    }

    [Fact]
    public void ApplyToOneCompanyTouchesOnlyThatEntry()
    {
        Assert.True(SharePointFieldImport.TryParse(ValidFile, out var file, out _));
        var r001 = new FakeRoute("001");

        var result = SharePointFieldImport.ApplyTo(file!, " SalesInvoice ", r001);

        Assert.Single(result.Applied);
        Assert.Empty(result.Skipped);                      // the other entries in the file are not "skipped", they are not in scope
        Assert.Equal("SalesInvoices", r001.Library);
        Assert.Equal(new Dictionary<string, string> { ["Title"] = "{invoiceNumber}", ["CompanyCode"] = "001" }, r001.Fields);
    }

    [Fact]
    public void ApplyToReportsAMissingEntryWithWhatTheFileContains()
    {
        Assert.True(SharePointFieldImport.TryParse(ValidFile, out var file, out _));
        var r = new FakeRoute("agr");

        var result = SharePointFieldImport.ApplyTo(file!, "SalesInvoice", r);

        Assert.Empty(result.Applied);
        var note = Assert.Single(result.Skipped);
        Assert.Contains("no entry for SalesInvoice / agr", note);
        Assert.Contains("salesinvoice / 001, salesinvoice / 002, PaymentAdvice / 001", note);
        Assert.Equal("OldLibrary", r.Library);             // untouched
    }

    [Fact]
    public void EmptyFieldsObjectClearsTheTemplates()
    {
        Assert.True(SharePointFieldImport.TryParse(ValidFile, out var file, out _));
        var r002 = new FakeRoute("002");
        SharePointFieldImport.Apply(file!, new[] { new SharePointFieldImportScope("SalesInvoice", new[] { r002 }) });
        Assert.Empty(r002.Fields);
        Assert.Equal("X", r002.Library);
    }

    [Fact]
    public void AbsentFieldsKeepTheTemplates()
    {
        const string json = """
            { "format": "HiveBDC.SharePointFields", "version": 1,
              "documentTypes": [ { "id": "PurchaseOrder", "companies": [ { "dataAreaId": "002", "sharePoint": { "enabled": true } } ] } ] }
            """;
        Assert.True(SharePointFieldImport.TryParse(json, out var file, out _));
        var r = new FakeRoute("002");
        SharePointFieldImport.Apply(file!, new[] { new SharePointFieldImportScope("PurchaseOrder", new[] { r }) });
        Assert.True(r.Enabled);
        Assert.Equal(2, r.Fields.Count);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{ "format": "Other", "version": 1, "documentTypes": [] }""", "\"format\"")]
    [InlineData("""{ "format": "HiveBDC.SharePointFields", "version": 2, "documentTypes": [ { "id": "A", "companies": [ { "dataAreaId": "1", "sharePoint": {} } ] } ] }""", "\"version\" 2")]
    [InlineData("""{ "format": "HiveBDC.SharePointFields", "version": 1, "documentTypes": [ { "id": "A", "companies": [ { "dataAreaId": "1", "sharePoint": { "siteUrl": "http://x" } } ] } ] }""", "valid https URL")]
    [InlineData("""{ "format": "HiveBDC.SharePointFields", "version": 1, "documentTypes": [ { "id": "A", "companies": [ { "dataAreaId": "1", "sharePoint": {} }, { "dataAreaId": "1", "sharePoint": {} } ] } ] }""", "listed twice")]
    [InlineData("""{ "format": "HiveBDC.SharePointFields", "version": 1, "documentTypes": [ { "id": "A", "companies": [ { "dataAreaId": "", "sharePoint": {} } ] } ] }""", "no \"dataAreaId\"")]
    public void InvalidFilesAreRejectedWithAReason(string json, string expected)
    {
        Assert.False(SharePointFieldImport.TryParse(json, out var file, out var errors));
        Assert.Null(file);
        Assert.Contains(errors, e => e.Contains(expected));
    }
}
