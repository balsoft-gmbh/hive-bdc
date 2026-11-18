using System.Text;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The secret vault is global to the process, so the test that swaps it runs alone.</summary>
[CollectionDefinition("global secret vault", DisableParallelization = true)]
public sealed class GlobalSecretVaultCollection;

/// <summary>Failure case 54 (part 14b): two administrators save the settings of one Azure instance.</summary>
[Collection("global secret vault")]
public sealed class SettingsSaveRefusalTests : IDisposable
{
    private const string Start = """
        { "HiveBDC": {
          "PollSeconds": 30,
          "DocumentTypes": [ { "Id": "SalesInvoice", "DisplayName": "Sales invoice", "Party": "Customer", "Handler": "EInvoice",
            "Enabled": true, "Mail": false, "Companies": [
              { "DataAreaId": "1000", "Input": "/home/in/1000", "Output": "/home/out/1000",
                "Targets": [ { "Kind": "docuware", "Connection": "dw", "Enabled": false, "Settings": { "FileCabinet": "Datapool" } } ] } ] } ],
          "Connections": [ { "Id": "dw", "Kind": "docuware", "Settings": { "Url": "https://dw.example" }, "Secret": "kv:dw" } ]
        } }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-refusal-" + Guid.NewGuid());
    private readonly MemoryETagBlob _blob = new();
    private readonly LocalStatusServer _api;

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    private sealed class RecordingVault : ISecretVault
    {
        public List<string> Written { get; } = new();
        public string? Get(string name) => "value";
        public void Set(string name, string value) => Written.Add(name);
    }

    public SettingsSaveRefusalTests()
    {
        Directory.CreateDirectory(_root);
        _blob.Put(Start);
        // The running options are deliberately stale (poll 99): GET must answer from the blob it versions.
        var stale = new BDCOptions { PollSeconds = 99 };
        _api = new LocalStatusServer(new Monitor(stale), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
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

    private async Task<(int Status, JsonObject Body)> Post(JsonObject config, string user, bool saveBoth = false, bool sendVersion = true)
    {
        var body = (JsonObject)config.DeepClone();
        if (!sendVersion) body.Remove("version");
        else body["baseVersion"] = body["version"]?.GetValue<string>();
        if (saveBoth) body["saveBoth"] = true;
        var call = new ApiCall("POST", "/config", new Dictionary<string, string>(), body.ToJsonString()) { User = user };
        await _api.Route(call);
        return (call.Status, JsonNode.Parse(Encoding.UTF8.GetString(call.Answer))!.AsObject());
    }

    private static JsonObject WithPoll(JsonObject config, int poll)
    {
        var copy = (JsonObject)config.DeepClone();
        copy["pollSeconds"] = poll;
        return copy;
    }

    private static JsonObject WithDocuWareOn(JsonObject config, string? newSecret = null)
    {
        var copy = (JsonObject)config.DeepClone();
        copy["documentTypes"]![0]!["companies"]![0]!["targets"]![0]!["enabled"] = true;
        if (newSecret is not null) copy["connections"]![0]!["secret"] = newSecret;
        return copy;
    }

    private JsonObject Stored() => JsonNode.Parse(_blob.Text!)!["HiveBDC"]!.AsObject();

    [Fact]
    public async Task GetAnswersFromTheBlobItVersionsNotFromStaleOptions()
    {
        var config = await Get();
        Assert.Equal(30, config["pollSeconds"]!.GetValue<int>());
        Assert.Equal(ConfigMerge.Version(Stored()), config["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASaveOnTheCurrentVersionIsWrittenWithWhoSavedIt()
    {
        var (status, body) = await Post(WithPoll(await Get(), 45), "anna@contoso.example");
        Assert.Equal(200, status);
        Assert.Equal(45, Stored()["PollSeconds"]!.GetValue<int>());
        Assert.Equal("anna@contoso.example", Stored()["LastSaved"]!["By"]!.GetValue<string>());
        var again = await Get();
        Assert.Equal(body["version"]!.GetValue<string>(), again["version"]!.GetValue<string>());
        Assert.Equal("anna@contoso.example", again["lastSaved"]!["by"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASaveFromAnOlderVersionIsRefusedWithBothChangesAndNothingWritten()
    {
        var annaOpened = await Get();
        var meOpened = await Get();
        Assert.Equal(200, (await Post(WithPoll(annaOpened, 45), "anna@contoso.example")).Status);

        var (status, body) = await Post(WithDocuWareOn(meOpened), "ertugrul@balsoft.example");
        Assert.Equal(409, status);
        Assert.True(body["conflict"]!.GetValue<bool>());
        Assert.True(body["baseKnown"]!.GetValue<bool>());
        Assert.Equal("anna@contoso.example", body["savedBy"]!.GetValue<string>());
        var theirs = Assert.Single(body["theirChanges"]!.AsArray());
        Assert.Equal("PollSeconds", theirs!["path"]!.GetValue<string>());
        Assert.Equal("30", theirs["before"]!.GetValue<string>());
        Assert.Equal("45", theirs["after"]!.GetValue<string>());
        Assert.Contains(body["yourChanges"]!.AsArray(), c => c!["path"]!.GetValue<string>() == "SalesInvoice › company 1000 › docuware dw Datapool › Enabled");
        Assert.Empty(body["overlaps"]!.AsArray());
        Assert.False(Stored()["DocumentTypes"]![0]!["Companies"]![0]!["Targets"]![0]!["Enabled"]!.GetValue<bool>());

        var (bothStatus, _) = await Post(WithDocuWareOn(meOpened), "ertugrul@balsoft.example", saveBoth: true);
        Assert.Equal(200, bothStatus);
        Assert.Equal(45, Stored()["PollSeconds"]!.GetValue<int>());
        Assert.True(Stored()["DocumentTypes"]![0]!["Companies"]![0]!["Targets"]![0]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("ertugrul@balsoft.example", Stored()["LastSaved"]!["By"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSameSettingChangedTwiceIsRefusedEvenWithSaveBoth()
    {
        var annaOpened = await Get();
        var meOpened = await Get();
        Assert.Equal(200, (await Post(WithPoll(annaOpened, 45), "anna@contoso.example")).Status);
        var (status, body) = await Post(WithPoll(meOpened, 60), "ertugrul@balsoft.example", saveBoth: true);
        Assert.Equal(409, status);
        Assert.Equal("PollSeconds", Assert.Single(body["overlaps"]!.AsArray())!.GetValue<string>());
        Assert.Equal(45, Stored()["PollSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task ASaveBetweenTheCheckAndTheWriteIsCaughtToo()
    {
        var opened = await Get();
        // Another writer saves right before this save's conditional write; the blob refuses the stale ETag and the
        // check runs again on the newer settings.
        _blob.BeforeWrite = blob =>
        {
            var root = JsonNode.Parse(blob.Text!)!.AsObject();
            root["HiveBDC"]!["PollSeconds"] = 45;
            root["HiveBDC"]!["LastSaved"] = new JsonObject { ["By"] = "anna@contoso.example", ["At"] = "2026-11-18T10:44:00Z" };
            blob.Put(root.ToJsonString());
        };
        var (status, body) = await Post(WithPoll(opened, 60), "ertugrul@balsoft.example");
        Assert.Equal(409, status);
        Assert.Equal("anna@contoso.example", body["savedBy"]!.GetValue<string>());
        Assert.Equal(45, Stored()["PollSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnUnknownStartIsRefusedAndAsksToLoadAgain()
    {
        var opened = await Get();
        Assert.Equal(200, (await Post(WithPoll(opened, 45), "anna@contoso.example")).Status);
        opened["version"] = "0000000000000000";
        var (status, body) = await Post(WithPoll(opened, 60), "ertugrul@balsoft.example", saveBoth: true);
        Assert.Equal(409, status);
        Assert.False(body["baseKnown"]!.GetValue<bool>());
        Assert.Empty(body["theirChanges"]!.AsArray());
        Assert.Equal(45, Stored()["PollSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheWindowsAppSendsNoVersionAndSavesAsBefore()
    {
        var opened = await Get();
        Assert.Equal(200, (await Post(WithPoll(opened, 45), "anna@contoso.example")).Status);
        var (status, _) = await Post(WithPoll(opened, 60), "dashboard", sendVersion: false);
        Assert.Equal(200, status);
        Assert.Equal(60, Stored()["PollSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task ARefusedSavePutsNoNewSecretIntoTheVault()
    {
        var previous = SecretProtector.Current;
        var vault = new RecordingVault();
        SecretProtector.UseVault(vault);
        try
        {
            var annaOpened = await Get();
            var meOpened = await Get();
            Assert.Equal(200, (await Post(WithPoll(annaOpened, 45), "anna@contoso.example")).Status);

            Assert.Equal(409, (await Post(WithPoll(WithDocuWareOn(meOpened, "typed-secret"), 60), "ertugrul@balsoft.example", saveBoth: true)).Status);
            Assert.Equal(409, (await Post(WithDocuWareOn(meOpened, "typed-secret"), "ertugrul@balsoft.example")).Status);
            Assert.Empty(vault.Written);

            Assert.Equal(200, (await Post(WithDocuWareOn(meOpened, "typed-secret"), "ertugrul@balsoft.example", saveBoth: true)).Status);
            Assert.Equal(new[] { "connection-dw" }, vault.Written);
            Assert.DoesNotContain("typed-secret", _blob.Text!);
        }
        finally
        {
            // SecretProtector has no public reset; the property's private setter puts the earlier resolver back.
            typeof(SecretProtector).GetProperty(nameof(SecretProtector.Current))!.SetValue(null, previous);
        }
    }
}