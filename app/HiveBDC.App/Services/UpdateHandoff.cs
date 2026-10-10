using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>An update the dashboard handed to Setup and then closed for.</summary>
public sealed record PendingUpdate(
    string TargetVersion, string FromVersion, string SetupPath, string LogPath, int SetupPid, DateTime StartedUtc);

/// <summary>What the dashboard finds on start about the last update it launched.</summary>
public enum PreviousUpdateState { None, Completed, StillRunning, Failed }

/// <summary>
/// Hands a verified installer to Windows and keeps a record of the attempt, without any WPF.
/// Setup is started DIRECTLY (ShellExecute), the way every update up to 2.1.1 was started.
/// 2.1.2 to 2.2.1 started it through a hidden PowerShell "wait for the dashboard, then run
/// Setup" step; on a customer server (2026-09-29) that step never ran Setup and nothing was logged, so the
/// dashboard just closed. It is not needed: since 2.1.1 Setup itself waits for the dashboard
/// to exit before it copies anything (Balsoft.Hive.Installer.Windows, CloseBeforeInstall, since 2.3.0).
/// Setup's own log goes next to the Hive BDC log, so a failed update travels with the log files.
/// </summary>
public static class UpdateHandoff
{
    /// <summary>Seconds a started Setup must survive; one that exits sooner did not install anything.</summary>
    public const int StartupGraceMilliseconds = 3000;

    private const string PendingFileName = "update-pending.json";

    /// <summary>%ProgramData%\HiveBDC, where the dashboard keeps the pending-update record.</summary>
    public static string DefaultStateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HiveBDC");

    /// <summary>Setup log path: in <paramref name="logDir"/>, named by version and time, never matching the service's hive-bdc-*.log glob.</summary>
    public static string SetupLogPath(string logDir, string targetVersion, DateTime now) =>
        Path.Combine(logDir, $"setup-v{targetVersion.TrimStart('v', 'V')}-{now:yyyyMMdd-HHmmss}.log");

    /// <summary>
    /// Setup command line. Silent: the dashboard's "Check for updates" (no window, no prompts).
    /// Not silent: the retry the user starts after a failed update, which shows progress and any error.
    /// </summary>
    public static string InstallerArguments(string logPath, bool silent) =>
        (silent ? "/VERYSILENT /SUPPRESSMSGBOXES" : "/SILENT") + $" /NORESTART /LOG=\"{logPath}\"";

    /// <summary>
    /// Start Setup and return it. Throws when Windows refuses to start it or when it exits within
    /// the grace period (blocked by policy or antivirus, damaged file); the caller then keeps the
    /// dashboard open and shows the reason instead of closing into nothing.
    /// </summary>
    public static Process Start(string setupPath, string logPath, bool silent)
    {
        var psi = new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = InstallerArguments(logPath, silent),
            WorkingDirectory = Path.GetDirectoryName(setupPath) ?? "",
            UseShellExecute = true,
        };
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Windows did not start the installer.");
        if (process.WaitForExit(StartupGraceMilliseconds))
            throw new InvalidOperationException(
                $"The installer stopped right after starting (exit code {process.ExitCode}). " +
                $"Setup log: {(File.Exists(logPath) ? logPath : "none written")}.");
        return process;
    }

    public static void Save(PendingUpdate pending, string? stateDir = null)
    {
        string dir = stateDir ?? DefaultStateDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PendingFileName), JsonSerializer.Serialize(pending));
    }

    public static PendingUpdate? Load(string? stateDir = null, Action<string>? onError = null)
    {
        try
        {
            string file = Path.Combine(stateDir ?? DefaultStateDir, PendingFileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            onError?.Invoke("The record of the last update could not be read: " + ex.Message);
            return null;
        }
    }

    public static void Clear(string? stateDir = null)
    {
        try { File.Delete(Path.Combine(stateDir ?? DefaultStateDir, PendingFileName)); } catch { /* best effort */ }
    }

    /// <summary>Outcome of <paramref name="pending"/> for a dashboard now running <paramref name="currentVersion"/>.</summary>
    public static PreviousUpdateState Evaluate(PendingUpdate? pending, string currentVersion, Func<int, bool> setupRunning)
    {
        if (pending is null) return PreviousUpdateState.None;
        if (!IsNewer(currentVersion, pending.TargetVersion)) return PreviousUpdateState.Completed;
        return setupRunning(pending.SetupPid) ? PreviousUpdateState.StillRunning : PreviousUpdateState.Failed;
    }

    /// <summary>True when <paramref name="latest"/> is a strictly higher version than <paramref name="current"/>.
    /// Tolerant of a leading 'v' and of 2 to 4 version components.</summary>
    public static bool IsNewer(string current, string latest) => ParseVersion(latest) > ParseVersion(current);

    private static Version ParseVersion(string v)
    {
        v = (v ?? "").Trim().TrimStart('v', 'V');
        if (!Version.TryParse(v, out var ver)) return new Version(0, 0, 0, 0);
        return new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build), Math.Max(0, ver.Revision));
    }

    /// <summary>True while the Setup process recorded in the pending update is alive.</summary>
    public static bool IsSetupRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("HiveBDC-Setup", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The last lines of the Setup log (where Setup writes the reason it stopped), or why there are none.</summary>
    public static string LogTail(string logPath, int lines = 12)
    {
        try
        {
            if (!File.Exists(logPath)) return "No Setup log was written, so Setup did not start.";
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex) { return "The Setup log could not be read: " + ex.Message; }
    }
}
