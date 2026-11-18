using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure case 55 (part 14c): checking that the instance can read the container F&amp;O writes to.</summary>
public sealed class SetupApiTests : IDisposable
{
    private const string ObjectId = "4c1f9a20-5d7e-4b8e-9a61-2f0c7d3e8b15";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-setup-" + Guid.NewGuid());
    private readonly MemoryETagBlob _settings = new();

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    private sealed class Store(Dictionary<string, string> blobs) : IBlobStore
    {
        public Exception? Fails { get; init; }
        public Uri ContainerUri => new("https://stcustomerfo.blob.core.windows.net/bdc-export");

        public IReadOnlyList<string> List(string prefix)
            => Fails is null ? blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList() : throw Fails;

        public bool Exists(string name) => blobs.ContainsKey(name);
        public byte[] Download(string name) => Encoding.UTF8.GetBytes(blobs[name]);
        public void Upload(string name, byte[] content, string contentType) => throw new InvalidOperationException("a check never writes");
        public void Move(string name, string newName) => throw new InvalidOperationException("a check never moves");
    }

    public SetupApiTests()
    {
        Directory.CreateDirectory(_root);
        _settings.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Token(params (string Name, string Value)[] claims)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject();
        foreach (var (name, value) in claims) payload[name] = value;
        return Part("{}") + "." + Part(payload.ToJsonString()) + ".c2ln";
    }

    private LocalStatusServer Api(ILicenseTenant tenant, Func<string, string, string?, IBlobStore> stores)
        => new(new Monitor(new BDCOptions()), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            NullLogger<LocalStatusServer>.Instance, null, "9.9.9", Path.Combine(_root, "unused.json"), _root,
            new BlobSettingsDocument(_settings), new BlobLicenseStore(new MemoryETagBlob()), tenant)
        { SourceStores = stores };

    private static async Task<(int Status, JsonObject Body, string Text)> Call(LocalStatusServer api, string method, string path, object? body = null)
    {
        var call = new ApiCall(method, path, new Dictionary<string, string>(), body is null ? "" : System.Text.Json.JsonSerializer.Serialize(body));
        await api.Route(call);
        string text = Encoding.UTF8.GetString(call.Answer);
        return (call.Status, JsonNode.Parse(text)!.AsObject(), text);
    }

    private static InstanceTenant Instance() => new(() => Token(("tid", Tenant), ("oid", ObjectId)));

    private static Dictionary<string, string> Exports() => new()
    {
        ["RE-1"] = """{ "dataAreaId": "1000", "documentNumber": "RE-1" }""",
        ["RE-1.pdf"] = "%PDF",
        ["RE-2"] = """{ "dataAreaId": "1000", "documentNumber": "RE-2" }""",
        ["RE-3"] = """{ "DataAreaId": "DEMF", "documentNumber": "RE-3" }""",
        ["processed/RE-0"] = """{ "dataAreaId": "1000" }""",
        ["error/RE-9"] = """{ "dataAreaId": "1000" }""",
    };

