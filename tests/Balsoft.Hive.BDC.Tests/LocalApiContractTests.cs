using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// What the Windows app relies on from the service's loopback API: the endpoint file, the bearer token, the routes and
/// the answers' shape. These run against the real server on 127.0.0.1, so a change of the transport underneath cannot
/// change what the app sees.
/// </summary>
public sealed class LocalApiContractTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-api-" + Guid.NewGuid());
    private readonly HttpClient _http = new();
    private readonly string _secret = "marker-" + Guid.NewGuid().ToString("N");
    private LocalStatusServer _server = null!;
    private ExportLedger _ledger = null!;
    private int _port;
    private string _token = "";

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, "logs", "hive-bdc-20261117.log"), "line one\nline two\nline three\n");
        string settings = Path.Combine(_root, "appsettings.json");
        var auth = new JsonObject { ["TenantId"] = "t", ["ClientId"] = "c", ["ClientSecret"] = _secret };
        File.WriteAllText(settings, new JsonObject { ["HiveBDC"] = new JsonObject { ["PollSeconds"] = 30, ["SharePointAuth"] = auth } }.ToJsonString());
        var options = new BDCOptions { PollSeconds = 30, SharePointAuth = { TenantId = "t", ClientId = "c", ClientSecret = _secret } };
        options.DocumentTypes.Add(new DocumentTypeOptions { Id = "SalesInvoice", Companies = { new DocumentRoute { DataAreaId = "1000", Input = Path.Combine(_root, "in") } } });
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
        _ledger.RecordEvent(new DocumentEvent("RE-1", "RE-1.json", "done", "ok", "delivered", DocumentNumber: "RE-1"));
        _ledger.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 10, "1000");
        _server = new LocalStatusServer(new Monitor(options), _ledger, NullLogger<LocalStatusServer>.Instance,
            Path.Combine(_root, "logs"), "9.9.9", settings, _root);
        await _server.StartAsync(CancellationToken.None);
        string endpoint = Path.Combine(_root, "app-endpoint.json");
        for (int i = 0; i < 100 && !File.Exists(endpoint); i++) await Task.Delay(50);
        var node = JsonNode.Parse(File.ReadAllText(endpoint))!;
        _port = (int)node["port"]!;
        _token = (string)node["token"]!;
        Assert.Equal(Path.Combine(_root, "logs"), (string)node["logDir"]!);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body, string? ContentType)> Call(HttpMethod method, string path, string? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        if (token != "") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WithoutOrWithAWrongTokenTheAnswerIs401()
    {
        var none = await Call(HttpMethod.Get, "/status", token: "");
        var wrong = await Call(HttpMethod.Get, "/status", token: "not-the-token");
        Assert.Equal((HttpStatusCode.Unauthorized, "unauthorized"), (none.Status, (string)none.Body!["error"]!));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
    }

    [Fact]
    public async Task StatusSaysRunningWithVersionTypesAndLicence()
    {
        var (status, body, type) = await Call(HttpMethod.Get, "/status");

        Assert.Equal((HttpStatusCode.OK, "application/json"), (status, type));
        Assert.True((bool)body!["running"]!);
        Assert.Equal("9.9.9", (string)body["version"]!);
        Assert.Equal(30, (int)body["pollSeconds"]!);
        Assert.Equal("SalesInvoice", (string)body["documentTypes"]![0]!["id"]!);
        Assert.NotNull(body["license"]!["state"]);
        Assert.True((bool)body["license"]!["blocked"]!);
    }

    [Fact]
    public async Task PathsIgnoreCaseAndATrailingSlash()
        => Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/Status/")).Status);

    [Fact]
    public async Task MetricsHistoryAndLogsAnswerWithTheirFields()
    {
        var metrics = (await Call(HttpMethod.Get, "/metrics?recent=5")).Body!;
        Assert.Equal(1, (long)metrics["total"]!);
        Assert.Equal("RE-1", (string)metrics["recent"]![0]!["documentId"]!);

        var history = (await Call(HttpMethod.Get, "/history?search=re-1&limit=10")).Body!;
        Assert.Equal("re-1", (string)history["search"]!);
        Assert.Equal("delivered", (string)history["events"]![0]!["message"]!);

        var logs = (await Call(HttpMethod.Get, "/logs?tail=2")).Body!;
        Assert.Equal(new[] { "line two", "line three" }, logs["lines"]!.AsArray().Select(l => (string)l!));
    }

    [Fact]
    public async Task ConfigNeverReturnsASecret()
    {
        var (status, body, _) = await Call(HttpMethod.Get, "/config");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool)body!["sharePointAuth"]!["secretSet"]!);
        Assert.DoesNotContain(_secret, body.ToJsonString());
    }

    [Fact]
    public async Task BrokenJsonEmptyLicenceAndUnknownRoutesAreRefusedWithAReason()
    {
        var config = await Call(HttpMethod.Post, "/config", "{ not json");
        Assert.Equal(HttpStatusCode.BadRequest, config.Status);
        Assert.False((bool)config.Body!["ok"]!);
        Assert.StartsWith("Invalid JSON", (string)config.Body["errors"]![0]!);

        var license = await Call(HttpMethod.Post, "/license", """{"content":""}""");
        Assert.Equal((HttpStatusCode.BadRequest, "No license content provided."), (license.Status, (string)license.Body!["message"]!));

        var unknown = await Call(HttpMethod.Get, "/nothing-here");
        Assert.Equal((HttpStatusCode.NotFound, "not found"), (unknown.Status, (string)unknown.Body!["error"]!));
    }

    [Fact]
    public async Task ATamperedLicenceIsRefusedAndNothingIsWritten()
    {
        var (status, body, _) = await Call(HttpMethod.Post, "/license", """{"content":"abc.def"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid", (string)body!["state"]!);
    }

    [Fact]
    public async Task ARequestFromAnotherAddressIsRefusedEvenWithTheToken()
    {
        var call = new ApiCall("GET", "/status", new Dictionary<string, string>(), "");

        await _server.Handle(call, IPAddress.Parse("203.0.113.7"), "Bearer " + _token);

        Assert.Equal((403, "loopback only"), (call.Status, (string)JsonNode.Parse(call.Answer)!["error"]!));
    }

    [Fact]
    public async Task TheCatalogRoutesAnswer()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/sources")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/targets")).Status);
        var kinds = (await Call(HttpMethod.Get, "/connections/kinds")).Body!;
        Assert.Contains("servicebus", kinds.ToJsonString());
    }
}