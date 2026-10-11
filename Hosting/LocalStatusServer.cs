using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Loopback control/status API for the companion desktop app. Binds an HttpListener
/// to 127.0.0.1 on an ephemeral port and publishes {port, token} to
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
    private readonly ExportLedger _ledger;
    private readonly ILogger<LocalStatusServer> _log;
    private readonly string? _logDir;
    private readonly string _version;
    private readonly string _settingsPath;

    private readonly HttpListener _listener = new();
    private readonly string _token = Base64Url(RandomNumberGenerator.GetBytes(32));

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public LocalStatusServer(
        IOptionsMonitor<BDCOptions> opts, ExportLedger ledger, ILogger<LocalStatusServer> log,
        string? logDir, string version, string settingsPath)
    {
        _optsMonitor = opts;
        _ledger = ledger;
        _log = log;
        _logDir = logDir;
        _version = version;
        _settingsPath = settingsPath;
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
        int port;
        try
        {
            port = FreeLoopbackPort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            WriteEndpointFile(port);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local status API could not start; the desktop app will not see this service.");
            return;
        }

        _log.LogInformation("Local status API listening on http://127.0.0.1:{Port} (token in %ProgramData%\\HiveBDC\\{File}).", port, EndpointFileName);
        using var reg = stoppingToken.Register(() => { try { _listener.Stop(); } catch { /* shutting down */ } });

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Local API: accepting a request failed; listening continues."); continue; }

            _ = Handle(ctx);
        }

        try { _listener.Close(); } catch { /* ignore */ }
        _log.LogInformation("Local status API stopped.");
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;

            if (req.RemoteEndPoint is null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address))
            { Respond(ctx, 403, new { error = "loopback only" }); return; }

            string? auth = req.Headers["Authorization"];
            if (auth is null || !auth.StartsWith("Bearer ", StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(auth["Bearer ".Length..]), Encoding.UTF8.GetBytes(_token)))
            { Respond(ctx, 401, new { error = "unauthorized" }); return; }

            string method = req.HttpMethod.ToUpperInvariant();
            string path = req.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";

            switch ((method, path))
            {
                case ("GET", "/status"): Respond(ctx, 200, Status()); return;
                case ("GET", "/metrics"): Respond(ctx, 200, Metrics(RecentParam(req))); return;
                case ("GET", "/logs"): Respond(ctx, 200, Logs(TailParam(req))); return;
                case ("GET", "/config"): GetConfig(ctx); return;
                case ("POST", "/config"): PostConfig(ctx, req); return;
                case ("POST", "/config/test-sharepoint"): await PostTestSharePoint(ctx, req); return;
                case ("POST", "/config/test-mail"): await PostTestMail(ctx, req); return;
                case ("POST", "/license"): PostLicense(ctx, req); return;
                default: Respond(ctx, 404, new { error = "not found" }); return;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local API {Request} failed.", $"{ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}");
            try { Respond(ctx, 500, new { error = "internal error" }); } catch { /* ignore */ }
        }
    }

    // ── GET /status /metrics /logs (observational) ──────────────────────────

    private object Status()
    {
        var opts = Snapshot();
        var lic = LicenseValidator.Validate(
            opts.License.File, opts.SharePointAuth.TenantId, DateOnly.FromDateTime(DateTime.Today));
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

    /// <summary>Parse the current appsettings.json (tolerant of // comments), or null if absent/unreadable.</summary>
    private JsonObject? ReadRoot()
    {
        var root = SettingsFile.ReadRoot(_settingsPath);
        if (root is null && File.Exists(_settingsPath))
            _log.LogError("{Path} exists but could not be read as JSON; settings changes start from an empty file.", _settingsPath);
        return root;
    }

    private static string ReadBody(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static int TailParam(HttpListenerRequest req)
        => int.TryParse(req.QueryString["tail"], out var n) ? n : 200;

    private static int RecentParam(HttpListenerRequest req)
        => int.TryParse(req.QueryString["recent"], out var n) ? Math.Clamp(n, 1, 5000) : 20;

    private void WriteEndpointFile(int port)
    {
        string dir = Path.GetDirectoryName(ExportLedger.DefaultDbPath)!;
        Directory.CreateDirectory(dir);
        // Publish the resolved log directory too, so the dashboard app can co-log into
        // the SAME rolling file the service writes (one unified, app-visible log).
        string json = JsonSerializer.Serialize(new { port, token = _token, logDir = _logDir });
        File.WriteAllText(Path.Combine(dir, EndpointFileName), json);
    }

    private void Respond(HttpListenerContext ctx, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        // Every refused or failed dashboard request lands in the log with its reason, so a
        // "could not save" in the dashboard can be explained from the log alone. Bodies of
        // error answers carry messages and validation errors only, never settings or secrets.
        if (status >= 400)
        {
            string what = $"{ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}";
            string reason = Encoding.UTF8.GetString(bytes);
            if (reason.Length > 1000) reason = reason[..1000] + "…";
            if (status >= 500) _log.LogError("Local API {Request} answered {Status}: {Reason}", what, status, reason);
            else _log.LogWarning("Local API {Request} answered {Status}: {Reason}", what, status, reason);
        }
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>Grab a free 127.0.0.1 port from the OS (bind :0), then hand it to HttpListener.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}
