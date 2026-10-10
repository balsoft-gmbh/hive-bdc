using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>Outcome of a loopback API call — either data, or a clean "unreachable" reason.</summary>
public sealed record ApiResult<T>(T? Data, bool Reachable, string? Error)
{
    public static ApiResult<T> Ok(T data) => new(data, true, null);
    public static ApiResult<T> Down(string reason) => new(default, false, reason);
}

/// <summary>
/// Talks to the service's loopback status API. Discovers the endpoint from
/// %ProgramData%\HiveBDC\app-endpoint.json ({port, token}) — re-read on every
/// call because the service mints a fresh port + token on each start — and issues
/// bearer-authed GETs to http://127.0.0.1:{port}. Every failure mode (file missing,
/// connection refused, timeout, bad JSON) is turned into a "service not running /
/// not reachable" result rather than an exception. Read-only by design: this client
/// never sees invoice content, only the API's metadata.
/// </summary>
public sealed class LoopbackClient
{
    private static readonly string EndpointPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HiveBDC", "app-endpoint.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Write path: omit null properties so a blank ClientSecret is simply absent
    // (the service reads "absent secret" as "keep the stored one").
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Fast fail for the polling GETs; a generous window for POSTs (the SharePoint
    // test performs a real Graph round-trip and can take several seconds).
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly HttpClient _httpLong = new() { Timeout = TimeSpan.FromSeconds(60) };

    public Task<ApiResult<StatusDto>> GetStatusAsync(CancellationToken ct = default)
        => GetAsync<StatusDto>("/status", ct);

    public Task<ApiResult<MetricsDto>> GetMetricsAsync(int recent = 20, CancellationToken ct = default)
        => GetAsync<MetricsDto>($"/metrics?recent={recent}", ct);

    public Task<ApiResult<LogsDto>> GetLogsAsync(int tail, CancellationToken ct = default)
        => GetAsync<LogsDto>($"/logs?tail={tail}", ct);

    /// <summary>GET /config — full current settings (client secret is never returned).</summary>
    public Task<ApiResult<ConfigDto>> GetConfigAsync(CancellationToken ct = default)
        => GetAsync<ConfigDto>("/config", ct);

    /// <summary>POST /config — persist settings. Returns {ok, errors[]} even on a 400 validation reply.</summary>
    public Task<ApiResult<SaveConfigResult>> SaveConfigAsync(ConfigDto config, CancellationToken ct = default)
        => PostAsync<SaveConfigResult>("/config", config, ct);

    /// <summary>POST /config/test-sharepoint — dry-run a connection for one company's target
    /// (tested against the STORED shared auth; a blank secret in the request reuses it).</summary>
    public Task<ApiResult<TestSharePointResult>> TestSharePointAsync(TestSharePointRequestDto request, CancellationToken ct = default)
        => PostAsync<TestSharePointResult>("/config/test-sharepoint", request, ct);

    /// <summary>POST /config/test-mail — dry-run the mail app registration and sender mailbox.
    /// Sends no mail; a blank secret in the request reuses the stored one.</summary>
    public Task<ApiResult<TestMailResult>> TestMailAsync(TestMailRequestDto request, CancellationToken ct = default)
        => PostAsync<TestMailResult>("/config/test-mail", request, ct);

    /// <summary>POST /license — validate + install an uploaded .lic. Returns {ok,state,message,…}
    /// even on a 400 rejection (bad signature / wrong tenant / expired).</summary>
    public Task<ApiResult<LicenseUploadResult>> UploadLicenseAsync(LicenseUploadRequestDto request, CancellationToken ct = default)
        => PostAsync<LicenseUploadResult>("/license", request, ct);

    private async Task<ApiResult<T>> GetAsync<T>(string route, CancellationToken ct)
    {
        var endpoint = ReadEndpoint();
        if (endpoint is null)
            return ApiResult<T>.Down("Service not running (no endpoint file).");

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"http://127.0.0.1:{endpoint.Port}{route}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
                return ApiResult<T>.Down($"Service returned {(int)resp.StatusCode}.");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var data = await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
            return data is null
                ? ApiResult<T>.Down("Service returned an empty response.")
                : ApiResult<T>.Ok(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Connection refused (service stopped), timeout, malformed JSON — all just "not reachable".
            return ApiResult<T>.Down("Service not reachable.");
        }
    }

    /// <summary>
    /// POST <paramref name="body"/> as JSON and deserialize the reply. The reply body is read on
    /// BOTH success and 400 responses, because a 400 still carries a structured {ok:false, errors}
    /// payload the caller needs. Only transport-level failures (no service, refused, timeout,
    /// unparseable body) become a "not reachable" result — never an exception.
    /// </summary>
    private async Task<ApiResult<T>> PostAsync<T>(string route, object body, CancellationToken ct)
    {
        var endpoint = ReadEndpoint();
        if (endpoint is null)
            return ApiResult<T>.Down("Service not running (no endpoint file).");

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}{route}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            req.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonWriteOpts), Encoding.UTF8, "application/json");

            using var resp = await _httpLong.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var data = await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
            return data is null
                ? ApiResult<T>.Down("Service returned an empty response.")
                : ApiResult<T>.Ok(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ApiResult<T>.Down("Service not reachable.");
        }
    }

    private static EndpointFile? ReadEndpoint()
    {
        try
        {
            if (!File.Exists(EndpointPath)) return null;
            var json = File.ReadAllText(EndpointPath);
            var ep = JsonSerializer.Deserialize<EndpointFile>(json, JsonOpts);
            AppLog.Recovered("Reading the service endpoint file");
            return ep is { Port: > 0, Token.Length: > 0 } ? ep : null;
        }
        catch (Exception ex)
        {
            AppLog.Failed("Reading the service endpoint file", ex.Message);
            return null;
        }
    }
}
