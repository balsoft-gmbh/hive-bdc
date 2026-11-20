using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Loopback control/status API for the companion desktop app. Runs a minimal ASP.NET Core
/// (Kestrel) server on 127.0.0.1 on an ephemeral port and publishes {port, token} to
/// %ProgramData%\HiveBDC\app-endpoint.json. Every request must present the
/// bearer token and originate from loopback. Endpoints:
///   GET  /status                 — running, version, config, document types, license state
///   GET  /metrics                — ledger counts (total + per document type) + recent exports
///   GET  /logs?tail=N            — last N lines of the current log file
///   GET  /config                 — current settings (client secret NEVER returned)
///   POST /config                 — write settings to appsettings.json atomically
///   POST /config/test-sharepoint — dry-run the SharePoint connection (writes nothing)
///   POST /license                — validate + install an uploaded .lic (renew from the app)
///
/// The status/metrics/logs surface is observational. /config writes appsettings.json
/// ATOMICALLY (temp file + move-over-replace) so the reload watcher never reads a
/// half-written file; the client secret is DPAPI-encrypted before it lands on disk
/// and is never echoed back. Document content is never read here.
/// </summary>
public sealed partial class LocalStatusServer : BackgroundService
{
    private const string EndpointFileName = "app-endpoint.json";
    private const string SettingsSection = SettingsFile.Section;
    private const string LegacySettingsSection = SettingsFile.LegacySection;   // 1.x section, migrated on write

    private readonly IOptionsMonitor<BDCOptions> _optsMonitor;
    private readonly IStateStore _ledger;
    private readonly ILogger<LocalStatusServer> _log;
    private readonly string? _logDir;
    private readonly string _version;
    private readonly string _settingsPath;
    private readonly string _endpointDir;
    private readonly ISettingsDocument _settings;
    private readonly ILicenseStore _licenseStore;
    private readonly ILicenseTenant _licenseTenant;

    private readonly string _token = Base64Url(RandomNumberGenerator.GetBytes(32));

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public LocalStatusServer(
        IOptionsMonitor<BDCOptions> opts, IStateStore ledger, ILogger<LocalStatusServer> log,
        string? logDir, string version, string settingsPath, string? endpointDir = null,
        ISettingsDocument? settings = null, ILicenseStore? licenseStore = null, ILicenseTenant? licenseTenant = null)
    {
        _optsMonitor = opts;
        _ledger = ledger;
        _log = log;
        _logDir = logDir;
        _version = version;
        _settingsPath = settingsPath;
        _endpointDir = endpointDir ?? Path.GetDirectoryName(ExportLedger.DefaultDbPath)!;
        _settings = settings ?? new FileSettingsDocument(settingsPath);
        _licenseStore = licenseStore ?? new FileLicenseStore();
        _licenseTenant = licenseTenant ?? new SettingsLicenseTenant();
    }

    /// <summary>Current options snapshot, normalized (idempotent) for display.</summary>
    private BDCOptions Snapshot()
    {
        var o = _optsMonitor.CurrentValue;
        o.Normalize();
        return o;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WebApplication app;
        int port;
        try
        {
            // An empty builder: no configuration files, no environment (ASPNETCORE_URLS would move the listener), no
            // logging providers of its own; only Kestrel on the loopback address with a port the OS picks.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            app = builder.Build();
            app.Run(Serve);
            await app.StartAsync(stoppingToken);
            port = new Uri(app.Urls.First()).Port;
            WriteEndpointFile(port);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local status API could not start; the desktop app will not see this service.");
            return;
        }

        _log.LogInformation("Local status API listening on http://127.0.0.1:{Port} (token in {File}).", port, Path.Combine(_endpointDir, EndpointFileName));
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
        _log.LogInformation("Local status API stopped.");
    }

