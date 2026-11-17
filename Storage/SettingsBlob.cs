using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>The one blob that holds the settings of an Azure instance, with the ETag every write is checked against.</summary>
public interface ISettingsBlob
{
    string Where { get; }

    /// <summary>The content and its ETag, or null when the blob does not exist yet.</summary>
    (byte[] Content, string ETag)? Read();

    /// <summary>The current ETag, or null when the blob does not exist; cheaper than <see cref="Read"/> for polling.</summary>
    string? CurrentETag();

    /// <summary>
    /// Writes only when the blob still has <paramref name="ifMatch"/>, or, with null, only when it does not exist yet;
    /// false when someone else wrote in between.
    /// </summary>
    bool TryWrite(byte[] content, string? ifMatch);
}

/// <summary>Azure Blob Storage, signed in with a connection string or the host identity (managed identity in Azure).</summary>
public sealed class AzureSettingsBlob(BlobClient blob) : ISettingsBlob
{
    public static AzureSettingsBlob For(string blobUrl, string? connectionString)
    {
        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Segments.Length < 3)
            throw new ArgumentException("The settings blob must look like https://<account>.blob.core.windows.net/<container>/<name>.json.");
        if (string.IsNullOrWhiteSpace(connectionString))
            return new AzureSettingsBlob(new BlobClient(url, new DefaultAzureCredential()));
        var parts = new BlobUriBuilder(url);
        return new AzureSettingsBlob(new BlobServiceClient(connectionString).GetBlobContainerClient(parts.BlobContainerName).GetBlobClient(parts.BlobName));
    }

    public string Where => blob.Uri.GetLeftPart(UriPartial.Path);

    public (byte[] Content, string ETag)? Read()
    {
        try
        {
            var result = blob.DownloadContent().Value;
            return (result.Content.ToArray(), result.Details.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public string? CurrentETag()
    {
        try
        {
            return blob.GetProperties().Value.ETag.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public bool TryWrite(byte[] content, string? ifMatch)
    {
        var conditions = ifMatch is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(ifMatch) };
        try
        {
            blob.Upload(new BinaryData(content), new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            });
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }
}

/// <summary>
/// The settings of an Azure instance as one JSON blob. A write reads the blob, changes the product section and writes it
/// back only if nobody wrote in between (ETag); otherwise it reads again and repeats the change, so a document type the
/// service registers and a save from the web page never overwrite each other (failure case 22).
/// </summary>
public sealed class BlobSettingsDocument(ISettingsBlob blob) : ISettingsDocument
{
    private const int Attempts = 5;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Raised after every write, so the configuration reloads without waiting for the next poll.</summary>
    public event Action? Changed;

    public string Where => blob.Where;

    public bool Update(Func<JsonObject, bool> mutate)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var current = blob.Read();
            JsonObject root;
            try
            {
                root = current is { } c ? SettingsFile.Parse(Encoding.UTF8.GetString(c.Content)) : new JsonObject();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{blob.Where} could not be read as JSON, so it was not changed.", ex);
            }
            if (!mutate(SettingsFile.ProductSection(root))) return false;
            if (blob.TryWrite(Encoding.UTF8.GetBytes(root.ToJsonString(Indented)), current?.ETag))
            {
                Changed?.Invoke();
                return true;
            }
        }
        throw new InvalidOperationException($"{blob.Where} was changed by someone else {Attempts} times in a row; the change was not written.");
    }
}

/// <summary>
/// Configuration from the settings blob, in place of the package's appsettings.json. It asks for the blob's ETag every
/// poll interval and reloads when it changed, so <c>IOptionsMonitor</c> sees a change within one interval. When the
/// blob cannot be read the last settings stay and <see cref="Problem"/> says why; a missing blob means empty settings.
/// </summary>
public sealed class BlobSettingsProvider : ConfigurationProvider, IConfigurationSource, IDisposable
{
    private readonly ISettingsBlob _blob;
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private string? _etag;

    public BlobSettingsProvider(ISettingsBlob blob, TimeSpan? pollEvery = null)
    {
        _blob = blob;
        if (pollEvery is { } every) _timer = new Timer(_ => Poll(), null, every, every);
    }

    /// <summary>Why the last read failed, or null when the settings in use are the blob's current ones.</summary>
    public string? Problem { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override void Load()
    {
        lock (_gate)
        {
            try
            {
                var current = _blob.Read();
                var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (current is { } c)
                {
                    using var stream = new MemoryStream(c.Content);
                    foreach (var (key, value) in new ConfigurationBuilder().AddJsonStream(stream).Build().AsEnumerable())
                        if (value is not null) data[key] = value;
                }
                Data = data;
                _etag = current?.ETag;
                Problem = null;
            }
            catch (Exception ex)
            {
                Problem = $"{_blob.Where}: {ex.Message}";
            }
        }
    }

    /// <summary>Reloads now, after this instance wrote the blob itself.</summary>
    public void Refresh()
    {
        Load();
        OnReload();
    }

    /// <summary>One poll: reload when the blob changed since the last read, or when the last read failed.</summary>
    public void Poll()
    {
        try
        {
            if (Problem is null && _blob.CurrentETag() == _etag) return;
        }
        catch (Exception ex)
        {
            Problem = $"{_blob.Where}: {ex.Message}";
            return;
        }
        Refresh();
    }

    public void Dispose() => _timer?.Dispose();
}