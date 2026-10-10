using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>The small update manifest published beside the installers (latest.json).</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate);

/// <summary>Outcome of an update check.</summary>
public sealed record UpdateCheckResult(
    bool Available, string Current, string? Latest, UpdateManifest? Manifest, string? Error);

/// <summary>
/// Installer-based self-update. Reads a small latest.json from the public Balsoft blob
/// feed; if it names a newer version, downloads the signed Setup.exe, verifies it
/// (sha256 from the manifest fetched over HTTPS + a valid Authenticode signature whose
/// signer is Balsoft), then launches it elevated and silent to upgrade the service and
/// dashboard in place. The download is never trusted blindly — an unsigned, non-Balsoft,
/// or checksum-mismatched file is refused before anything runs.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Public read-only installer feed (see config/balsoft/bdc/bdc.yml update_feed).</summary>
    // The 2.0 feed is its own container ("hive-bdc") on the same storage account, so 1.x Hive
    // eInvoice dashboards polling "updates/" never see a Hive BDC release as an update.
    public const string DefaultFeedBaseUrl = "https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc/";

    private readonly string _feedBaseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public UpdateService(string? feedBaseUrl = null)
        => _feedBaseUrl = string.IsNullOrWhiteSpace(feedBaseUrl) ? DefaultFeedBaseUrl : feedBaseUrl!;

    private string ManifestUrl => _feedBaseUrl.TrimEnd('/') + "/latest.json";
    private string ChangelogUrl => _feedBaseUrl.TrimEnd('/') + "/changelog.json";

    /// <summary>Fetch the full changelog from the feed (may include versions newer than this build).
    /// Returns null when the feed has no changelog or is unreachable — callers fall back to the embedded copy.</summary>
    public async Task<List<ChangelogEntry>?> GetChangelogAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(ChangelogUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Info($"Changelog from the update feed not available ({(int)resp.StatusCode}); showing the built-in copy.");
                return null;
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonSerializer.DeserializeAsync<ChangelogDoc>(s, cancellationToken: ct);
            return doc?.Releases?.ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Changelog from the update feed could not be loaded, showing the built-in copy: " + ex.Message);
            return null;
        }
    }

    /// <summary>Fetch latest.json and decide whether it names a newer version than <paramref name="currentVersion"/>.
    /// A missing manifest (404 — no release published yet) is reported as "up to date", not an error.</summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        UpdateManifest? m;
        try
        {
            using var resp = await _http.GetAsync(ManifestUrl, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(false, currentVersion, null, null, null);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            m = await JsonSerializer.DeserializeAsync<UpdateManifest>(s, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, currentVersion, null, null, ex.Message);
        }

        if (m is null || string.IsNullOrWhiteSpace(m.Version) || string.IsNullOrWhiteSpace(m.Url))
            return new UpdateCheckResult(false, currentVersion, null, null, "Update manifest was empty or malformed.");

        return new UpdateCheckResult(IsNewer(currentVersion, m.Version!), currentVersion, m.Version, m, null);
    }

    /// <summary>True when <paramref name="latest"/> is a strictly higher version than <paramref name="current"/>.
    /// Tolerant of a leading 'v' and of 2–4 version components.</summary>
    public static bool IsNewer(string current, string latest) => UpdateHandoff.IsNewer(current, latest);

    /// <summary>Download the installer to a temp file and verify it (checksum + Balsoft Authenticode) before returning its path.</summary>
    public async Task<string> DownloadVerifiedAsync(UpdateManifest m, IProgress<int>? progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "HiveBDCUpdate");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, SafeFileName(m.Url!) ?? "HiveBDC-Setup.exe");

        using (var resp = await _http.GetAsync(m.Url!, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total is > 0) progress?.Report((int)(read * 100 / total.Value));
            }
        }

        // 1) integrity — sha256 must match the manifest (which came over HTTPS from our blob).
        if (!string.IsNullOrWhiteSpace(m.Sha256))
        {
            string actual = Sha256Hex(file);
            if (!string.Equals(actual, m.Sha256!.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
                throw new InvalidOperationException("The downloaded installer failed its checksum — update aborted.");
            }
        }

        // 2) authenticity — the installer must carry a valid Authenticode signature from Balsoft.
        var (signatureValid, signer) = await Task.Run(() =>
        {
            bool valid = InstallerSignature.Verify(file, out var detail);
            return (valid, detail);
        }, ct);
        if (!signatureValid)
        {
            TryDelete(file);
            throw new InvalidOperationException($"The downloaded installer is not signed by Balsoft ({signer}) — update aborted.");
        }

        return file;
    }

    /// <summary>
    /// Start the (already verified) installer silently and record the attempt, so the next
    /// dashboard start can tell whether it completed. The caller shuts the app down afterwards;
    /// Setup waits for the dashboard to exit before it copies files. Throws when Setup does not
    /// start or stops at once, and the dashboard then stays open with the reason.
    /// </summary>
    public PendingUpdate LaunchInstaller(string setupPath, string targetVersion, string currentVersion, string? logDir)
    {
        string dir = logDir ?? Path.GetDirectoryName(setupPath)!;
        string log = UpdateHandoff.SetupLogPath(dir, targetVersion, DateTime.Now);
        using var setup = UpdateHandoff.Start(setupPath, log, silent: true);
        var pending = new PendingUpdate(targetVersion, currentVersion, setupPath, log, setup.Id, DateTime.UtcNow);
        UpdateHandoff.Save(pending);
        return pending;
    }

    private static string Sha256Hex(string file)
    {
        using var fs = File.OpenRead(file);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static string? SafeFileName(string url)
    {
        try
        {
            string name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { /* best effort */ } }
}
