using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Balsoft.Hive.BDC.Connectors;

public sealed record DocuWareField(string Name, string DisplayName, string Type, bool Required, int Length, IReadOnlyList<DocuWareField> Columns);

public sealed record DocuWareCabinet(string Id, string Name, IReadOnlyList<DocuWareField> Fields)
{
    public DocuWareField? Field(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record DocuWareCabinetSummary(string Id, string Name);

/// <summary>A DocuWare call failed. <see cref="RetryLater"/> is true when trying again later can succeed (sign-in, network, server busy).</summary>
public sealed class DocuWareException(string message, bool retryLater) : ConnectorException(message, retryLater);

/// <summary>
/// DocuWare Platform REST client for one connection: signs in through DocuWare's identity service (the only sign-in the
/// platform still accepts), reads file cabinets and their fields, stores a document with its index values, updates index
/// values and finds a document by one field. Tokens, cabinet fields and search dialogs are cached.
/// </summary>
public sealed class DocuWareClient
{
    private const string ClientId = "docuware.platform.net.client";
    private const string Scope = "docuware.platform";
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan CabinetCacheTime = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string _platform;
    private readonly string _userName;
    private readonly string _password;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenUntil;
    private readonly Dictionary<string, (DocuWareCabinet Cabinet, DateTimeOffset Until)> _cabinets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dialogs = new(StringComparer.OrdinalIgnoreCase);

    public DocuWareClient(string organizationUrl, string userName, string password, HttpMessageHandler? handler = null)
    {
        _platform = PlatformUrl(organizationUrl);
        _userName = userName;
        _password = password;
        _http = handler is null ? Shared : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>"https://x.docuware.cloud", ".../DocuWare/Platform/..." or ".../DocuWare/Platform/WebClient" all give ".../DocuWare/Platform".</summary>
    public static string PlatformUrl(string organizationUrl)
    {
        string url = (organizationUrl ?? "").Trim().TrimEnd('/');
        int at = url.IndexOf("/DocuWare/Platform", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return url[..at] + "/DocuWare/Platform";
        at = url.IndexOf("/DocuWare", StringComparison.OrdinalIgnoreCase);
        return (at >= 0 ? url[..at] : url) + "/DocuWare/Platform";
    }

    public async Task<IReadOnlyList<DocuWareCabinetSummary>> CabinetsAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "/FileCabinets", null, "read the file cabinets");
        var list = new List<DocuWareCabinetSummary>();
        if (doc.RootElement.TryGetProperty("FileCabinet", out var cabinets))
            foreach (var c in cabinets.EnumerateArray())
                if (!(c.TryGetProperty("IsBasket", out var basket) && basket.ValueKind == JsonValueKind.True))
                    list.Add(new DocuWareCabinetSummary(Text(c, "Id"), Text(c, "Name")));
        return list;
    }

    public async Task<DocuWareCabinet> CabinetAsync(string cabinetId)
    {
        if (_cabinets.TryGetValue(cabinetId, out var cached) && DateTimeOffset.UtcNow < cached.Until) return cached.Cabinet;
        using var doc = await SendAsync(HttpMethod.Get, $"/FileCabinets/{Uri.EscapeDataString(cabinetId)}", null, "read the file cabinet");
        var root = doc.RootElement;
        var fields = new List<DocuWareField>();
        if (root.TryGetProperty("Fields", out var all))
            foreach (var f in all.EnumerateArray())
                if (Text(f, "Scope") == "User")
                    fields.Add(ReadField(f));
        var cabinet = new DocuWareCabinet(Text(root, "Id"), Text(root, "Name"), fields);
        _cabinets[cabinetId] = (cabinet, DateTimeOffset.UtcNow.Add(CabinetCacheTime));
        return cabinet;
    }

    /// <summary>Stores the file with its index values; returns the new document's id.</summary>
    public async Task<string> StoreAsync(string cabinetId, JsonArray fields, DocumentFile file)
    {
        var index = new JsonObject { ["Fields"] = fields.DeepClone() };
        using var form = new MultipartFormDataContent();
        var indexPart = new StringContent(index.ToJsonString(), Encoding.UTF8, "application/json");
        form.Add(indexPart, "document", "document.json");
        var filePart = new ByteArrayContent(file.ReadAllBytes());
        filePart.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        form.Add(filePart, "file", file.Name);
        using var doc = await SendAsync(HttpMethod.Post, $"/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Documents", form, "store the document");
        string id = doc.RootElement.TryGetProperty("Id", out var v) ? v.ToString() : "";
        if (id.Length == 0) throw new DocuWareException("DocuWare stored the document but returned no document id.", retryLater: false);
        return id;
    }

    public async Task UpdateFieldsAsync(string cabinetId, string documentId, JsonArray fields)
    {
        var body = new JsonObject { ["Field"] = fields.DeepClone() };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var _ = await SendAsync(HttpMethod.Put,
            $"/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Documents/{Uri.EscapeDataString(documentId)}/Fields", content, "update the index values");
    }

    /// <summary>The id of a document whose <paramref name="field"/> equals <paramref name="value"/>, or null.</summary>
    public async Task<string?> FindAsync(string cabinetId, string field, string value)
        => (await QueryIdsAsync(cabinetId, field, value, 2)).FirstOrDefault();

    /// <summary>The ids of up to <paramref name="count"/> documents whose <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public async Task<IReadOnlyList<string>> QueryIdsAsync(string cabinetId, string field, string value, int count)
    {
        string dialog = await SearchDialogAsync(cabinetId);
        var body = new JsonObject
        {
            ["Condition"] = new JsonArray(new JsonObject { ["DBName"] = field, ["Value"] = new JsonArray(value) }),
            ["Operation"] = "And",
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var doc = await SendAsync(HttpMethod.Post,
            $"/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Query/DialogExpression?dialogId={Uri.EscapeDataString(dialog)}&count={Math.Clamp(count, 1, 500)}",
            content, "search the file cabinet");
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("Items", out var items))
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("Id", out var id)) ids.Add(id.ToString());
        return ids;
    }

    /// <summary>One stored document's index values: single values typed (string, decimal, int, DateTime, keyword list) and table rows.</summary>
    public async Task<SourceRecord> RecordAsync(string cabinetId, string documentId)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Documents/{Uri.EscapeDataString(documentId)}", null, "read the document");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("Fields", out var fields))
            foreach (var f in fields.EnumerateArray())
            {
                string name = Text(f, "FieldName");
                if (name.Length == 0) continue;
                if (f.TryGetProperty("Item", out var item) && item.ValueKind == JsonValueKind.Object
                    && Text(item, "$type") == "DocumentIndexFieldTable")
                {
                    var rows = new List<IReadOnlyDictionary<string, object?>>();
                    if (item.TryGetProperty("Row", out var rowList))
                        foreach (var row in rowList.EnumerateArray())
                        {
                            var cells = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            if (row.TryGetProperty("ColumnValue", out var colList))
                                foreach (var c in colList.EnumerateArray()) cells[Text(c, "FieldName")] = IndexValue(c);
                            rows.Add(cells);
                        }
                    tables[name] = rows;
                    continue;
                }
                values[name] = IndexValue(f);
            }
        return new SourceRecord(documentId, values, tables);
    }

