using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Delivery;

/// <summary>
/// Uploads generated e-invoice files to a SharePoint document library via
/// Microsoft Graph (app-only client credentials) and stamps list-item columns.
/// Plain HttpClient — no Graph SDK dependency. Built ONCE from the SHARED
/// <see cref="SharePointAuthOptions"/> and reused across every company: each
/// <see cref="Upload"/> call carries its own <see cref="SharePointTarget"/>, and the
/// per-site drive cache lets a single uploader serve multiple sites/libraries.
/// Tokens and resolved drives are cached for the lifetime of the instance.
/// </summary>
public sealed class SharePointUploader
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const int SimpleUploadLimit = 4 * 1024 * 1024;      // Graph small-upload cap
    private const int UploadChunkSize = 16 * 327_680;           // upload-session chunks must be 320 KiB multiples

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly SharePointAuthOptions _auth;
    private readonly ILogger _log;
    private string? _token;
    private DateTimeOffset _tokenExpiresUtc;
    private readonly Dictionary<string, string> _driveIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _foldersEnsured = new(StringComparer.OrdinalIgnoreCase);

    public SharePointUploader(SharePointAuthOptions auth, ILogger log)
    {
        _auth = auth;
        _log = log;
    }

    public SharePointAuthOptions Auth => _auth;

    /// <summary>
    /// Uploader when the SHARED auth is usable; null otherwise (problems are logged).
    /// Enablement is now per company — the caller passes a null uploader (or skips the
    /// Upload) for companies whose SharePoint is disabled.
    /// </summary>
    public static SharePointUploader? Create(SharePointAuthOptions? auth, ILogger log)
    {
        if (auth is null) return null;
        var problems = auth.Validate();
        if (problems.Count == 0) return new SharePointUploader(auth, log);

        foreach (var p in problems)
            log.LogError("SharePoint export is enabled for a company but the shared auth is misconfigured: {Problem}", p);
        log.LogError("SharePoint export is DISABLED for this run — fix the HiveBDC:SharePointAuth section in appsettings.json and restart.");
        return null;
    }

    /// <summary>
    /// Config smoke test (--test-sharepoint, loopback /config/test-sharepoint): acquire a token
    /// with the shared auth and, when a target is supplied, resolve its drive. No upload/write.
    /// </summary>
    public static async Task<bool> TestAsync(SharePointAuthOptions? auth, SharePointTarget? target, ILogger log)
    {
        if (auth is null)
        {
            log.LogError("No usable HiveBDC:SharePointAuth configuration found in appsettings.json.");
            return false;
        }
        var problems = auth.Validate();
        if (problems.Count > 0)
        {
            foreach (var p in problems) log.LogError("SharePoint auth problem: {Problem}", p);
            return false;
        }
        try
        {
            var uploader = new SharePointUploader(auth, log);
            await uploader.GetTokenAsync();
            log.LogInformation("Token acquired for tenant {Tenant}.", auth.TenantId);
            if (target is not null)
            {
                string driveId = await uploader.GetDriveIdAsync(target);
                log.LogInformation("Target resolved. site={Site} library={Library} driveId={DriveId}",
                    target.SiteUrl, target.Library, driveId);
            }
            else
            {
                log.LogInformation("Auth OK; no target supplied — each company's SharePoint site/library resolves on first upload.");
            }
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("SharePoint test FAILED: {Message}", ex.Message);
            return false;
        }
    }

    public void Upload(string filePath, SharePointTarget target, IReadOnlyDictionary<string, string> fields)
        => UploadAsync(filePath, target, fields).GetAwaiter().GetResult();

    public async Task UploadAsync(string filePath, SharePointTarget target, IReadOnlyDictionary<string, string> fields)
    {
        string driveId = await GetDriveIdAsync(target);
        await EnsureFolderAsync(driveId, target.Folder);

        string fileName = Path.GetFileName(filePath);
        string itemPath = string.IsNullOrWhiteSpace(target.Folder)
            ? EscapePath(fileName)
            : EscapePath(target.Folder.Trim('/', '\\')) + "/" + EscapePath(fileName);

        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        string itemId;
        if (bytes.Length <= SimpleUploadLimit)
        {
            (itemId, bool replaced) = await UploadSmallAsync(driveId, itemPath, bytes);
            if (replaced)
                _log.LogWarning("SharePoint: {File} already existed in {Library} and was replaced (SharePoint keeps the old one as a previous version).",
                    fileName, target.Library);
        }
        else
            itemId = await UploadLargeAsync(driveId, itemPath, bytes);

        if (fields.Count > 0)
            await SetFieldsAsync(driveId, itemId, fields);
        else
            _log.LogWarning("SharePoint: {File} uploaded without any column values (no column templates resolved). Set them under Settings > Document types > company > Column templates.",
                fileName);
    }

    // ── Graph plumbing ──────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresUtc)
            return _token;

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _auth.ClientId,
            ["client_secret"] = SecretProtector.Unprotect(_auth.ClientSecret),
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        });
        using var resp = await Http.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(_auth.TenantId)}/oauth2/v2.0/token", body);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed ({(int)resp.StatusCode}): {ErrorSummary(json)}");

        using var doc = JsonDocument.Parse(json);
        _token = doc.RootElement.GetProperty("access_token").GetString()
                 ?? throw new InvalidOperationException("Token response contained no access_token.");
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
        return _token;
    }

    private async Task<string> GetDriveIdAsync(SharePointTarget target)
    {
        string key = target.CacheKey;
        if (_driveIdCache.TryGetValue(key, out var cached)) return cached;

        var siteUri = new Uri(target.SiteUrl);
        string sitePath = siteUri.AbsolutePath.TrimEnd('/');
        using var siteDoc = await GetJsonAsync($"{GraphBase}/sites/{siteUri.Host}:{sitePath}?$select=id,displayName");
        string siteId = siteDoc.RootElement.GetProperty("id").GetString()!;
        _log.LogInformation("SharePoint: site {Url} resolved ({Site}); looking up library {Library}.",
            target.SiteUrl, siteDoc.RootElement.GetProperty("displayName").GetString(), target.Library);

        // The configured library may be the URL name (the segment after the site in the
        // library's address, e.g. "SalesInvoices", which is what people copy),
        // the display name ("Rechnungen und Gutschriften"), or the library's full URL. Match
        // each drive by its display name AND by the last segment of its webUrl.
        string wanted = LibraryKey(target.Library);
        using var drivesDoc = await GetJsonAsync($"{GraphBase}/sites/{siteId}/drives?$select=id,name,webUrl&$top=200");
        foreach (var d in drivesDoc.RootElement.GetProperty("value").EnumerateArray())
        {
            string name = d.GetProperty("name").GetString() ?? "";
            string urlName = d.TryGetProperty("webUrl", out var w) ? LibraryKey(w.GetString()) : "";
            if (string.Equals(name, target.Library.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(LibraryKey(name), wanted, StringComparison.OrdinalIgnoreCase)
                || (urlName.Length > 0 && string.Equals(urlName, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                _log.LogInformation("SharePoint: library {Library} resolved to \"{Name}\" ({Url}).", target.Library, name,
                    d.TryGetProperty("webUrl", out var u) ? u.GetString() : "");
                return _driveIdCache[key] = d.GetProperty("id").GetString()!;
            }
        }

        try
        {
            using var listDrive = await GetJsonAsync(
                $"{GraphBase}/sites/{siteId}/lists/{Uri.EscapeDataString(target.Library.Trim())}/drive?$select=id");
            return _driveIdCache[key] = listDrive.RootElement.GetProperty("id").GetString()!;
        }
        catch (Exception)
        {
            var names = drivesDoc.RootElement.GetProperty("value").EnumerateArray()
                .Select(d =>
                {
                    string name = d.GetProperty("name").GetString() ?? "";
                    string urlName = d.TryGetProperty("webUrl", out var w) ? LibraryKey(w.GetString()) : "";
                    return urlName.Length > 0 && !string.Equals(urlName, name, StringComparison.OrdinalIgnoreCase)
                        ? $"{name} (URL name: {urlName})" : name;
                }).ToArray();
            throw new InvalidOperationException(
                $"Document library '{target.Library}' not found on {target.SiteUrl}. Use the name from the library's URL or its display name. " +
                $"Libraries on the site: {string.Join(", ", names)}");
        }
    }

    /// <summary>
    /// The comparable form of a library reference: the last path segment, URL-decoded and
    /// trimmed of slashes. Works for a bare name, a URL name, and a full library URL
    /// (".../sites/X/SalesInvoices" or ".../Forms/AllItems.aspx" is reduced to the library segment).
    /// </summary>
    internal static string LibraryKey(string? value)
    {
        string s = (value ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        if (s.Length == 0) return "";
        // A pasted "…/Library/Forms/AllItems.aspx" points at the library one level up.
        int forms = s.IndexOf("/Forms/", StringComparison.OrdinalIgnoreCase);
        if (forms > 0) s = s[..forms];
        int cut = s.LastIndexOf('/');
        if (cut >= 0) s = s[(cut + 1)..];
        return Uri.UnescapeDataString(s);
    }

    private async Task EnsureFolderAsync(string driveId, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string ensuredKey = driveId + "\n" + folder;
        if (_foldersEnsured.Contains(ensuredKey)) return;

        string parent = "";
        foreach (var segment in folder.Trim('/', '\\').Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            string parentUrl = parent.Length == 0
                ? $"{GraphBase}/drives/{driveId}/root/children"
                : $"{GraphBase}/drives/{driveId}/root:/{parent}:/children";
            var payload = new JsonObject
            {
                ["name"] = segment,
                ["folder"] = new JsonObject(),
                ["@microsoft.graph.conflictBehavior"] = "fail",
            };
            using var req = await NewRequestAsync(HttpMethod.Post, parentUrl);
            req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Conflict)   // 409 = already exists
                throw new InvalidOperationException(
                    $"Could not create folder '{segment}' in the library ({(int)resp.StatusCode}): {ErrorSummary(await resp.Content.ReadAsStringAsync())}");
            parent = parent.Length == 0 ? EscapePath(segment) : parent + "/" + EscapePath(segment);
        }
        _foldersEnsured.Add(ensuredKey);
    }

    /// <summary>PUT the file; Graph answers 201 for a new file and 200 when it replaced one.</summary>
    private async Task<(string Id, bool Replaced)> UploadSmallAsync(string driveId, string itemPath, byte[] bytes)
    {
        using var req = await NewRequestAsync(HttpMethod.Put, $"{GraphBase}/drives/{driveId}/root:/{itemPath}:/content");
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await Http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Upload failed ({(int)resp.StatusCode}): {ErrorSummary(json)}");
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("id").GetString()!, resp.StatusCode == HttpStatusCode.OK);
    }

    private async Task<string> UploadLargeAsync(string driveId, string itemPath, byte[] bytes)
    {
        using var createReq = await NewRequestAsync(
            HttpMethod.Post, $"{GraphBase}/drives/{driveId}/root:/{itemPath}:/createUploadSession");
        createReq.Content = new StringContent(
            """{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""", Encoding.UTF8, "application/json");
        using var createResp = await Http.SendAsync(createReq);
        string createJson = await createResp.Content.ReadAsStringAsync();
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"createUploadSession failed ({(int)createResp.StatusCode}): {ErrorSummary(createJson)}");
        string uploadUrl;
        using (var doc = JsonDocument.Parse(createJson))
            uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString()!;

        for (long offset = 0; offset < bytes.Length; offset += UploadChunkSize)
        {
            int size = (int)Math.Min(UploadChunkSize, bytes.Length - offset);
            using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl);   // pre-authenticated URL, no bearer
            chunkReq.Content = new ByteArrayContent(bytes, (int)offset, size);
            chunkReq.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + size - 1, bytes.Length);
            using var chunkResp = await Http.SendAsync(chunkReq);
            string chunkJson = await chunkResp.Content.ReadAsStringAsync();
            if (!chunkResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Chunk upload failed ({(int)chunkResp.StatusCode}): {ErrorSummary(chunkJson)}");
            if (chunkResp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(chunkJson);
                if (doc.RootElement.TryGetProperty("id", out var id))
                    return id.GetString()!;
            }
        }
        throw new InvalidOperationException("Upload session finished without returning the drive item.");
    }

    private async Task SetFieldsAsync(string driveId, string itemId, IReadOnlyDictionary<string, string> fields)
    {
        var payload = new JsonObject();
        foreach (var (k, v) in fields) payload[k] = v;

        using var req = await NewRequestAsync(HttpMethod.Patch, $"{GraphBase}/drives/{driveId}/items/{itemId}/listItem/fields");
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            _log.LogInformation("SharePoint: columns set: {Fields}", string.Join("; ", fields.Select(f => $"{f.Key}={f.Value}")));
            return;
        }

        // The file is uploaded at this point and a retry would only repeat the same rejected
        // patch, so this is reported loudly and left behind rather than thrown: a document must
        // not go undelivered because a column name in the settings is wrong.
        string summary = ErrorSummary(await resp.Content.ReadAsStringAsync());
        _log.LogError(
            "SharePoint: the file was uploaded but its columns were NOT set ({Status}): {Error}. " +
            "The document itself is fine and processing continues; fix the column names in Settings.",
            (int)resp.StatusCode, summary);

        await LogAvailableColumnsAsync(driveId, fields.Keys);
    }

    /// <summary>
    /// After a rejected column patch, name the columns the library actually has. Without this
    /// the message says which name is wrong but not which names are right, and SharePoint's
    /// INTERNAL names are not what the browser shows: a column renamed after it was created
    /// keeps its original internal name, which is the usual cause.
    /// </summary>
    private async Task LogAvailableColumnsAsync(string driveId, IEnumerable<string> attempted)
    {
        try
        {
            using var listDoc = await GetJsonAsync($"{GraphBase}/drives/{driveId}/list?$select=id");
            string listId = listDoc.RootElement.GetProperty("id").GetString()!;

            using var colDoc = await GetJsonAsync(
                $"{GraphBase}/lists/{listId}/columns?$select=name,displayName,readOnly&$top=200");

            var writable = new List<string>();
            foreach (var c in colDoc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (c.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True) continue;
                string name = c.GetProperty("name").GetString() ?? "";
                string display = c.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                writable.Add(string.Equals(name, display, StringComparison.Ordinal) ? name : $"{name} (shown as \"{display}\")");
            }

            var known = new HashSet<string>(
                writable.Select(w => w.Split(' ')[0]), StringComparer.OrdinalIgnoreCase);
            var unknown = attempted.Where(a => !known.Contains(a)).ToArray();

            if (unknown.Length > 0)
                _log.LogError("SharePoint: no column named {Unknown} in this library.", string.Join(", ", unknown));

            _log.LogError("SharePoint: writable column names in this library are: {Columns}", string.Join(", ", writable));
        }
        catch (Exception ex)
        {
            _log.LogWarning("SharePoint: could not list the library's columns to help with the above ({Message}).", ex.Message);
        }
    }

    /// <summary>Graph read that returns the parsed body, or throws with a one-line summary.</summary>
    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var req = await NewRequestAsync(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {url} failed ({(int)resp.StatusCode}): {ErrorSummary(json)}");
        return JsonDocument.Parse(json);
    }

    private async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync());
        return req;
    }

    private static string EscapePath(string path)
        => string.Join("/", path.Split('/', '\\').Select(Uri.EscapeDataString));

    /// <summary>Compact one-line error from a Graph/AAD error response body (never includes secrets).</summary>
    private static string ErrorSummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object)
                    return $"{err.GetProperty("code").GetString()}: {err.GetProperty("message").GetString()}";
                if (err.ValueKind == JsonValueKind.String)
                {
                    string desc = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
                    return $"{err.GetString()}: {desc.Split('\n')[0]}";
                }
            }
        }
        catch { /* not JSON */ }
        return body.Length > 300 ? body[..300] : body;
    }
}
