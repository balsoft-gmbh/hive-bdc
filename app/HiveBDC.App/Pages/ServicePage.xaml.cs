using System.Windows.Controls;
using System.Windows.Threading;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>
/// Service: Status (start, stop and the service's own settings), Licence, Updates (check for updates and the release
/// notes) and the technical log, which refreshes every 3 seconds while it is open and keeps the reader's place.
/// </summary>
public partial class ServicePage : UserControl
{
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private LogsViewModel? _logs;
    private ChangelogViewModel? _changelog;
    private bool _logBusy;
    private ScrollViewer? _logScroller;

    public ServicePage()
    {
        InitializeComponent();
        _logTimer.Tick += async (_, _) => await RefreshLog();
    }

    public void Attach(ShellViewModel shell, MainViewModel dashboard, LogsViewModel logs, ChangelogViewModel changelog)
    {
        _logs = logs;
        _changelog = changelog;
        TabsBar.DataContext = shell;
        StatusPanel.DataContext = dashboard;
        AboutPanel.DataContext = dashboard;
        LicenceTab.DataContext = dashboard;
        UpdatesPanel.DataContext = dashboard;
        ReleaseNotesPanel.DataContext = changelog;
        LogTab.DataContext = logs;

        LogTab.IsVisibleChanged += async (_, _) =>
        {
            if (!LogTab.IsVisible) { _logTimer.Stop(); return; }
            _logTimer.Start();
            await RefreshLog();
        };
        UpdatesTab.IsVisibleChanged += async (_, _) =>
        {
            if (!UpdatesTab.IsVisible || _changelog is null) return;
            try { await _changelog.LoadAsync(); }
            catch (Exception ex) { AppLog.Warn("Release notes: loading failed (the built-in copy stays shown): " + ex.Message); }
        };
    }

    public void AttachSettings(SettingsViewModel settings) => SettingsPanel.DataContext = settings;

    // Refresh regularly, but keep the reader's scroll position (no auto-scroll).
    private async Task RefreshLog()
    {
        if (_logBusy || _logs is null) return;
        _logBusy = true;
        try
        {
            _logScroller ??= VisualTreeUtil.FindScrollViewer(LogList);
            double offset = _logScroller?.VerticalOffset ?? 0;
            await _logs.RefreshAsync();
            if (_logScroller is not null)
                _ = Dispatcher.BeginInvoke(new Action(() => _logScroller.ScrollToVerticalOffset(offset)), DispatcherPriority.Background);
            AppLog.Recovered("Log view refresh");
        }
        catch (Exception ex) { AppLog.Failed("Log view refresh", ex.Message); }
        finally { _logBusy = false; }
    }
}