    /// <summary>The document's file as stored (one file, or DocuWare's merged PDF when it has several).</summary>
    public async Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(string cabinetId, string documentId)
    {
        string token = await TokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_platform}/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Documents/{Uri.EscapeDataString(documentId)}/FileDownload?targetFileType=Auto&keepAnnotations=false");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new DocuWareException($"DocuWare could not be reached to download the file: {ex.Message}", retryLater: true); }
        using (response)
        {
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
                throw new DocuWareException($"DocuWare refused to download the file ({(int)response.StatusCode}): {ErrorText(Encoding.UTF8.GetString(bytes))}",
                    (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.Unauthorized);
            string name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? "";
            string type = response.Content.Headers.ContentType?.MediaType ?? "application/pdf";
            return (bytes, name.Trim('"'), type);
        }
    }

    /// <summary>Writes one field's value, e.g. the status after a record was handed over.</summary>
    public Task SetFieldAsync(string cabinetId, string documentId, string field, string value)
        => UpdateFieldsAsync(cabinetId, documentId, new JsonArray(new JsonObject
        {
            ["FieldName"] = field,
            ["Item"] = value,
            ["ItemElementName"] = "String",
        }));

    /// <summary>
    /// The time zone DocuWare dates are read in. A date stored at German midnight arrives as 23:00 UTC of the day before,
    /// so taking the UTC date would put it one day early.
    /// </summary>
    internal static readonly TimeZoneInfo BusinessTimeZone = FindZone("W. Europe Standard Time", "Europe/Berlin");

