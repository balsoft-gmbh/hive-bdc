using System.Text;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The settings of an Azure instance in one blob: reading, reloading, writing with ETag checks (case 22).</summary>
public sealed class SettingsBlobTests
{
    private readonly MemoryETagBlob _blob = new();

    private static JsonObject Section(MemoryETagBlob blob) => (JsonObject)SettingsFile.Parse(blob.Text!)["HiveBDC"]!;

    [Fact]
    public void AFirstWriteCreatesTheBlobWithTheProductSection()
    {
        var document = new BlobSettingsDocument(_blob);
        int changed = 0;
        document.Changed += () => changed++;

        Assert.True(document.Update(s => { s["PollSeconds"] = 20; return true; }));

        Assert.Equal(20, Section(_blob)["PollSeconds"]!.GetValue<int>());
        Assert.Equal(1, changed);
    }

    [Fact]
    public void NothingIsWrittenWhenTheChangeChangesNothing()
    {
        _blob.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        var document = new BlobSettingsDocument(_blob);

        Assert.False(document.Update(_ => false));
        Assert.Equal(0, _blob.Writes);
    }

    [Fact]
    public void AnotherWriterInBetweenIsKeptAndTheChangeIsMadeAgainOnItsVersion()
    {
        _blob.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        _blob.BeforeWrite = b => b.Put("""{ "HiveBDC": { "PollSeconds": 45, "LogDir": "/home/LogFiles" } }""");
        var document = new BlobSettingsDocument(_blob);
        int attempts = 0;

        Assert.True(document.Update(s => { attempts++; s["Mail"] = new JsonObject { ["Enabled"] = true }; return true; }));

        var section = Section(_blob);
        Assert.Equal(2, attempts);
        Assert.Equal(45, section["PollSeconds"]!.GetValue<int>());
        Assert.Equal("/home/LogFiles", section["LogDir"]!.GetValue<string>());
        Assert.True(section["Mail"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ABlobThatIsNotJsonIsLeftAlone()
    {
        _blob.Put("{ not json");
        var document = new BlobSettingsDocument(_blob);

        Assert.Throws<InvalidOperationException>(() => document.Update(s => { s["PollSeconds"] = 5; return true; }));
        Assert.Equal("{ not json", _blob.Text);
    }

    [Fact]
    public void AChangedBlobIsReadAgainOnThePollAndTheOptionsSeeIt()
    {
        _blob.Put("""{ "HiveBDC": { "PollSeconds": 30, "DocumentTypes": [ { "Id": "SalesInvoice" }, { "Id": "Credit" } ] } }""");
        var provider = new BlobSettingsProvider(_blob);
        var config = new ConfigurationBuilder().Add(provider).Build();
        int reloads = 0;
        ChangeToken.OnChange(config.GetReloadToken, () => reloads++);
        Assert.Equal("30", config["HiveBDC:PollSeconds"]);

        provider.Poll();
        Assert.Equal(0, reloads);

        _blob.Put("""{ "HiveBDC": { "PollSeconds": 10, "DocumentTypes": [ { "Id": "SalesInvoice" } ] } }""");
        provider.Poll();

        Assert.Equal(1, reloads);
        var opts = config.GetSection("HiveBDC").Get<BDCOptions>()!;
        Assert.Equal(10, opts.PollSeconds);
        Assert.Equal(new[] { "SalesInvoice" }, opts.DocumentTypes.Select(t => t.Id));
    }

    [Fact]
    public void AnUnreadableBlobKeepsTheLastSettingsAndSaysWhyUntilItAnswersAgain()
    {
        _blob.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        var provider = new BlobSettingsProvider(_blob);
        var config = new ConfigurationBuilder().Add(provider).Build();

        _blob.Down = true;
        provider.Poll();
        Assert.Equal("30", config["HiveBDC:PollSeconds"]);
        Assert.Contains("blob 503", provider.Problem);

        _blob.Down = false;
        provider.Poll();
        Assert.Null(provider.Problem);
    }

    [Fact]
    public void AMissingBlobMeansEmptySettingsNotAProblem()
    {
        var provider = new BlobSettingsProvider(_blob);
        var config = new ConfigurationBuilder().Add(provider).Build();

        Assert.Null(config["HiveBDC:PollSeconds"]);
        Assert.Null(provider.Problem);
    }

    [Fact]
    public void AWriteOfThisInstanceReloadsAtOnce()
    {
        _blob.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        var provider = new BlobSettingsProvider(_blob);
        var config = new ConfigurationBuilder().Add(provider).Build();
        var document = new BlobSettingsDocument(_blob);
        document.Changed += provider.Refresh;

        document.Update(s => { s["PollSeconds"] = 12; return true; });

        Assert.Equal("12", config["HiveBDC:PollSeconds"]);
    }

    [Fact]
    public void ANewDocumentTypeFromTheErpIsRegisteredInTheBlob()
    {
        _blob.Put("""{ "HiveBDC": { "DocumentTypes": [ { "Id": "SalesInvoice" } ] } }""");
        var registry = new DocumentTypeRegistry(new BlobSettingsDocument(_blob), NullLogger.Instance);
        var opts = new BDCOptions { DocumentTypes = { new DocumentTypeOptions { Id = "SalesInvoice" } } };
        var seed = new DocumentRoute { DataAreaId = "1000", Input = "/tmp/in", Source = new SourceOptions { Kind = "blob" } };

        var type = registry.Resolve(opts, "ProjectInvoice", "Customer", seed, out bool discovered);

        Assert.True(discovered);
        Assert.Equal("ProjectInvoice", type.Id);
        var ids = ((JsonArray)Section(_blob)["DocumentTypes"]!).Select(t => t!["Id"]!.GetValue<string>());
        Assert.Equal(new[] { "SalesInvoice", "ProjectInvoice" }, ids);
    }

    [Fact]
    public void TheGateWaitsWhileTheSettingsBlobCannotBeRead()
    {
        var gate = new AzureProcessingGate(new TableStateStore(new MemoryTableRows(), NullLogger.Instance), new SecretResolver(null),
            () => "https://account.blob.core.windows.net/settings/hivebdc.json: blob 503");

        Assert.Equal("the settings in Blob Storage cannot be read: https://account.blob.core.windows.net/settings/hivebdc.json: blob 503",
            gate.Hold(new BDCOptions()));
    }

    [Theory]
    [InlineData("https://account.blob.core.windows.net/settings")]
    [InlineData("http://account.blob.core.windows.net/settings/hivebdc.json")]
    [InlineData("hivebdc.json")]
    public void ASettingsBlobAddressWithoutContainerAndNameIsRefused(string url)
        => Assert.Throws<ArgumentException>(() => AzureETagBlob.For(url, null));
}