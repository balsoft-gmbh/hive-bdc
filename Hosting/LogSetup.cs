using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Builds the Serilog logger (console + rolling daily file) and resolves a
/// writable log directory with graceful fallback. The file log is what makes
/// the service observable when it runs headless under the SCM.
/// </summary>
public static class LogSetup
{
    /// <summary>Log files (daily logs, Setup logs) older than this are deleted.</summary>
    public const int RetentionDays = 30;

    public const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Default log dir: %ProgramData%\HiveBDC\logs (writable by any service account).</summary>
    public static string DefaultLogDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HiveBDC", "logs");

    /// <summary>
    /// Resolve a usable log directory: try the configured (or default) dir, then
    /// &lt;app&gt;\logs, then give up (null = console-only). Never throws.
    /// </summary>
    public static string? ResolveLogDir(string? configured)
    {
        string primary = string.IsNullOrWhiteSpace(configured) ? DefaultLogDir : configured!;
        foreach (var dir in new[] { primary, Path.Combine(AppContext.BaseDirectory, "logs") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, $".writetest-{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch
            {
                // not writable — try the next candidate
            }
        }
        return null; // console-only
    }

    /// <summary>Build a Serilog logger writing to console and, if logDir is set, a rolling daily file.</summary>
    public static Logger Build(string? logDir, LogEventLevel level)
    {
        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: Template);

        if (logDir is not null)
        {
            cfg.WriteTo.File(
                path: Path.Combine(logDir, "hive-bdc-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(RetentionDays),
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: Template);
        }

        return cfg.CreateLogger();
    }

    /// <summary>
    /// Delete every *.log in <paramref name="logDir"/> last written more than
    /// <see cref="RetentionDays"/> days ago: the daily logs (Serilog only prunes them when it
    /// rolls, and only for its own writer) and the Setup logs updates leave next to them.
    /// Returns what it did as log lines; never throws.
    /// </summary>
    public static (int Deleted, List<string> Problems) DeleteOldLogs(string? logDir, DateTime? now = null)
    {
        var problems = new List<string>();
        int deleted = 0;
        if (string.IsNullOrWhiteSpace(logDir) || !Directory.Exists(logDir)) return (0, problems);
        var cutoff = (now ?? DateTime.Now).AddDays(-RetentionDays);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(logDir, "*.log").ToList(); }
        catch (Exception ex) { problems.Add($"could not list {logDir}: {ex.Message}"); return (0, problems); }
        foreach (var f in files)
        {
            try
            {
                if (File.GetLastWriteTime(f) >= cutoff) continue;
                File.Delete(f);
                deleted++;
            }
            catch (Exception ex) { problems.Add($"could not delete {Path.GetFileName(f)}: {ex.Message}"); }
        }
        return (deleted, problems);
    }

    public static LogEventLevel ParseLevel(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" or "info" => LogEventLevel.Information,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "critical" or "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