    private static TimeZoneInfo FindZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>An index value as .NET value: DocuWare sends dates as "/Date(ms)/" or ISO text, keywords as a list.</summary>
    internal static object? IndexValue(JsonElement field)
    {
        if (field.TryGetProperty("IsNull", out var isNull) && isNull.ValueKind == JsonValueKind.True) return null;
        if (!field.TryGetProperty("Item", out var item)) return null;
        string kind = Text(field, "ItemElementName");
        switch (item.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Number:
                return kind is "Int" && item.TryGetInt64(out var whole) ? whole : item.GetDecimal();
            case JsonValueKind.Object when Text(item, "$type") == "DocumentIndexFieldKeywords":
                return item.TryGetProperty("Keyword", out var list)
                    ? list.EnumerateArray().Select(k => k.GetString() ?? "").Where(k => k.Length > 0).ToList()
                    : new List<string>();
            case JsonValueKind.String:
                string text = item.GetString() ?? "";
                if (kind is "Date" or "DateTime")
                {
                    var ms = System.Text.RegularExpressions.Regex.Match(text, @"^/Date\((-?\d+)(?:[+-]\d{4})?\)/$");
                    if (ms.Success)
                    {
                        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(ms.Groups[1].Value)), BusinessTimeZone).DateTime;
                        return kind == "Date" ? local.Date : local;
                    }
                    if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date))
                        return date;
                }
                if (kind == "Decimal" && decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    return number;
                return text;
            default:
                return item.ToString();
        }
    }

    private async Task<string> SearchDialogAsync(string cabinetId)
    {
        if (_dialogs.TryGetValue(cabinetId, out var cached)) return cached;
        using var doc = await SendAsync(HttpMethod.Get, $"/FileCabinets/{Uri.EscapeDataString(cabinetId)}/Dialogs?DialogType=Search", null, "read the search dialogs");
        string id = doc.RootElement.TryGetProperty("Dialog", out var dialogs) && dialogs.GetArrayLength() > 0 ? Text(dialogs[0], "Id") : "";
        if (id.Length == 0) throw new DocuWareException("The file cabinet has no search dialog, so Hive BDC cannot look for an earlier copy. Remove the match field from the target or add a search dialog in DocuWare.", retryLater: false);
        return _dialogs[cabinetId] = id;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, HttpContent? content, string what)
    {
        string token = await TokenAsync();
        using var request = new HttpRequestMessage(method, _platform + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new DocuWareException($"DocuWare could not be reached to {what}: {ex.Message}", retryLater: true); }
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            if (response.StatusCode == HttpStatusCode.Unauthorized) _token = null;
            bool later = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                         || (int)response.StatusCode >= 500;
            throw new DocuWareException($"DocuWare refused to {what} ({(int)response.StatusCode}): {ErrorText(body)}", later);
        }
    }

    private async Task<string> TokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenUntil) return _token;
        await _tokenGate.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenUntil) return _token;
            string identity = Text(await GetPublicJsonAsync(_platform + "/Home/IdentityServiceInfo"), "IdentityServiceUrl");
            if (identity.Length == 0) throw new DocuWareException("DocuWare did not name its sign-in service. Check the DocuWare address in the connection.", retryLater: false);
            string tokenUrl = Text(await GetPublicJsonAsync(identity.TrimEnd('/') + "/.well-known/openid-configuration"), "token_endpoint");
            if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps)
                throw new DocuWareException("DocuWare named a sign-in address that is not https, so the password is not sent.", retryLater: false);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["scope"] = Scope,
                ["client_id"] = ClientId,
                ["username"] = _userName,
                ["password"] = _password,
            });
            HttpResponseMessage response;
            try { response = await _http.PostAsync(tokenUrl, form); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            { throw new DocuWareException($"DocuWare's sign-in service could not be reached: {ex.Message}", retryLater: true); }
            using (response)
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new DocuWareException($"DocuWare refused the sign-in for user '{_userName}': {ErrorText(body)}", retryLater: true);
                using var doc = JsonDocument.Parse(body);
                _token = Text(doc.RootElement, "access_token");
                int seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
                _tokenUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 120));
                return _token;
            }
        }
        finally { _tokenGate.Release(); }
    }

    private async Task<JsonElement> GetPublicJsonAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new DocuWareException($"DocuWare answered {(int)response.StatusCode} at {new Uri(url).Host}. Check the DocuWare address in the connection.", retryLater: (int)response.StatusCode >= 500);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new DocuWareException($"DocuWare could not be reached at {new Uri(url).Host}: {ex.Message}", retryLater: true); }
        catch (JsonException)
        { throw new DocuWareException($"{new Uri(url).Host} did not answer like a DocuWare system. Check the DocuWare address in the connection.", retryLater: false); }
    }

    private static DocuWareField ReadField(JsonElement f)
    {
        var columns = new List<DocuWareField>();
        if (f.TryGetProperty("TableFieldColumns", out var cols))
            foreach (var c in cols.EnumerateArray()) columns.Add(ReadField(c));
        int length = f.TryGetProperty("Length", out var l) && l.TryGetInt32(out var n) ? n : 0;
        bool required = f.TryGetProperty("NotEmpty", out var r) && r.ValueKind == JsonValueKind.True;
        return new DocuWareField(Text(f, "DBFieldName"), Text(f, "DisplayName"), Text(f, "DWFieldType"), required, length, columns);
    }

    private static string Text(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    /// <summary>DocuWare's own message from an error body ("Message" or "error_description"), else the start of the body.</summary>
    internal static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "Message", "error_description", "error" })
            {
                string text = Text(doc.RootElement, name);
                if (text.Length > 0) return text;
            }
        }
        catch (JsonException) { }
        string flat = body.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 300 ? flat : flat[..300];
    }
}