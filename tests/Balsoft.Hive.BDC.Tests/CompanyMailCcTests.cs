using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Delivery;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Mail copies (Cc) per document type and company: Cc on Live and on test mails, every address once.</summary>
[Collection("global secret vault")]
public sealed class CompanyMailCcTests : IDisposable
{
    private static MailSender Sender(params string[] testRecipients)
        => new(new MailOptions { Enabled = true, From = "rechnung@balsoft.example", RedirectTo = testRecipients.ToList() }, "tenant");

    // ── who gets the mail ─────────────────────────────────────────────────────

    [Fact]
    public void LiveMailKeepsTheCustomerAndAddsTheCompanyAsCc()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "kunde@muster.example", "einkauf@muster.example",
            new[] { "buchhaltung@contoso.example", "anna@contoso.example" });

        Assert.False(plan.Redirected);
        Assert.Equal(new[] { "kunde@muster.example" }, plan.To);
        Assert.Equal(new[] { "einkauf@muster.example", "buchhaltung@contoso.example", "anna@contoso.example" }, plan.Cc);
    }

    [Fact]
    public void EveryAddressGetsTheMailOnceWhatEverItsCase()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "Kunde@Muster.example", "einkauf@muster.example",
            new[] { "kunde@muster.example", "EINKAUF@muster.example", "anna@contoso.example", "Anna@CONTOSO.example" });

        Assert.Equal(new[] { "Kunde@Muster.example" }, plan.To);
        Assert.Equal(new[] { "einkauf@muster.example", "anna@contoso.example" }, plan.Cc);
    }

    [Fact]
    public void TestMailGoesToTheTestRecipientsWithTheCompanyAsCcAndNeverToTheCustomer()
    {
        var plan = Sender("test@balsoft.example", "tester@contoso.example").PlanRecipients("Test", "kunde@muster.example", "einkauf@muster.example",
            new[] { "Tester@contoso.example", "anna@contoso.example" });

        Assert.True(plan.Redirected);
        Assert.Equal(new[] { "test@balsoft.example", "tester@contoso.example" }, plan.To);
        Assert.Equal(new[] { "anna@contoso.example" }, plan.Cc);
        Assert.DoesNotContain(plan.To.Concat(plan.Cc), a => a.Contains("muster", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Test")]
    [InlineData(null)]
    public void ACompanyWithoutCcMailsAsBefore(string? environment)
    {
        var sender = Sender("test@balsoft.example");
        var without = sender.PlanRecipients(environment, "kunde@muster.example", "einkauf@muster.example", Array.Empty<string>());
        var old = sender.PlanRecipients(environment, "kunde@muster.example", "einkauf@muster.example");
        Assert.Equal(old.To, without.To);
        Assert.Equal(old.Cc, without.Cc);
        Assert.Equal(environment == "Live" ? new[] { "einkauf@muster.example" } : Array.Empty<string>(), without.Cc);
    }

    [Fact]
    public void ALiveDocumentWithoutRecipientMailsNobodyNotEvenTheCopies()
    {
        var plan = Sender("test@balsoft.example").PlanRecipients("Live", "", null, new[] { "anna@contoso.example" });
        Assert.Empty(plan.To);   // MailDocumentTarget skips the mail when To is empty: the copies follow the customer mail
    }

    // ── settings ──────────────────────────────────────────────────────────────

    [Fact]
    public void TypedTextBecomesOneListInTheAppAndTheService()
    {
        Assert.Equal(new[] { "a@contoso.example", "b@contoso.example", "c@contoso.example" },
            MailAddressText.Parse(" a@contoso.example; b@contoso.example,c@contoso.example\r\nA@CONTOSO.example ;; "));
        Assert.Empty(MailAddressText.Parse(null));
        Assert.Equal("a@contoso.example; b@contoso.example", MailAddressText.Join(new[] { "a@contoso.example", "b@contoso.example" }));
    }

    [Fact]
    public void TheSettingsFileWritesCcOnlyWhenThereIsSomeAndOldFilesReadNone()
    {
        var with = new DocumentRoute { DataAreaId = "1000", Input = @"C:\Export", MailCc = { "anna@contoso.example", " ", "ANNA@contoso.example; bob@contoso.example" } };
        with.Normalize();
        Assert.Equal(new[] { "anna@contoso.example", "bob@contoso.example" }, with.MailCc);
        var node = SettingsFile.RouteNode(with);
        Assert.Equal(new[] { "anna@contoso.example", "bob@contoso.example" }, node["MailCc"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(SettingsFile.RouteNode(new DocumentRoute { DataAreaId = "2000", Input = @"C:\Export" }).ContainsKey("MailCc"));

        var old = JsonSerializer.Deserialize<DocumentRoute>("""{ "DataAreaId": "2000", "Input": "C:\\Export" }""")!;
        old.Normalize();
        Assert.Empty(old.MailCc);
        Assert.Empty(with.Clone().MailCc);   // an auto-registered type follows its own settings, as with Mail
    }

    // ── the local API ─────────────────────────────────────────────────────────

    private const string Start = """
        { "HiveBDC": {
          "PollSeconds": 30,
          "DocumentTypes": [ { "Id": "SalesInvoice", "DisplayName": "Sales invoice", "Party": "Customer", "Handler": "EInvoice",
            "Enabled": true, "Mail": true, "Companies": [
              { "DataAreaId": "1000", "Input": "/home/in/1000", "Output": "/home/out/1000", "MailCc": [ "x@contoso.example", "y@contoso.example" ] },
              { "DataAreaId": "2000", "Input": "/home/in/2000", "Output": "/home/out/2000", "MailCc": [ "z@fabrikam.example" ] } ] } ]
        } }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-mailcc-" + Guid.NewGuid());
    private readonly MemoryETagBlob _blob = new();
    private readonly LocalStatusServer _api;

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    public CompanyMailCcTests()
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

    private static string[] Cc(JsonNode? company) => company?["MailCc"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? Array.Empty<string>();

    [Fact]
    public async Task EachCompanyKeepsItsOwnCcThroughGetAndSave()
    {
        var config = await Get();
        var companies = config["documentTypes"]![0]!["companies"]!.AsArray();
        Assert.Equal(new[] { "x@contoso.example", "y@contoso.example" }, companies[0]!["mailCc"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "z@fabrikam.example" }, companies[1]!["mailCc"]!.AsArray().Select(n => n!.GetValue<string>()));

        companies[1]!["mailCc"] = new JsonArray("z@fabrikam.example", "neu@fabrikam.example; Z@FABRIKAM.example");
        var (status, answer) = await Post(config);

        Assert.True(status == 200, answer);
        Assert.Equal(new[] { "x@contoso.example", "y@contoso.example" }, Cc(StoredCompanies()[0]));
        Assert.Equal(new[] { "z@fabrikam.example", "neu@fabrikam.example" }, Cc(StoredCompanies()[1]));
    }

    [Fact]
    public async Task ADashboardThatSendsNoCcKeepsTheStoredOnes()
    {
        var config = await Get();
        foreach (var company in config["documentTypes"]![0]!["companies"]!.AsArray()) company!.AsObject().Remove("mailCc");

        var (status, answer) = await Post(config);

        Assert.True(status == 200, answer);
        Assert.Equal(new[] { "x@contoso.example", "y@contoso.example" }, Cc(StoredCompanies()[0]));
        Assert.Equal(new[] { "z@fabrikam.example" }, Cc(StoredCompanies()[1]));
    }

    [Fact]
    public async Task AnInvalidCcAddressIsRefusedAndNamed()
    {
        var config = await Get();
        config["documentTypes"]![0]!["companies"]![1]!["mailCc"] = new JsonArray("z@fabrikam.example", "kein-mail");
        string before = _blob.Text!;

        var (status, answer) = await Post(config);

        Assert.Equal(400, status);
        var errors = JsonNode.Parse(answer)!["errors"]!.AsArray().Select(e => e!.GetValue<string>());
        Assert.Contains("SalesInvoice › 2000: the Cc address 'kein-mail' is not a valid e-mail address.", errors);
        Assert.Equal(before, _blob.Text);
    }
}
