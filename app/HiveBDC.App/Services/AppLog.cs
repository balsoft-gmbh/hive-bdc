using System.IO;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>
/// Dashboard-side audit/diagnostic log. The service process and the app process are
/// separate, so the service log never sees what the user does in the dashboard
/// (Start/Stop, Save settings, Test SharePoint, Renew license, updates, open/close,
/// or the not-elevated "access denied"). This writes those events to the SAME rolling
/// daily file the service uses — discovered from %ProgramData%\HiveBDC\app-endpoint.json
/// — so everything lands in one log the dashboard already displays. App lines are tagged
/// "UI · " to tell them apart from service lines. If the service log dir is unknown or not
/// writable (e.g. the app runs as a different user), it falls back to a user-local file with
/// a DISTINCT name so the service's /logs reader never picks it up. Best-effort: logging
/// never throws into the UI.
/// </summary>
public static class AppLog
{
    private static Logger? _log;
    private static string? _dir;

    /// <summary>Directory the app log is being written to (for diagnostics); null if logging is off.</summary>
    public static string? Directory => _dir;

    public static void Init()
    {
        if (_log is not null) return;

        // Primary: co-locate with the service log (shared writer, same daily rolling name).
        string? serviceDir = ResolveServiceLogDir();
        if (serviceDir is not null && TryBuild(Path.Combine(serviceDir, "hive-bdc-.log"), serviceDir))
            return;

        // Fallback: a user-writable location with a DISTINCT prefix so the service's
        // /logs reader (which globs hive-bdc-*.log) never mistakes it for a service log.
        try
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Balsoft", "HiveBDC", "logs");
            TryBuild(Path.Combine(local, "hive-dashboard-.log"), local);
        }
        catch { _log = null; }
    }

    private static bool TryBuild(string path, string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            _log = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Debug)
                .WriteTo.File(
                    path: path,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: null,
                    retainedFileTimeLimit: TimeSpan.FromDays(RetentionDays),
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            _dir = dir;
            return true;
        }
        catch
        {
            _log = null;
            return false;
        }
    }

    /// <summary>The service's resolved log dir from app-endpoint.json, if present and writable; else its default, if writable; else null.</summary>
    private static string? ResolveServiceLogDir()
    {
        try
        {
            string ep = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HiveBDC", "app-endpoint.json");
            if (File.Exists(ep))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ep));
                if (doc.RootElement.TryGetProperty("logDir", out var ld) && ld.ValueKind == JsonValueKind.String)
                {
                    string? d = ld.GetString();
                    if (!string.IsNullOrWhiteSpace(d) && IsWritable(d!)) return d;
                }
            }
        }
        catch { /* fall through */ }

        // The service default (%ProgramData%\HiveBDC\logs) when the endpoint file
        // has no logDir (older service) but the service still logs there by default.
        string def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HiveBDC", "logs");
        return IsWritable(def) ? def : null;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".uiwrite-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static void Info(string message) => Safe(() => _log?.Information("UI · " + message));
    public static void Warn(string message) => Safe(() => _log?.Warning("UI · " + message));
    public static void Error(string message, Exception? ex = null) => Safe(() => _log?.Error(ex, "UI · " + message));

    /// <summary>Log files older than this are deleted (same window as the service).</summary>
    public const int RetentionDays = 30;

    private static readonly HashSet<string> Failing = new();

    /// <summary>
    /// A background step that runs every few seconds (status poll, log refresh) failed. Logged
    /// once when it starts failing, not on every tick; <see cref="Recovered"/> logs the end.
    /// </summary>
    public static void Failed(string what, string reason)
    {
        lock (Failing) if (!Failing.Add(what)) return;
        Warn($"{what} failed: {reason} (logged once until it works again)");
    }

    /// <summary>The step named in an earlier <see cref="Failed"/> works again.</summary>
    public static void Recovered(string what)
    {
        lock (Failing) if (!Failing.Remove(what)) return;
        Info($"{what} works again.");
    }

    public static void Close() => Safe(() => { _log?.Dispose(); _log = null; });

    private static void Safe(Action a) { try { a(); } catch { /* logging must never break the UI */ } }
}
