using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The HTTPS source of an Azure instance (failure case 21), through the web host on loopback.</summary>
public sealed class HttpsInboundTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-https-in-" + Guid.NewGuid());
    private readonly HttpClient _http = new();
    private BDCOptions _options = null!;
    private DocumentRoute _route = null!;
    private AzureWebHost _host = null!;
    private bool _signInOn = true;

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    public async Task InitializeAsync()
    {
        _route = new DocumentRoute
        {
            DataAreaId = "1000", Input = Path.Combine(_root, "in"), Output = Path.Combine(_root, "in", "out"),
            Source = new SourceOptions { Kind = "https", Connection = "erp-in" },
        };
        _options = new BDCOptions
        {
            Connections = { new ConnectionOptions { Id = "erp-in", Kind = "https", Secret = "key-123" } },
            DocumentTypes = { new DocumentTypeOptions { Id = "SalesInvoice", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { _route } } },
        };
        _options.Normalize();
        var api = new LocalStatusServer(new Monitor(_options), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            NullLogger<LocalStatusServer>.Instance, null, "9.9.9", Path.Combine(_root, "unused.json"), _root);
        _host = new AzureWebHost(api, NullLogger.Instance, "9.9.9", null, () => _signInOn, new IPEndPoint(IPAddress.Loopback, 0), () => _options);
        await _host.StartAsync(CancellationToken.None);
        for (int i = 0; i < 600 && _host.BoundPort == 0; i++) await Task.Delay(50);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
        _http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Body(string number = "RE-7", bool withPdf = true)
        => new JsonObject
        {
            ["document"] = new JsonObject { ["documentType"] = "SalesInvoice", ["dataAreaId"] = "1000", ["documentNumber"] = number },
            ["pdf"] = withPdf ? Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.4 posted")) : null,
        }.ToJsonString();

    private async Task<(HttpStatusCode Status, JsonNode? Answer)> Post(string path, string body, string? key = "key-123")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_host.BoundPort}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("x-api-key", key);
        using var response = await _http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private string[] Staged() => Directory.Exists(_route.Input) ? Directory.GetFiles(_route.Input).Select(Path.GetFileName).OrderBy(n => n).ToArray()! : Array.Empty<string>();

    [Fact]
    public async Task APostedDocumentIsStagedAndTheSameOneTwiceIsNotCopiedAgain()
    {
        var first = await Post("/inbound/SalesInvoice/1000", Body());
        Assert.Equal((HttpStatusCode.Accepted, "accepted", "RE-7"), (first.Status, (string)first.Answer!["status"]!, (string)first.Answer["id"]!));
        Assert.Equal(new[] { "RE-7_https.json", "RE-7_https.pdf" }, Staged());
        Assert.Equal("%PDF-1.4 posted", File.ReadAllText(Path.Combine(_route.Input, "RE-7_https.pdf")));
        Assert.Equal("RE-7", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(_route.Input, "RE-7_https.json")))!["documentNumber"]!);

        var again = await Post("/inbound/SalesInvoice/1000", Body());
        Assert.Equal((HttpStatusCode.OK, "already received"), (again.Status, (string)again.Answer!["status"]!));
        Assert.Equal(2, Staged().Length);
    }

    [Fact]
    public async Task AMissingOrWrongKeyIsRefusedAndNothingIsStored()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/inbound/SalesInvoice/1000", Body(), key: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/inbound/SalesInvoice/1000", Body(), key: "key-124")).Status);
        Assert.Empty(Staged());
    }

    [Fact]
    public async Task AnUnknownCompanyOrTypeAndABodyThatIsNotTheShapeAreRefused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/inbound/SalesInvoice/2000", Body())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/inbound/Credit/1000", Body())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/inbound/SalesInvoice", Body())).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/inbound/SalesInvoice/1000", "{ not json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/inbound/SalesInvoice/1000", """{"pdf":""}""")).Status);
        Assert.Empty(Staged());
    }

    [Fact]
    public void ABodyOverTheLimitIsRefusedBeforeItIsRead()
    {
        bool read = false;
        var answer = new HttpsInbound(NullLogger.Instance).Receive(_options, "SalesInvoice", "1000",
            name => name == "x-api-key" ? "key-123" : null, null, HttpsInbound.MaxBytes + 1, () => { read = true; return ""; });

        Assert.Equal(413, answer.Status);
        Assert.False(read);
    }

    [Fact]
    public async Task TheKeyNotTheSignInGuardsThePathEvenWhenSignInIsOff()
    {
        _signInOn = false;

        Assert.Equal(HttpStatusCode.Accepted, (await Post("/inbound/SalesInvoice/1000?id=ERP-42", Body())).Status);
        Assert.Contains("ERP-42_https.json", Staged());
    }

    [Fact]
    public async Task ThePostedDocumentGoesThroughThePipeline()
    {
        await Post("/inbound/SalesInvoice/1000", Body("RE-9"));
        var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
        var processor = new DocumentProcessor(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), ledger);
        var license = new LicenseResult { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

        Assert.Equal(1, processor.ScanOnce(_options, license, null, null).Processed);
        Assert.True(File.Exists(Path.Combine(_route.Output, "RE-9.pdf")));
    }

    [Fact]
    public void AzureAcceptsAnHttpsSource()
    {
        _route.Targets.Add(new TargetOptions { Kind = "https", Connection = "erp-in" });
        _route.Input = "/tmp/hivebdc/https";
        Assert.Contains("connection 'erp-in' is kept in the settings", Assert.Single(AzureSettingsCheck.Problems(_options)));

        _options.Connections[0].Secret = "kv:connection-erp-in";
        Assert.Empty(AzureSettingsCheck.Problems(_options));
    }
}