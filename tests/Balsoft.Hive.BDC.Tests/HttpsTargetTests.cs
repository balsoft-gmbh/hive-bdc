using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The HTTPS target (failure case 20) through a full scan against a fake endpoint.</summary>
public sealed class HttpsTargetTests : IDisposable
{
    private const string TriggerUrl = "https://prod-12.westeurope.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?api-version=2016-10-01&sig=SECRETSIG";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-https-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private readonly FakeEndpoint _endpoint = new();
    private static readonly LicenseResult License = new() { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

    public HttpsTargetTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeEndpoint : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Url, string? Key, string Body)> Calls { get; } = new();

        public Func<HttpResponseMessage> Answer { get; set; } = () => Json(HttpStatusCode.OK, """{"id":"INV-1"}""");

        public static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string? key = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
            Calls.Add((request.Method, request.RequestUri!, key, body));
            return Answer();
        }

        public JsonObject LastBody => (JsonObject)JsonNode.Parse(Calls.Last().Body)!;
    }

    private DocumentRoute Route()
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        return new DocumentRoute
        {
            DataAreaId = "A", Input = input, Output = Path.Combine(input, "out"),
            Targets = { new TargetOptions { Kind = "https", Connection = "api", Settings = { ["Path"] = "/invoices" }, Fields = { ["number"] = "{documentNumber}", ["date"] = "{documentDate:date}" } } },
        };
    }

    private static BDCOptions Options(DocumentRoute route, ConnectionOptions? connection = null)
    {
        var options = new BDCOptions();
        options.Connections.Add(connection ?? new ConnectionOptions
        {
            Id = "api", Kind = "https", Secret = "key-123", Settings = { ["Url"] = "https://api.example/hooks" },
        });
        options.DocumentTypes.Add(new DocumentTypeOptions { Id = "PurchaseOrder", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { route } });
        options.Normalize();
        return options;
    }

    private static void Pair(DocumentRoute route)
    {
        string path = Path.Combine(route.Input, "order_1.json");
        File.WriteAllText(path, """{"documentType":"PurchaseOrder","dataAreaId":"A","documentNumber":"PO-7","documentDate":"03.11.2026"}""");
        File.WriteAllText(Path.ChangeExtension(path, ".pdf"), "%PDF-1.4 synthetic");
    }

    private DocumentProcessor Processor()
    {
        var connectors = ConnectorRegistry.BuiltIn();
        connectors.RegisterTarget("https", runtime => new HttpsDocumentTarget(runtime.Options, _endpoint));
        return new DocumentProcessor(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger, connectors);
    }

    [Fact]
    public void TheDocumentIsPostedAsJsonWithItsFileFieldsAndKeyAndTheAnsweredIdIsKept()
    {
        var route = Route();
        Pair(route);

        Assert.Equal(1, Processor().ScanOnce(Options(route), License, null, null).Processed);

        var call = Assert.Single(_endpoint.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.example/hooks/invoices", call.Url.ToString());
        Assert.Equal("key-123", call.Key);
        var body = _endpoint.LastBody;
        Assert.Equal(("PO-7", "PurchaseOrder", "A", "PO-7.pdf", "application/pdf"),
            ((string)body["documentNumber"]!, (string)body["documentType"]!, (string)body["dataAreaId"]!, (string)body["fileName"]!, (string)body["contentType"]!));
        Assert.Equal("%PDF-1.4 synthetic", Encoding.UTF8.GetString(Convert.FromBase64String((string)body["content"]!)));
        Assert.Equal("PO-7", (string)body["fields"]!["number"]!);
        Assert.Equal("2026-11-03", (string)body["fields"]!["date"]!);
        Assert.Null(body["previousId"]);
        Assert.Equal("Sent PO-7.pdf to https://api.example/hooks/invoices (HTTP 200, id INV-1).",
            _ledger.Events("PO-7", 50).Single(e => e.Step == "delivered").Message);
    }

    [Fact]
    public void ARepeatCarriesTheIdTheEndpointAnsweredTheFirstTime()
    {
        var route = Route();
        Pair(route);
        Processor().ScanOnce(Options(route), License, null, null);
        Pair(route);

        Processor().ScanOnce(Options(route), License, null, null);

        Assert.Equal(2, _endpoint.Calls.Count);
        Assert.Equal("INV-1", (string)_endpoint.LastBody["previousId"]!);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void ABusyEndpointLeavesTheDocumentWaitingAndTheNextScanSendsIt(HttpStatusCode status)
    {
        var route = Route();
        Pair(route);
        _endpoint.Answer = () => FakeEndpoint.Json(status, "busy");

        var first = Processor().ScanOnce(Options(route), License, null, null);

        Assert.Equal((0, 1, 0), (first.Processed, first.Held, first.Errors));
        Assert.True(File.Exists(Path.Combine(route.Input, "order_1.json")));
        Assert.Contains("tries again on the next scan", _ledger.Events("order", 50).Last(e => e.Step == "waiting").Message);

        _endpoint.Answer = () => FakeEndpoint.Json(HttpStatusCode.OK, "{}");
        Assert.Equal(1, Processor().ScanOnce(Options(route), License, null, null).Processed);
    }

    [Fact]
    public void NoAnswerInTimeLeavesTheDocumentWaiting()
    {
        var route = Route();
        Pair(route);
        _endpoint.Answer = () => throw new TaskCanceledException("timeout");

        var result = Processor().ScanOnce(Options(route), License, null, null);

        Assert.Equal(1, result.Held);
        Assert.Contains("no answer within 60 seconds", _ledger.Events("order", 50).Last(e => e.Step == "waiting").Message);
    }

    [Fact]
    public void ARefusalGoesToErrorWithTheEndpointsAnswer()
    {
        var route = Route();
        Pair(route);
        _endpoint.Answer = () => FakeEndpoint.Json(HttpStatusCode.BadRequest, """{"error":"documentDate missing"}""");

        var result = Processor().ScanOnce(Options(route), License, null, null);

        Assert.Equal(1, result.Errors);
        Assert.True(File.Exists(Path.Combine(route.Input, "error", "order_1.json")));
        Assert.Contains("""answered HTTP 400: {"error":"documentDate missing"}""",
            _ledger.Events("order", 50).Single(e => e.Step == "delivered" && e.State == "failed").Message);
    }

    [Fact]
    public void ATriggerUrlKeptAsTheSecretIsCalledAsItIsAndItsKeyNeverShows()
    {
        var route = Route();
        route.Targets[0].Settings.Clear();
        Pair(route);
        var trigger = new ConnectionOptions { Id = "api", Kind = "https", Secret = TriggerUrl };

        Assert.Equal(1, Processor().ScanOnce(Options(route, trigger), License, null, null).Processed);

        var call = Assert.Single(_endpoint.Calls);
        Assert.Equal(TriggerUrl, call.Url.ToString());
        Assert.Null(call.Key);
        Assert.DoesNotContain(_ledger.Events("PO-7", 50), e => e.Message.Contains("SECRETSIG"));
    }

    [Theory]
    [InlineData("http://api.example/hooks", "key", "the address must start with https://")]
    [InlineData("", "", "give the address, or for a Logic App or Power Automate trigger its whole URL as the secret")]
    public void AnUnusableConnectionIsNamedBeforeAnythingIsProduced(string url, string secret, string reason)
    {
        var connection = new ConnectionOptions { Id = "api", Kind = "https", Secret = secret };
        if (url.Length > 0) connection.Settings["Url"] = url;
        var target = new HttpsDocumentTarget(new BDCOptions { Connections = { connection } }, _endpoint);

        Assert.Contains(reason, target.Unavailable(new TargetOptions { Kind = "https", Connection = "api" }));
        Assert.Contains("which is not set up", target.Unavailable(new TargetOptions { Kind = "https", Connection = "other" }));
        Assert.Empty(_endpoint.Calls);
    }

    [Fact]
    public async Task TheConnectionTestAsksWithGetAndNamesARefusedKey()
    {
        var connection = new ConnectionOptions { Id = "api", Kind = "https", Secret = "key-123", Settings = { ["Url"] = "https://api.example/hooks" } };
        var kind = new HttpsConnectionKind(_endpoint);

        _endpoint.Answer = () => FakeEndpoint.Json(HttpStatusCode.MethodNotAllowed, "");
        Assert.Equal("https://api.example/hooks answers (HTTP 405). Documents are sent there with POST.", await kind.TestAsync(connection));
        Assert.Equal(HttpMethod.Get, _endpoint.Calls.Single().Method);

        _endpoint.Answer = () => FakeEndpoint.Json(HttpStatusCode.Unauthorized, "");
        var refused = await Assert.ThrowsAsync<ConnectorException>(() => kind.TestAsync(connection));
        Assert.Contains("refused the key (HTTP 401)", refused.Message);
    }

    [Fact]
    public void SettingsOfferTheHttpsDestinationAndConnection()
    {
        var registry = ConnectorRegistry.BuiltIn();
        Assert.Contains("https", registry.DestinationKindNames);
        Assert.Contains(ConnectionCatalog.Describe(registry), d => d.Kind == "https" && d.SecretLabel == "Key or trigger URL");
    }
}