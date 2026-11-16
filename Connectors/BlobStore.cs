using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>The blob operations Hive BDC needs, so the staging and target logic can be tested without Azure.</summary>
public interface IBlobStore
{
    /// <summary>The account and container, for log lines and for checking that a link points into this container.</summary>
    Uri ContainerUri { get; }

    IReadOnlyList<string> List(string prefix);

    bool Exists(string name);

    byte[] Download(string name);

    void Upload(string name, byte[] content, string contentType);

    /// <summary>Copies the blob to <paramref name="newName"/> and deletes the original.</summary>
    void Move(string name, string newName);
}

/// <summary>
/// Azure Blob Storage for one container. Signs in with the connection's secret when it is a storage connection
/// string, otherwise with the managed identity of the host (Azure) through the account URL in the settings.
/// </summary>
public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobContainerClient _container;

    public AzureBlobStore(BlobContainerClient container) => _container = container;

    public static AzureBlobStore For(ConnectionOptions connection, string container)
    {
        string secret = SecretProtector.Unprotect(connection.Secret);
        var client = secret.Length > 0
            ? new BlobServiceClient(secret)
            : new BlobServiceClient(new Uri(connection.Setting("AccountUrl")), new DefaultAzureCredential());
        return new AzureBlobStore(client.GetBlobContainerClient(container));
    }

    public Uri ContainerUri => _container.Uri;

    public IReadOnlyList<string> List(string prefix)
        => _container.GetBlobs(BlobTraits.None, BlobStates.None, string.IsNullOrEmpty(prefix) ? null : prefix, CancellationToken.None).Select(b => b.Name).ToList();

    public bool Exists(string name) => _container.GetBlobClient(name).Exists().Value;

    public byte[] Download(string name) => _container.GetBlobClient(name).DownloadContent().Value.Content.ToArray();

    public void Upload(string name, byte[] content, string contentType)
        => _container.GetBlobClient(name).Upload(new BinaryData(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } });

    public void Move(string name, string newName)
    {
        var source = _container.GetBlobClient(name);
        var target = _container.GetBlobClient(newName);
        target.Upload(source.DownloadContent().Value.Content, overwrite: true);
        source.DeleteIfExists();
    }
}