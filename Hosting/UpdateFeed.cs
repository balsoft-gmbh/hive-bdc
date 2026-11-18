using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// One entry of the Azure update feed (<c>azure/latest.json</c> beside the Windows feed). Two RSA SHA-256 PKCS#1
/// signatures, both made on the signing host with Balsoft's update key: <see cref="EntrySignature"/> over
/// <see cref="Statement"/>, so nothing of the entry is shown or used unverified, and <see cref="Signature"/> over the ZIP.
/// </summary>
public sealed record UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("signature")] public string Signature { get; init; } = "";
    [JsonPropertyName("entrySignature")] public string EntrySignature { get; init; } = "";
    [JsonPropertyName("runtime")] public string Runtime { get; init; } = "";
    [JsonPropertyName("notes")] public List<string> Notes { get; init; } = [];
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; init; }

    /// <summary>The signed text: build/sign-azure-package.ps1 writes the same lines, joined by "\n", as UTF-8.</summary>
    public string Statement() => string.Join("\n",
        new[] { "hive-bdc-azure-update", Version, Size.ToString(CultureInfo.InvariantCulture), Sha256.ToLowerInvariant(), Runtime, ReleaseDate ?? "" }
            .Concat(Notes));
}

public interface IUpdateFeed
{
    /// <summary>The newest Azure package; throws when the feed cannot be read.</summary>
    Task<UpdateManifest> Latest(CancellationToken ct);

    Task<Stream> Open(string url, CancellationToken ct);
}

/// <summary>The public Balsoft feed, or the one named in the app setting HiveBDC__Azure__UpdateFeed.</summary>
public sealed class HttpUpdateFeed(HttpClient http, string baseUrl) : IUpdateFeed
{
    public const string DefaultBaseUrl = "https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc";
    private const int MaxEntryBytes = 64 * 1024;

    public async Task<UpdateManifest> Latest(CancellationToken ct)
    {
        using var answer = await http.GetAsync(EntryUrl(baseUrl), HttpCompletionOption.ResponseHeadersRead, ct);
        if (answer.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("the feed offers no Azure package yet");
        if (!answer.IsSuccessStatusCode) throw new InvalidDataException($"the feed answered {(int)answer.StatusCode}");
        if (answer.Content.Headers.ContentLength > MaxEntryBytes) throw new InvalidDataException("the feed entry is too large");

        await using var body = await answer.Content.ReadAsStreamAsync(ct);
        using var copy = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (copy.Length + read > MaxEntryBytes) throw new InvalidDataException("the feed entry is too large");
            copy.Write(chunk, 0, read);
        }
        copy.Position = 0;
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(copy, cancellationToken: ct)
               ?? throw new InvalidDataException("the feed answered with nothing");
    }

    public async Task<Stream> Open(string url, CancellationToken ct)
    {
        var answer = await http.GetAsync(PackageUrl(baseUrl, url), HttpCompletionOption.ResponseHeadersRead, ct);
        answer.EnsureSuccessStatusCode();
        return await answer.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>{base}/azure/latest.json; a query on the base (a read-only SAS of a private mirror) stays on the address.</summary>
    public static Uri EntryUrl(string baseUrl)
    {
        var address = new UriBuilder(baseUrl.Trim());
        address.Path = address.Path.TrimEnd('/') + "/azure/latest.json";
        return address.Uri;
    }

    /// <summary>
    /// The package address of the entry, https only. A package on the feed's own host without a query gets the feed's
    /// query, so a private mirror's SAS stays in the app setting and never has to be written into the entry.
    /// </summary>
    public static Uri PackageUrl(string baseUrl, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var package) || package.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("the feed names a package address that is not https");
        var feed = new Uri(baseUrl.Trim());
        if (package.Query.Length == 0 && feed.Query.Length > 1 && string.Equals(package.Host, feed.Host, StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(package) { Query = feed.Query[1..] }.Uri;
        return package;
    }
}

public static class UpdateSignature
{
    public static bool Verify(Stream data, string signatureBase64, RSA key)
    {
        try
        {
            return key.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>SHA-256 of the key's SubjectPublicKeyInfo, lower-case hex: what package.sh writes into hivebdc-package.json.</summary>
    public static string Fingerprint(RSA key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
}

/// <summary>
/// The public half of Balsoft's update key, embedded at build time from <c>Hosting/update-key.pem</c> (or the file the
/// build property UpdateKeyFile names for a test build). A build without it cannot check signatures and never updates itself.
/// </summary>
public static class UpdateKey
{
    private const string ResourceName = "Balsoft.Hive.BDC.update-key.pem";

    public static RSA? Load()
    {
        using var stream = typeof(UpdateKey).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var rsa = RSA.Create();
        rsa.ImportFromPem(reader.ReadToEnd());
        return rsa;
    }
}