    /// <summary>Kestrel's side: the request becomes an <see cref="ApiCall"/>, the handler's answer goes back as JSON.</summary>
    private async Task Serve(HttpContext http)
    {
        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(http.RequestAborted);
        var query = http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var call = new ApiCall(http.Request.Method, http.Request.Path.Value ?? "", query, body);
        await Handle(call, http.Connection.RemoteIpAddress, http.Request.Headers.Authorization.ToString());
        http.Response.StatusCode = call.Status;
        http.Response.ContentType = "application/json";
        http.Response.ContentLength = call.Answer.Length;
        await http.Response.Body.WriteAsync(call.Answer, http.RequestAborted);
    }

    internal async Task Handle(ApiCall call, IPAddress? remote, string? auth)
    {
        try
        {
            if (remote is null || !IPAddress.IsLoopback(remote))
            { Respond(call, 403, new { error = "loopback only" }); return; }

            if (auth is null || !auth.StartsWith("Bearer ", StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(auth["Bearer ".Length..]), Encoding.UTF8.GetBytes(_token)))
            { Respond(call, 401, new { error = "unauthorized" }); return; }

            await Route(call);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local API {Request} failed.", ForLog($"{call.Method} {call.Path}"));
            try { Respond(call, 500, new { error = "internal error" }); } catch { /* ignore */ }
        }
    }

