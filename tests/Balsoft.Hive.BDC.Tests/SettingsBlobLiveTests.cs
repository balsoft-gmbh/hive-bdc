using System.Text;
using Azure.Identity;
using Azure.Storage.Blobs;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The ETag rules of the settings blob against real Blob Storage. HIVEBDC_SETTINGS_CONTAINER_URL names a container
/// (https://{account}.blob.core.windows.net/{container}) the sign-in may write to; the test writes its own blob there
/// and deletes it afterwards.
/// </summary>
public sealed class SettingsBlobLiveTests : IDisposable
{
    private readonly string _url = Environment.GetEnvironmentVariable("HIVEBDC_SETTINGS_CONTAINER_URL")?.TrimEnd('/') + "/hivebdc-check-" + Guid.NewGuid().ToString("N")[..8] + ".json";

    [SettingsBlobLiveFact]
    public void ARealBlobKeepsTheETagRulesAndTheProviderReadsIt()
    {
        var blob = AzureETagBlob.For(_url, null);
        Assert.Null(blob.Read());
        Assert.Null(blob.CurrentETag());

        Assert.True(blob.TryWrite(Encoding.UTF8.GetBytes("""{ "HiveBDC": { "PollSeconds": 30 } }"""), null));
        Assert.False(blob.TryWrite(Encoding.UTF8.GetBytes("{}"), null));
        string first = blob.Read()!.Value.ETag;
        Assert.Equal(first, blob.CurrentETag());

        Assert.True(blob.TryWrite(Encoding.UTF8.GetBytes("""{ "HiveBDC": { "PollSeconds": 20 } }"""), first));
        Assert.False(blob.TryWrite(Encoding.UTF8.GetBytes("{}"), first));

        var provider = new BlobSettingsProvider(blob);
        var config = new ConfigurationBuilder().Add(provider).Build();
        Assert.Equal("20", config["HiveBDC:PollSeconds"]);

        var document = new BlobSettingsDocument(blob);
        document.Changed += provider.Refresh;
        Assert.True(document.Update(s => { s["PollSeconds"] = 10; return true; }));
        Assert.Equal("10", config["HiveBDC:PollSeconds"]);
        Assert.Null(provider.Problem);
    }

    public void Dispose()
    {
        if (SettingsBlobLiveFactAttribute.Missing) return;
        new BlobClient(new Uri(_url), new DefaultAzureCredential()).DeleteIfExists();
    }
}

public sealed class SettingsBlobLiveFactAttribute : FactAttribute
{
    public static bool Missing => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIVEBDC_SETTINGS_CONTAINER_URL"));

    public SettingsBlobLiveFactAttribute()
    {
        if (Missing) Skip = "Set HIVEBDC_SETTINGS_CONTAINER_URL (a container the sign-in may write to) to run this integration test.";
    }
}