    [Fact]
    public async Task TheIdentityNamesTheObjectIdAndTheRoleCommand()
    {
        var (status, body, _) = await Call(Api(Instance(), (_, _, _) => new Store(new())), "GET", "/setup/identity");
        Assert.Equal(200, status);
        Assert.Equal(ObjectId, body["objectId"]!.GetValue<string>());
        Assert.Equal(Tenant, body["tenantId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALocalInstallHasNoIdentityOfItsOwn()
    {
        var (status, body, _) = await Call(Api(new SettingsLicenseTenant(), (_, _, _) => new Store(new())), "GET", "/setup/identity");
        Assert.Equal(200, status);
        Assert.Null(body["objectId"]);
        Assert.Contains("Azure", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task AReadableContainerCountsTheWaitingExportsPerCompany()
    {
        string? usedUrl = null, usedContainer = null;
        var api = Api(Instance(), (url, container, _) => { usedUrl = url; usedContainer = container; return new Store(Exports()); });
        var (status, body, _) = await Call(api, "POST", "/setup/check-source",
            new { accountUrl = "https://stcustomerfo.blob.core.windows.net", container = "bdc-export" });
        Assert.Equal(200, status);
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal(3, body["waiting"]!.GetValue<int>());
        var companies = body["byCompany"]!.AsArray().Select(c => $"{c!["company"]}:{c["count"]}").ToArray();
        Assert.Equal(new[] { "1000:2", "DEMF:1" }, companies);
        Assert.Equal("Access works. 3 exports are waiting: 2 for company 1000, 1 for DEMF.", body["message"]!.GetValue<string>());
        Assert.Equal("https://stcustomerfo.blob.core.windows.net", usedUrl);
        Assert.Equal("bdc-export", usedContainer);
    }

    [Fact]
    public async Task TheCheckAlsoCountsPerDocumentTypeAndCompany()
    {
        var exports = new Dictionary<string, string>
        {
            ["A"] = """{ "documentType": "SalesInvoice", "dataAreaId": "1000" }""",
            ["B"] = """{ "DocumentType": "salesinvoice", "dataAreaId": "1000" }""",
            ["C"] = """{ "documentType": "SalesConfirmation", "dataAreaId": "1000" }""",
            ["D"] = """{ "documentType": "SalesInvoice", "dataAreaId": "DEMF" }""",
            ["E"] = """{ "dataAreaId": "DEMF" }""",
        };
        var (_, body, _) = await Call(Api(Instance(), (_, _, _) => new Store(exports)), "POST", "/setup/check-source",
            new { accountUrl = "https://stcustomerfo.blob.core.windows.net", container = "bdc-export" });
        var types = body["byType"]!.AsArray().Select(t => $"{t!["documentType"]}/{t["company"]}:{t["count"]}").ToArray();
        Assert.Equal(new[] { "SalesInvoice/1000:2", "SalesConfirmation/1000:1", "SalesInvoice/DEMF:1", "unknown/DEMF:1" }, types);
    }

    [Fact]
    public async Task AnAzureInstanceNamesWhereSourcesAreStaged()
    {
        var (_, azure, _) = await Call(Api(Instance(), (_, _, _) => new Store(new())), "GET", "/setup/identity");
        Assert.Equal("/home/data/hivebdc/in", azure["inputRoot"]!.GetValue<string>());
        var (_, local, _) = await Call(Api(new SettingsLicenseTenant(), (_, _, _) => new Store(new())), "GET", "/setup/identity");
        Assert.Null(local["inputRoot"]);
    }

    [Fact]
    public async Task TheSettingsNameEachCompanysSource()
    {
        _settings.Put("""
            { "HiveBDC": { "PollSeconds": 30, "DocumentTypes": [ { "Id": "SalesInvoice", "Party": "Customer", "Handler": "Pdf",
              "Companies": [ { "DataAreaId": "1000", "Input": "/home/data/hivebdc/in/fo",
                "Source": { "Kind": "blob", "Connection": "fo-storage", "Settings": { "Container": "bdc-export" } } },
                { "DataAreaId": "DEMF", "Input": "/home/data/hivebdc/https" } ] } ] } }
            """);
        var (_, body, _) = await Call(Api(Instance(), (_, _, _) => new Store(new())), "GET", "/config");
        var companies = body["documentTypes"]![0]!["companies"]!.AsArray();
        Assert.Equal("blob", companies[0]!["source"]!["kind"]!.GetValue<string>());
        Assert.Equal("fo-storage", companies[0]!["source"]!["connection"]!.GetValue<string>());
        Assert.Equal("bdc-export", companies[0]!["source"]!["settings"]!["Container"]!.GetValue<string>());
        Assert.Null(companies[1]!["source"]);
    }

    [Fact]
    public async Task NoAccessYetNamesTheCommandToRun()
    {
        var api = Api(Instance(), (_, _, _) => new Store(new())
        {
            Fails = new RequestFailedException(403, "This request is not authorized to perform this operation using this permission.", "AuthorizationPermissionMismatch", null),
        });
        var (status, body, _) = await Call(api, "POST", "/setup/check-source",
            new { accountUrl = "https://stcustomerfo.blob.core.windows.net", container = "bdc-export" });
        Assert.Equal(200, status);
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Contains("no access", body["message"]!.GetValue<string>());
        string command = body["command"]!.GetValue<string>();
        Assert.Contains("--assignee-object-id " + ObjectId, command);
        Assert.Contains("--role \"Storage Blob Data Contributor\"", command);
        Assert.Contains("az storage account show -n stcustomerfo", command);
    }

    [Fact]
    public async Task AMissingContainerIsNamed()
    {
        var api = Api(Instance(), (_, _, _) => new Store(new())
        {
            Fails = new RequestFailedException(404, "The specified container does not exist.", "ContainerNotFound", null),
        });
        var (_, body, _) = await Call(api, "POST", "/setup/check-source",
            new { accountUrl = "https://stcustomerfo.blob.core.windows.net", container = "bdc-exports" });
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Equal("The container 'bdc-exports' does not exist in stcustomerfo.blob.core.windows.net.", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATypedConnectionStringIsUsedButNeitherStoredNorEchoed()
    {
        const string connectionString = "DefaultEndpointsProtocol=https;AccountName=stcustomerfo;EndpointSuffix=core.windows.net";
        string? used = null;
        string before = _settings.Text!;
        var api = Api(Instance(), (_, _, cs) => { used = cs; return new Store(Exports()); });
        var (status, _, text) = await Call(api, "POST", "/setup/check-source",
            new { accountUrl = "https://stcustomerfo.blob.core.windows.net", container = "bdc-export", connectionString });
        Assert.Equal(200, status);
        Assert.Equal(connectionString, used);
        Assert.DoesNotContain("AccountName", text);
        Assert.Equal(before, _settings.Text);
    }

    [Fact]
    public async Task PauseAndStartWriteOnlyThePausedSetting()
    {
        var api = Api(Instance(), (_, _, _) => new Store(new()));
        var (status, body, _) = await Call(api, "POST", "/setup/pause");
        Assert.Equal(200, status);
        Assert.True(body["paused"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(_settings.Text!)!["HiveBDC"]!["Paused"]!.GetValue<bool>());
        Assert.Equal(30, JsonNode.Parse(_settings.Text!)!["HiveBDC"]!["PollSeconds"]!.GetValue<int>());

        Assert.Equal(200, (await Call(api, "POST", "/setup/start")).Status);
        Assert.False(JsonNode.Parse(_settings.Text!)!["HiveBDC"]!["Paused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AConsoleSaveWithoutThePauseKeepsIt()
    {
        var api = Api(Instance(), (_, _, _) => new Store(new()));
        await Call(api, "POST", "/setup/pause");
        var (_, config, _) = await Call(api, "GET", "/config");
        config.Remove("paused");
        config["baseVersion"] = config["version"]!.GetValue<string>();
        var call = new ApiCall("POST", "/config", new Dictionary<string, string>(), config.ToJsonString());
        await api.Route(call);
        Assert.Equal(200, call.Status);
        Assert.True(JsonNode.Parse(_settings.Text!)!["HiveBDC"]!["Paused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ATestRunAnswersWhatTheWorkerDidWithTheDocumentsOwnSteps()
    {
        var runs = new TestRuns();
        var state = new TableStateStore(new MemoryTableRows(), NullLogger.Instance);
        state.RecordEvent(new DocumentEvent("RE-1", "RE-1_blob.json", EventSteps.Received, EventStates.Ok, "Received."));
        state.RecordEvent(new DocumentEvent("RE-10", "RE-10_blob.json", EventSteps.Received, EventStates.Ok, "Another document."));
        state.RecordEvent(new DocumentEvent("RE-1", "RE-1_blob.json", EventSteps.Done, EventStates.Ok, "Finished."));
        var api = new LocalStatusServer(new Monitor(new BDCOptions()), state, NullLogger<LocalStatusServer>.Instance, null, "9.9.9",
            Path.Combine(_root, "unused.json"), _root, new BlobSettingsDocument(_settings), new BlobLicenseStore(new MemoryETagBlob()), Instance())
        { TestRuns = runs };

        var worker = Task.Run(async () =>
        {
            TestRunRequest? request = null;
            for (int i = 0; i < 200 && (request = runs.Take()) is null; i++) await Task.Delay(10);
            Assert.Equal("SalesInvoice", request!.DocumentType);
            Assert.Equal("1000", request.Company);
            request.Complete(new TestRunResult(true, "RE-1", "Done. The export went through every step."));
        });
        var (status, body, _) = await Call(api, "POST", "/setup/test-run", new { documentType = "SalesInvoice", company = "1000" });
        await worker;
        Assert.Equal(200, status);
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal("RE-1", body["documentId"]!.GetValue<string>());
        Assert.Equal(new[] { "Received.", "Finished." }, body["events"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ATestRunWithoutAWorkerOrWhileAnotherRunsIsRefused()
    {
        var api = Api(Instance(), (_, _, _) => new Store(new()));
        Assert.Equal(503, (await Call(api, "POST", "/setup/test-run", new { documentType = "SalesInvoice", company = "1000" })).Status);

        var runs = new TestRuns();
        Assert.True(runs.TryRequest("SalesInvoice", "1000", out _));
        api.TestRuns = runs;
        Assert.Equal(409, (await Call(api, "POST", "/setup/test-run", new { documentType = "SalesInvoice", company = "1000" })).Status);
    }

    [Fact]
    public async Task ATestRunThatTakesLongerAnswersFollowTheHistory()
    {
        var api = Api(Instance(), (_, _, _) => new Store(new()));
        api.TestRuns = new TestRuns();
        api.TestRunWait = TimeSpan.FromMilliseconds(50);
        var (status, body, _) = await Call(api, "POST", "/setup/test-run", new { documentType = "SalesInvoice", company = "1000" });
        Assert.Equal(202, status);
        Assert.Contains("document history", body["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://stcustomerfo.blob.core.windows.net", "bdc-export")]
    [InlineData("not an address", "bdc-export")]
    [InlineData("https://stcustomerfo.blob.core.windows.net", "Bad_Name")]
    [InlineData("https://stcustomerfo.blob.core.windows.net", "")]
    public async Task AWrongAddressOrContainerNameIsRefusedBeforeAnyCall(string accountUrl, string container)
    {
        bool called = false;
        var api = Api(Instance(), (_, _, _) => { called = true; return new Store(new()); });
        var (status, body, _) = await Call(api, "POST", "/setup/check-source", new { accountUrl, container });
        Assert.Equal(400, status);
        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.False(called);
    }
}