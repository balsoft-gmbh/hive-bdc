using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Delivery;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Blind copies (Bcc) per document type and company, next to the Cc: the ERP's Bcc on Live, every address once.</summary>
[Collection("global secret vault")]
public sealed class CompanyMailBccTests : IDisposable
{
    private static MailSender Sender(params string[] testRecipients)
        => new(new MailOptions { Enabled = true, From = "rechnung@balsoft.example", RedirectTo = testRecipients.ToList() }, "tenant");

    private static readonly string[] CompanyCc = { "buchhaltung@contoso.example" };
    private static readonly string[] CompanyBcc = { "archiv@contoso.example", "chef@contoso.example" };

    // ── who gets the mail ─────────────────────────────────────────────────────

    [Fact]
    public void LiveMailAddsTheErpBccAndTheCompanyBcc()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "kunde@muster.example", "einkauf@muster.example",
            CompanyCc, "revision@muster.example", CompanyBcc);

        Assert.False(plan.Redirected);
        Assert.Equal(new[] { "kunde@muster.example" }, plan.To);
        Assert.Equal(new[] { "einkauf@muster.example", "buchhaltung@contoso.example" }, plan.Cc);
        Assert.Equal(new[] { "revision@muster.example", "archiv@contoso.example", "chef@contoso.example" }, plan.Bcc);
        Assert.Equal(new[] { "revision@muster.example" }, plan.OriginalBcc);
    }

    [Fact]
    public void ABccAddressAlreadyInToOrCcIsNotRepeated()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "Kunde@Muster.example", "einkauf@muster.example",
            CompanyCc, "KUNDE@muster.example; Einkauf@Muster.example", new[] { "BUCHHALTUNG@contoso.example", "archiv@contoso.example", "Archiv@CONTOSO.example" });

        Assert.Equal(new[] { "Kunde@Muster.example" }, plan.To);
        Assert.Equal(new[] { "einkauf@muster.example", "buchhaltung@contoso.example" }, plan.Cc);
        Assert.Equal(new[] { "archiv@contoso.example" }, plan.Bcc);
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("Dev")]
    [InlineData(null)]
    public void TestMailGetsOnlyTheCompanyBccAndNeverTheErpAddresses(string? environment)
    {
        var plan = Sender("test@balsoft.example", "tester@contoso.example").PlanRecipients(environment, "kunde@muster.example",
            "einkauf@muster.example", CompanyCc, "revision@muster.example", new[] { "Tester@contoso.example", "archiv@contoso.example" });

        Assert.True(plan.Redirected);
        Assert.Equal(new[] { "test@balsoft.example", "tester@contoso.example" }, plan.To);
        Assert.Equal(new[] { "buchhaltung@contoso.example" }, plan.Cc);
        Assert.Equal(new[] { "archiv@contoso.example" }, plan.Bcc);
        Assert.DoesNotContain(plan.To.Concat(plan.Cc).Concat(plan.Bcc), a => a.Contains("muster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheTestBannerNamesTheErpBcc()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Test", "kunde@muster.example", "einkauf@muster.example",
            null, "revision@muster.example", CompanyBcc);
        var body = MailSender.BuildBody("Guten Tag", "Sales invoice", "RE-1", "customer", plan);

        Assert.Contains("Copy to: einkauf@muster.example", body.Content);
        Assert.Contains("Blind copy to: revision@muster.example", body.Content);
        Assert.DoesNotContain("Blind copy to", MailSender.BuildBody("Guten Tag", "Sales invoice", "RE-1", "customer",
            Sender("test@balsoft.example").PlanRecipients("Test", "kunde@muster.example", null, null, null, CompanyBcc)).Content);
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Test")]
    public void WithoutAnyBccTheMailIsTheSameRequestAsBefore(string environment)
    {
        var sender = Sender("test@balsoft.example");
        var plan = sender.PlanRecipients(environment, "kunde@muster.example", "einkauf@muster.example", CompanyCc, "", Array.Empty<string>());
        var old = sender.PlanRecipients(environment, "kunde@muster.example", "einkauf@muster.example", CompanyCc);

        Assert.Empty(plan.Bcc);
        Assert.Equal(old.To, plan.To);
        Assert.Equal(old.Cc, plan.Cc);
        var message = MailSender.BuildMessage(plan, "Rechnung RE-1", new MailBody("x", false));
        Assert.False(message.ContainsKey("bccRecipients"));
        Assert.Equal(MailSender.BuildMessage(old, "Rechnung RE-1", new MailBody("x", false)).ToJsonString(), message.ToJsonString());
    }

    [Fact]
    public void TheGraphMessageCarriesTheBcc()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "kunde@muster.example", null, null, null, CompanyBcc);
        var message = MailSender.BuildMessage(plan, "Rechnung RE-1", new MailBody("x", false));

        var bcc = message["bccRecipients"]!.AsArray().Select(r => r!["emailAddress"]!["address"]!.GetValue<string>());
        Assert.Equal(CompanyBcc, bcc);
        Assert.False(message.ContainsKey("ccRecipients"));
    }

    [Fact]
    public void ALiveDocumentWithoutRecipientMailsNobodyNotEvenTheBlindCopies()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "", null, null, "revision@muster.example", CompanyBcc);
        Assert.Empty(plan.To);   // MailDocumentTarget skips the mail when To is empty: the copies follow the customer mail
    }

    [Fact]
    public void TheExportFieldIsRead()
    {
        var data = AxDocument.Parse("""{ "documentType": "SalesInvoice", "recipientEmailBcc": "revision@muster.example" }""");
        Assert.Equal("revision@muster.example", data.RecipientEmailBcc);
        Assert.Equal("", AxDocument.Parse("""{ "documentType": "SalesInvoice" }""").RecipientEmailBcc);
    }

    // ── settings ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheSettingsFileWritesBccOnlyWhenThereIsSomeAndOldFilesReadNone()
    {
        var with = new DocumentRoute { DataAreaId = "1000", Input = @"C:\Export", MailBcc = { "archiv@contoso.example", " ", "ARCHIV@contoso.example; chef@contoso.example" } };
        with.Normalize();
        Assert.Equal(new[] { "archiv@contoso.example", "chef@contoso.example" }, with.MailBcc);
        var node = SettingsFile.RouteNode(with);
        Assert.Equal(new[] { "archiv@contoso.example", "chef@contoso.example" }, node["MailBcc"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(SettingsFile.RouteNode(new DocumentRoute { DataAreaId = "2000", Input = @"C:\Export" }).ContainsKey("MailBcc"));

        var old = JsonSerializer.Deserialize<DocumentRoute>("""{ "DataAreaId": "2000", "Input": "C:\\Export", "MailCc": [ "a@contoso.example" ] }""")!;
        old.Normalize();
        Assert.Empty(old.MailBcc);
        Assert.Empty(with.Clone().MailBcc);   // an auto-registered type follows its own settings, as with Mail and Cc
    }

    // ── the local API ─────────────────────────────────────────────────────────

    private const string Start = """
        { "HiveBDC": {
          "PollSeconds": 30,
          "DocumentTypes": [ { "Id": "SalesInvoice", "DisplayName": "Sales invoice", "Party": "Customer", "Handler": "EInvoice",
            "Enabled": true, "Mail": true, "Companies": [
              { "DataAreaId": "1000", "Input": "/home/in/1000", "Output": "/home/out/1000", "MailCc": [ "x@contoso.example" ], "MailBcc": [ "b1@contoso.example", "b2@contoso.example" ] },
              { "DataAreaId": "2000", "Input": "/home/in/2000", "Output": "/home/out/2000", "MailBcc": [ "b3@fabrikam.example" ] } ] } ]
        } }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-mailbcc-" + Guid.NewGuid());
    private readonly MemoryETagBlob _blob = new();
    private readonly LocalStatusServer _api;

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    public CompanyMailBccTests()
    {
        Directory.CreateDirectory(_root);
        _blob.Put(Start);
        _api = new LocalStatusServer(new Monitor(new BDCOptions()), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            NullLogger<LocalStatusServer>.Instance, null, "9.9.9", Path.Combine(_root, "unused.json"), _root,
            new BlobSettingsDocument(_blob), new BlobLicenseStore(new MemoryETagBlob()),
            new InstanceTenant(() => throw new InvalidOperationException("no identity in tests")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<JsonObject> Get()
    {
        var call = new ApiCall("GET", "/config", new Dictionary<string, string>(), "");
        await _api.Route(call);
        Assert.Equal(200, call.Status);
        return JsonNode.Parse(Encoding.UTF8.GetString(call.Answer))!.AsObject();
    }

    private async Task<(int Status, string Answer)> Post(JsonObject config)
    {
        var body = (JsonObject)config.DeepClone();
        body["baseVersion"] = body["version"]?.GetValue<string>();
        var call = new ApiCall("POST", "/config", new Dictionary<string, string>(), body.ToJsonString()) { User = "admin@balsoft.example" };
        await _api.Route(call);
        return (call.Status, Encoding.UTF8.GetString(call.Answer));
    }

    private JsonArray StoredCompanies() => JsonNode.Parse(_blob.Text!)!["HiveBDC"]!["DocumentTypes"]![0]!["Companies"]!.AsArray();

    private static string[] List(JsonNode? company, string key) => company?[key]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? Array.Empty<string>();

    [Fact]
    public async Task EachCompanyKeepsItsOwnBccThroughGetAndSave()
    {
        var config = await Get();
        var companies = config["documentTypes"]![0]!["companies"]!.AsArray();
        Assert.Equal(new[] { "b1@contoso.example", "b2@contoso.example" }, List(companies[0], "mailBcc"));
        Assert.Equal(new[] { "b3@fabrikam.example" }, List(companies[1], "mailBcc"));

        companies[1]!["mailBcc"] = new JsonArray("b3@fabrikam.example", "neu@fabrikam.example; B3@FABRIKAM.example");
        var (status, answer) = await Post(config);

        Assert.True(status == 200, answer);
        Assert.Equal(new[] { "b1@contoso.example", "b2@contoso.example" }, List(StoredCompanies()[0], "MailBcc"));
        Assert.Equal(new[] { "x@contoso.example" }, List(StoredCompanies()[0], "MailCc"));
        Assert.Equal(new[] { "b3@fabrikam.example", "neu@fabrikam.example" }, List(StoredCompanies()[1], "MailBcc"));
    }

    [Fact]
    public async Task ADashboardThatSendsNoBccKeepsTheStoredOnes()
    {
        var config = await Get();
        foreach (var company in config["documentTypes"]![0]!["companies"]!.AsArray()) company!.AsObject().Remove("mailBcc");

        var (status, answer) = await Post(config);

        Assert.True(status == 200, answer);
        Assert.Equal(new[] { "b1@contoso.example", "b2@contoso.example" }, List(StoredCompanies()[0], "MailBcc"));
        Assert.Equal(new[] { "b3@fabrikam.example" }, List(StoredCompanies()[1], "MailBcc"));
    }

    [Fact]
    public async Task AnEmptiedBccListIsSavedWithoutTheKey()
    {
        var config = await Get();
        config["documentTypes"]![0]!["companies"]![1]!["mailBcc"] = new JsonArray();

        var (status, answer) = await Post(config);

        Assert.True(status == 200, answer);
        Assert.False(StoredCompanies()[1]!.AsObject().ContainsKey("MailBcc"));
    }

    [Fact]
    public async Task AnInvalidBccAddressIsRefusedAndNamed()
    {
        var config = await Get();
        config["documentTypes"]![0]!["companies"]![1]!["mailBcc"] = new JsonArray("b3@fabrikam.example", "kein-mail");
        string before = _blob.Text!;

        var (status, answer) = await Post(config);

        Assert.Equal(400, status);
        var errors = JsonNode.Parse(answer)!["errors"]!.AsArray().Select(e => e!.GetValue<string>());
        Assert.Contains("SalesInvoice › 2000: the Bcc address 'kein-mail' is not a valid e-mail address.", errors);
        Assert.Equal(before, _blob.Text);
    }
}