    /// <summary>The API itself, once the caller is allowed in (token on loopback locally, sign-in and role in Azure).</summary>
    internal async Task Route(ApiCall call)
    {
        try
        {
            if (call.Path == "/service/updates" || call.Path.StartsWith("/service/updates/", StringComparison.Ordinal))
            {
                await RouteUpdates(call);
                return;
            }
            switch ((call.Method, call.Path))
            {
                case ("GET", "/status"): Respond(call, 200, Status()); return;
                case ("GET", "/metrics"): Respond(call, 200, Metrics(RecentParam(call))); return;
                case ("GET", "/logs"): Respond(call, 200, Logs(TailParam(call))); return;
                case ("GET", "/history"): Respond(call, 200, History(call)); return;
                case ("GET", "/history/attention"): Respond(call, 200, Attention(call)); return;
                case ("POST", "/history/retry"): PostRetry(call); return;
                case ("POST", "/config/test-docuware"): await PostTestDocuWare(call); return;
                case ("POST", "/config/docuware-fields"): await PostDocuWareFields(call); return;
                case ("GET", "/sources"): Respond(call, 200, Sources()); return;
                case ("POST", "/sources/choices"): await PostSourceChoices(call); return;
                case ("POST", "/sources/sample"): await PostSourceSample(call); return;
                case ("POST", "/sources/preview"): await PostSourcePreview(call); return;
                case ("GET", "/targets"): Respond(call, 200, Targets()); return;
                case ("POST", "/targets/choices"): await PostTargetChoices(call); return;
                case ("GET", "/connections/kinds"): Respond(call, 200, ConnectionKindsList()); return;
                case ("GET", "/setup/identity"): GetSetupIdentity(call); return;
                case ("POST", "/setup/check-source"): PostCheckSource(call); return;
                case ("POST", "/setup/check-queue"): await PostCheckQueue(call); return;
                case ("GET", "/export/sample"): GetExportSample(call); return;
                case ("POST", "/setup/test-run"): await PostTestRun(call); return;
                case ("POST", "/setup/start"): PostPaused(call, paused: false); return;
                case ("POST", "/setup/pause"): PostPaused(call, paused: true); return;
                case ("POST", "/connections/test"): await PostConnectionTest(call); return;
                case ("GET", "/config"): GetConfig(call); return;
                case ("POST", "/config"): PostConfig(call); return;
                case ("POST", "/config/test-sharepoint"): await PostTestSharePoint(call); return;
                case ("POST", "/config/test-mail"): await PostTestMail(call); return;
                case ("POST", "/license"): PostLicense(call); return;
                default: Respond(call, 404, new { error = "not found" }); return;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local API {Request} failed.", ForLog($"{call.Method} {call.Path}"));
            try { Respond(call, 500, new { error = "internal error" }); } catch { /* ignore */ }
        }
    }

    // ── GET /status /metrics /logs (observational) ──────────────────────────

    private object Status()
    {
        var opts = Snapshot();
        var scope = _licenseTenant.Scope(opts);
        var lic = _licenseStore.Check(opts, scope, DateOnly.FromDateTime(DateTime.Today));
        return new
        {
            running = true,
            version = _version,
            companyCount = opts.CompanyIds().Count,
            companies = opts.CompanyIds().Select(id => new
            {
                dataAreaId = id,
                sharePointEnabled = opts.ActiveRoutes().Any(r => string.Equals(r.DataAreaId, id, StringComparison.OrdinalIgnoreCase) && r.SharePoint.Enabled),
            }).ToArray(),
            folderCount = opts.ScanFolders().Count,
            pollSeconds = opts.PollSeconds,
            sharePoint = opts.AnySharePointEnabled(),   // any entry of an enabled type uploading
            documentTypeCount = opts.DocumentTypes.Count,
            documentTypes = opts.DocumentTypes.Select(t => new
            {
                id = t.Id,
                displayName = t.EffectiveDisplayName,
                party = t.Party.ToString(),
                handler = t.Handler.ToString(),
                enabled = t.Enabled,
                mail = t.Mail,
                sharePoint = t.Companies.Any(r => r.SharePoint.Enabled),
                companyCount = t.Companies.Count,
                discovered = t.Discovered,
                // Whether the installed license lets this type flow right now.
                licensed = lic.HasFeature(DocumentHandlers.For(t.Handler).RequiredFeature),
            }).ToArray(),
            license = new
            {
                state = lic.State.ToString(),
                message = lic.Message,
                customer = lic.Info?.Customer,
                expires = lic.ExpiresOn?.ToString("yyyy-MM-dd"),
                features = lic.Blocked ? Array.Empty<string>() : (lic.Info?.Features ?? new List<string>()).ToArray(),
                blocked = lic.Blocked,   // true => processing is stopped; the dashboard shows a "stopped" banner
                instanceId = lic.Info?.InstanceId is { Length: > 0 } bound ? bound.Trim() : null,   // the Azure instance the licence is bound to
                thisInstance = scope.IsLocal || scope.InstanceId == LicenseScope.UnknownInstance ? null : scope.InstanceId,
            },
        };
    }

    private object Metrics(int recent = 20)
    {
        var m = _ledger.Metrics(recent);
        return new
        {
            today = m.Today,
            month = m.Month,
            total = m.Total,
            monthByType = m.MonthByType.Select(t => new { documentType = t.DocumentType, count = t.Count }).ToArray(),
            recent = m.Recent.Select(e => new
            {
            documentId = e.DocumentId,
            documentType = e.DocumentType,
            format = e.Format,
            source = e.Source,
            outputFile = e.OutputFile,
            targetSite = e.TargetSite,
            sizeBytes = e.SizeBytes,
            exportedAt = e.ExportedAt,
            dataAreaId = e.DataAreaId,
            }),
        };
    }

    private object Logs(int tail)
    {
        if (_logDir is null) return new { lines = Array.Empty<string>(), note = "console-only, no log file" };
        try
        {
            var file = new DirectoryInfo(_logDir)
                .EnumerateFiles("hive-bdc-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null) return new { lines = Array.Empty<string>() };

            // The dashboard asks for the last N lines every few seconds. Read backwards from the
            // end in 64 KiB blocks until N newlines are seen instead of streaming the whole day.
            return new { file = file.Name, lines = TailLines(file.FullName, Math.Clamp(tail, 1, 5000)) };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Local API: reading the log for the dashboard failed.");
            return new { lines = Array.Empty<string>(), error = "log read failed" };
        }
    }

    /// <summary>
    /// GET /history?search=&amp;limit=: the steps of one document (search by its id or number), or the newest steps of
    /// all documents when search is blank.
    /// </summary>
    private object History(ApiCall call)
    {
        string? search = call.Query("search");
        int limit = int.TryParse(call.Query("limit"), out var n) ? Math.Clamp(n, 1, 5000) : 500;
        var events = _ledger.Events(search, limit);
        return new
        {
            search = search ?? "",
            events = events.Select(e => new
            {
                id = e.Id,
                at = e.At,
                documentId = e.DocumentId,
                documentNumber = e.DocumentNumber,
                documentType = e.DocumentType,
                dataAreaId = e.DataAreaId,
                input = e.Input,
                step = e.Step,
                target = e.Target,
                state = e.State,
                message = e.Message,
            }).ToArray(),
        };
    }

    /// <summary>Last <paramref name="count"/> lines of a UTF-8 text file, read from the end.</summary>
    private static List<string> TailLines(string path, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int block = 64 * 1024;
        long pos = fs.Length;
        var chunks = new List<byte[]>();
        int newlines = 0;
        var buf = new byte[block];
        while (pos > 0 && newlines <= count)
        {
            int size = (int)Math.Min(block, pos);
            pos -= size;
            fs.Seek(pos, SeekOrigin.Begin);
            int read = 0;
            while (read < size) { int n = fs.Read(buf, read, size - read); if (n <= 0) break; read += n; }
            var chunk = new byte[read];
            Array.Copy(buf, chunk, read);
            chunks.Add(chunk);
            for (int i = 0; i < read; i++) if (chunk[i] == (byte)'\n') newlines++;
        }
        chunks.Reverse();
        int total = chunks.Sum(c => c.Length);
        var all = new byte[total];
        int off = 0;
        foreach (var c in chunks) { Array.Copy(c, 0, all, off, c.Length); off += c.Length; }
        string text = Encoding.UTF8.GetString(all);
        var lines = text.Split('\n', StringSplitOptions.None).Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);   // trailing newline
        if (pos > 0 && lines.Count > 0) lines.RemoveAt(0);                              // partial first line of the window
        return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
    }

    // ── settings I/O ────────────────────────────────────────────────────────

    private static int TailParam(ApiCall call)
        => int.TryParse(call.Query("tail"), out var n) ? n : 200;

    private static int RecentParam(ApiCall call)
        => int.TryParse(call.Query("recent"), out var n) ? Math.Clamp(n, 1, 5000) : 20;

    private void WriteEndpointFile(int port)
    {
        string dir = _endpointDir;
        Directory.CreateDirectory(dir);
        // Publish the resolved log directory too, so the dashboard app can co-log into
        // the SAME rolling file the service writes (one unified, app-visible log).
        string json = JsonSerializer.Serialize(new { port, token = _token, logDir = _logDir });
        // Written under a temporary name and moved, so the dashboard (and a test) never reads half a file.
        Documents.CsvFile.WriteAtomic(Path.Combine(dir, EndpointFileName), System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>A caller-supplied value for the log, without line breaks that could fake further log entries.</summary>
    internal static string ForLog(string value) => value.Replace("\r", " ").Replace("\n", " ");

    private void Respond(ApiCall call, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        // Every refused or failed dashboard request lands in the log with its reason, so a
        // "could not save" in the dashboard can be explained from the log alone. Bodies of
        // error answers carry messages and validation errors only, never settings or secrets.
        if (status >= 400)
        {
            string what = ForLog($"{call.Method} {call.Path}");
            string reason = ForLog(Encoding.UTF8.GetString(bytes));
            if (reason.Length > 1000) reason = reason[..1000] + "…";
            if (status >= 500) _log.LogError("Local API {Request} answered {Status}: {Reason}", what, status, reason);
            else _log.LogWarning("Local API {Request} answered {Status}: {Reason}", what, status, reason);
        }
        call.Respond(status, bytes);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}
