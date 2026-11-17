using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// A SharePoint document library as the source of an inbound flow: items whose column has the ready value; once handed
/// over, the column gets the done value. Uses the SharePoint app from Connections (application permission
/// Sites.ReadWrite.All or Sites.Selected with write). Settings SiteUrl, Library, ReadyColumn, ReadyValue, DoneValue.
/// </summary>
public sealed class SharePointRecordSource : IRecordSource
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly Dictionary<string, (string Token, DateTimeOffset Until)> Tokens = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly System.Text.RegularExpressions.Regex InternalName = new("^[A-Za-z_][A-Za-z0-9_]{0,254}$");

    private readonly SharePointAuthOptions? _auth;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _fileNames = new(StringComparer.Ordinal);

    public SharePointRecordSource(SharePointAuthOptions? auth, HttpMessageHandler? handler = null)
    {
        _auth = auth;
        _http = handler is null ? SharedHttp : new HttpClient(handler);
    }

    public SourceDescriptor Descriptor { get; } = new(
        TargetKinds.SharePoint, "SharePoint",
        "Files in a document library, picked up when a column has a value, then the column is set to imported.",
        TargetKinds.SharePoint, LicenseFeatures.SharePoint, "item", "SP", "{ID}.csv",
        new[]
        {
            new SourceSetting("SiteUrl", "Site address", SourceSteps.Which, Hint: "https://yourcompany.sharepoint.com/sites/Name"),
            new SourceSetting("Library", "Library", SourceSteps.Which, Lookup: "libraries", DependsOn: "SiteUrl"),
            new SourceSetting("ReadyColumn", "Ready when the column", SourceSteps.Which, Lookup: "columns", DependsOn: "Library"),
            new SourceSetting("ReadyValue", "has the value", SourceSteps.Which, Lookup: "values", DependsOn: "ReadyColumn",
                Hint: "SharePoint filters best on an indexed column (Library settings, Indexed columns)."),
            new SourceSetting("DoneValue", "Afterwards set the column to", SourceSteps.After, Lookup: "values", DependsOn: "ReadyColumn",
                Hint: "So the file is not picked up again, and people see in SharePoint that it reached the ERP."),
        });

    public bool IsConnected => _auth is not null && _auth.Validate().Count == 0;

    public IReadOnlyList<string> Problems(SourceOptions source)
    {
        var problems = new List<string>();
        if (!IsConnected)
            problems.Add("Set up SharePoint under Settings > Connections first (tenant ID, client ID and secret of the SharePoint app).");
        if (!Uri.TryCreate(source.Setting("SiteUrl"), UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
            problems.Add("Set the site address, e.g. https://yourcompany.sharepoint.com/sites/Name.");
        if (source.Setting("Library").Length == 0) problems.Add("Choose the library.");
        if (source.Setting("ReadyColumn").Length == 0 || source.Setting("ReadyValue").Length == 0)
            problems.Add("Choose the column and the value that mark a file ready.");
        else if (!InternalName.IsMatch(source.Setting("ReadyColumn")))
            problems.Add($"\"{source.Setting("ReadyColumn")}\" is not a column's internal name. Choose the column from the list.");
        if (source.Setting("DoneValue").Length == 0) problems.Add("Set the value written once a file is imported.");
        return problems;
    }

    public async Task<IReadOnlyList<string>> ReadyAsync(SourceOptions source, int max)
    {
        string column = source.Setting("ReadyColumn");
        if (!InternalName.IsMatch(column))
            throw new ConnectorException($"\"{column}\" is not a column's internal name. Choose the column from the list.", false);
        string filter = $"fields/{column} eq '{source.Setting("ReadyValue").Replace("'", "''")}'";
        string url = $"{await ListUrlAsync(source)}/items?$expand=fields($select=Id)&$filter={Uri.EscapeDataString(filter)}&$top={Math.Clamp(max, 1, 500)}";
        using var doc = await SendAsync(HttpMethod.Get, url, null, "list the ready files",
            prefer: "HonorNonIndexedQueriesWarningMayFailRandomly", column: column);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("id").GetString() ?? "")
            .Where(id => id.Length > 0).ToList();
    }

    public async Task<SourceRecord> ReadAsync(SourceOptions source, string id)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{await ListUrlAsync(source)}/items/{Uri.EscapeDataString(id)}?$expand=fields", null, "read the file's columns");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("fields", out var fields))
            foreach (var p in fields.EnumerateObject())
                if (!p.Name.StartsWith('@')) values[p.Name] = Value(p.Value);
        values["ID"] = id;
        if (values.TryGetValue("FileLeafRef", out var leaf) && leaf is string name)
        {
            values["FileName"] = name;
            _fileNames[id] = name;
        }
        if (doc.RootElement.TryGetProperty("webUrl", out var web)) values["WebUrl"] = web.GetString();
        return new SourceRecord(id, values, new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());
    }

    public async Task<DocumentFile?> FileAsync(SourceOptions source, string id)
    {
        if (!_fileNames.TryGetValue(id, out var name)) name = (await ReadAsync(source, id)).Values["FileName"] as string ?? id + ".pdf";
        string url = $"{await ListUrlAsync(source)}/items/{Uri.EscapeDataString(id)}/driveItem/content";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        using var response = await Send(request, "download the file");
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, Encoding.UTF8.GetString(bytes), "download the file", null);
        return DocumentFile.FromBytes(name, bytes);
    }

    public async Task<string> MarkDoneAsync(SourceOptions source, string id)
    {
        string column = source.Setting("ReadyColumn"), done = source.Setting("DoneValue");
        var body = new JsonObject { [column] = done };
        using var _ = await SendAsync(HttpMethod.Patch, $"{await ListUrlAsync(source)}/items/{Uri.EscapeDataString(id)}/fields",
            body.ToJsonString(), "set the column");
        return $"Marked {column} = {done} in SharePoint, so it is not imported again.";
    }

    public async Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, SourceOptions source)
    {
        if (!IsConnected)
            throw new ConnectorException("Set up SharePoint under Settings > Connections first (tenant ID, client ID and secret of the SharePoint app).", false);
        switch (lookup)
        {
            case "libraries":
                return (await ListsAsync(source)).Where(l => l.Library).Select(l => new SourceChoice(l.DisplayName, l.DisplayName, l.Name)).ToList();
            case "columns":
                return (await ColumnsAsync(source)).Where(c => !c.ReadOnly).Select(c => new SourceChoice(c.Name, c.DisplayName, c.Kind)).ToList();
            case "values":
                var column = (await ColumnsAsync(source)).FirstOrDefault(c => c.Name.Equals(source.Setting("ReadyColumn"), StringComparison.OrdinalIgnoreCase));
                return column?.Choices.Select(v => new SourceChoice(v, v)).ToList() ?? new List<SourceChoice>();
            case "fields":
                var list = (await ColumnsAsync(source)).Select(c => new SourceChoice(c.Name, c.DisplayName, c.Kind)).ToList();
                list.Add(new SourceChoice("ID", "Item id", "number"));
                list.Add(new SourceChoice("FileName", "File name", "text"));
                list.Add(new SourceChoice("WebUrl", "Address of the file", "text"));
                return list;
            default:
                return Array.Empty<SourceChoice>();
        }
    }

    public string RecordKey(SourceOptions source, string id) => $"{source.Setting("SiteUrl").TrimEnd('/')}/{source.Setting("Library")}|{id}";

    public string Scope(SourceOptions source)
        => string.Join("|", source.Setting("SiteUrl").TrimEnd('/'), source.Setting("Library"), source.Setting("ReadyColumn"), source.Setting("ReadyValue"))
            .ToLowerInvariant();

    // ── Graph ───────────────────────────────────────────────────────────────

    private sealed record ListInfo(string Id, string Name, string DisplayName, bool Library);

    private sealed record ColumnInfo(string Name, string DisplayName, string Kind, bool ReadOnly, IReadOnlyList<string> Choices);

    private async Task<string> SiteIdAsync(SourceOptions source)
    {
        if (!Uri.TryCreate(source.Setting("SiteUrl"), UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
            throw new ConnectorException("Set the site address first, e.g. https://yourcompany.sharepoint.com/sites/Name.", false);
        string key = AuthKey() + "|site|" + site.AbsoluteUri.TrimEnd('/');
        lock (Gate) if (Ids.TryGetValue(key, out var cached)) return cached;
        using var doc = await SendAsync(HttpMethod.Get, $"{GraphBase}/sites/{site.Host}:{site.AbsolutePath.TrimEnd('/')}?$select=id", null, "find the site");
        string id = doc.RootElement.GetProperty("id").GetString() ?? "";
        lock (Gate) Ids[key] = id;
        return id;
    }

    private async Task<IReadOnlyList<ListInfo>> ListsAsync(SourceOptions source)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{GraphBase}/sites/{await SiteIdAsync(source)}/lists?$select=id,name,displayName,list&$top=200",
            null, "list the libraries");
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(l => new ListInfo(
            l.GetProperty("id").GetString() ?? "",
            l.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            l.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
            l.TryGetProperty("list", out var info) && info.TryGetProperty("template", out var t) && t.GetString() == "documentLibrary")).ToList();
    }

    private async Task<string> ListUrlAsync(SourceOptions source)
    {
        string siteId = await SiteIdAsync(source);
        string library = source.Setting("Library");
        string key = AuthKey() + "|list|" + siteId + "|" + library;
        string? listId;
        lock (Gate) Ids.TryGetValue(key, out listId);
        if (listId is null)
        {
            var match = (await ListsAsync(source)).FirstOrDefault(l =>
                l.DisplayName.Equals(library, StringComparison.OrdinalIgnoreCase) || l.Name.Equals(library, StringComparison.OrdinalIgnoreCase))
                ?? throw new ConnectorException($"The site has no library \"{library}\". Choose one from the list.", false);
            listId = match.Id;
            lock (Gate) Ids[key] = listId;
        }
        return $"{GraphBase}/sites/{siteId}/lists/{listId}";
    }

    private async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(SourceOptions source)
    {
        if (source.Setting("Library").Length == 0) return Array.Empty<ColumnInfo>();
        using var doc = await SendAsync(HttpMethod.Get, $"{await ListUrlAsync(source)}/columns?$top=200", null, "read the library's columns");
        var list = new List<ColumnInfo>();
        foreach (var c in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (c.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True) continue;
            string kind = c.TryGetProperty("choice", out _) ? "choice"
                : c.TryGetProperty("dateTime", out _) ? "date"
                : c.TryGetProperty("number", out _) ? "number"
                : c.TryGetProperty("currency", out _) ? "currency"
                : c.TryGetProperty("boolean", out _) ? "yes/no"
                : c.TryGetProperty("personOrGroup", out _) ? "person"
                : c.TryGetProperty("lookup", out _) ? "lookup"
                : c.TryGetProperty("text", out _) ? "text" : "";
            var choices = c.TryGetProperty("choice", out var ch) && ch.TryGetProperty("choices", out var vals)
                ? vals.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                : new List<string>();
            list.Add(new ColumnInfo(c.GetProperty("name").GetString() ?? "", c.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                kind, c.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True, choices));
        }
        return list;
    }

    private static object? Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => DateValue(v.GetString()!) ?? (object)v.GetString()!,
        JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())),
        _ => null,
    };

    /// <summary>SharePoint sends dates in UTC; a date column holds midnight in Germany, so it is read back in that time zone.</summary>
    private static DateTime? DateValue(string s)
        => s.Length >= 20 && s[10] == 'T' && s.EndsWith('Z')
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)
            ? TimeZoneInfo.ConvertTime(utc, DocuWareClient.BusinessTimeZone).DateTime
            : null;

    private string AuthKey() => _auth is null ? "" : _auth.TenantId + "|" + _auth.ClientId;

    private async Task<string> TokenAsync()
    {
        if (_auth is null || _auth.Validate().Count > 0)
            throw new ConnectorException("Set up SharePoint under Settings > Connections first (tenant ID, client ID and secret of the SharePoint app).", false);
        string secret = SecretProtector.Unprotect(_auth.ClientSecret);
        string key = AuthKey() + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        lock (Gate)
            if (Tokens.TryGetValue(key, out var t) && DateTimeOffset.UtcNow < t.Until) return t.Token;
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _auth.ClientId,
            ["client_secret"] = secret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(_auth.TenantId)}/oauth2/v2.0/token") { Content = body };
        using var response = await Send(request, "sign in");
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new ConnectorException($"The SharePoint app could not sign in ({(int)response.StatusCode}): {ErrorText(json)}", (int)response.StatusCode >= 500);
        using var doc = JsonDocument.Parse(json);
        string token = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        int expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        lock (Gate) Tokens[key] = (token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expires - 300)));
        return token;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? json, string what, string? prefer = null, string? column = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Send(request, what);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text, what, column);
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, string what)
    {
        try { return await _http.SendAsync(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new ConnectorException($"SharePoint could not be reached to {what}: {ex.Message}", retryLater: true); }
    }

    private static ConnectorException Failure(HttpStatusCode status, string body, string what, string? column)
    {
        string message = ErrorText(body);
        if (column is not null && status == HttpStatusCode.BadRequest
            && (message.Contains("indexed", StringComparison.OrdinalIgnoreCase) || message.Contains("threshold", StringComparison.OrdinalIgnoreCase)))
            return new ConnectorException(
                $"SharePoint cannot pick files by the column {column}: {message} Index the column {column} in the library settings " +
                "(Library settings, Indexed columns); Hive BDC picks the files up on the next scan after that.", retryLater: false);
        bool later = (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized;
        return new ConnectorException($"SharePoint refused to {what} ({(int)status}): {message}", later);
    }

    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) return m.GetString() ?? "";
                if (e.ValueKind == JsonValueKind.String)
                    return doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : e.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}