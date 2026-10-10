using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Bring the dashboard-side log up FIRST so every action (and any crash) is recorded.
        AppLog.Init();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        AppLog.Info($"Dashboard started — v{version}, PID {Environment.ProcessId}, user {Environment.UserName}, " +
                    $"elevated={IsElevated()}, log dir={AppLog.Directory ?? "(none)"}.");

        // Funnel unhandled failures into the log instead of letting them vanish.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception (fatal).", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        // An update this dashboard handed to Setup earlier: report how it ended.
        if (FollowUpPreviousUpdate(version)) return;

        // Minimal hand-wired composition (a DI container is deferred; the graph is tiny).
        // HIVE_UPDATE_FEED overrides the public feed (staging/testing); blank => the default prod feed.
        var updates = new UpdateService(Environment.GetEnvironmentVariable("HIVE_UPDATE_FEED"));
        var vm = new MainViewModel(new LoopbackClient(), new ServiceControl(), updates);
        var window = new MainWindow(vm);
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Log it, keep the app alive (the individual command already surfaced a friendly message).
        AppLog.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"Dashboard closing (exit code {e.ApplicationExitCode}).");
        AppLog.Close();
        base.OnExit(e);
    }

    /// <summary>
    /// Logs the result of the last auto-update. When Setup is still at work the dashboard closes
    /// again (Setup waits for it). When the update did not complete, the reason from Setup's log
    /// goes into the Hive BDC log and the user can rerun the installer with its window visible.
    /// Returns true when the dashboard is shutting down.
    /// </summary>
    private bool FollowUpPreviousUpdate(string version)
    {
        var pending = UpdateHandoff.Load(onError: AppLog.Warn);
        switch (UpdateHandoff.Evaluate(pending, version, UpdateHandoff.IsSetupRunning))
        {
            case PreviousUpdateState.Completed:
                AppLog.Info($"Update from v{pending!.FromVersion} to v{pending.TargetVersion} completed.");
                UpdateHandoff.Clear();
                return false;

            case PreviousUpdateState.StillRunning:
                AppLog.Info($"Update to v{pending!.TargetVersion} is still being installed (Setup PID {pending.SetupPid}); the dashboard closes again.");
                MessageBox.Show($"Version {pending.TargetVersion} is being installed right now.\n\n" +
                                "The dashboard opens again by itself when the update is done.",
                    "Update in progress", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return true;

            case PreviousUpdateState.Failed:
                break;

            default:
                return false;
        }

        string tail = UpdateHandoff.LogTail(pending!.LogPath);
        AppLog.Warn($"Update from v{pending.FromVersion} to v{pending.TargetVersion} did not complete. " +
                    $"Setup log {pending.LogPath}:{Environment.NewLine}{tail}");
        UpdateHandoff.Clear();

        if (!File.Exists(pending.SetupPath) || !InstallerSignature.Verify(pending.SetupPath, out _))
        {
            MessageBox.Show($"The update to version {pending.TargetVersion} did not complete.\n\n" +
                            "Use \"Check for updates\" to try again. The details are in the log.",
                "Update not installed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var choice = MessageBox.Show(
            $"The update to version {pending.TargetVersion} did not complete. The details are in the log.\n\n" +
            "Run the installer again now? It shows its progress and any error this time.",
            "Update not installed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return false;

        try
        {
            string log = UpdateHandoff.SetupLogPath(AppLog.Directory ?? Path.GetDirectoryName(pending.SetupPath)!,
                pending.TargetVersion, DateTime.Now);
            using var setup = UpdateHandoff.Start(pending.SetupPath, log, silent: false);
            UpdateHandoff.Save(pending with { LogPath = log, SetupPid = setup.Id, StartedUtc = DateTime.UtcNow });
            AppLog.Info($"Installer for v{pending.TargetVersion} started again with its window (PID {setup.Id}), Setup log {log}.");
            Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Installer could not be started again.", ex);
            MessageBox.Show("The installer could not be started: " + ex.Message, "Update not installed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Whether the app is running elevated — Start/Stop of the service require it.</summary>
    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
