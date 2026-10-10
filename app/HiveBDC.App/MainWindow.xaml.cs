using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _logTimer;
    private bool _statusBusy;
    private bool _logBusy;
    private ScrollViewer? _logScroller;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Reopen at the size/position/maximized state the user left it in.
        WindowPlacement.Restore(this);
        Closing += (_, _) => WindowPlacement.Save(this);

        // /status + /metrics every 3s; /logs every 5s. Tick handlers run on the UI
        // thread, so awaiting them and updating bound properties is dispatcher-safe.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += OnStatusTick;
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _logTimer.Tick += OnLogTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Start();
        _logTimer.Start();
        await SafeStatusRefresh();
        await SafeLogRefresh();
    }

    private async void OnStatusTick(object? sender, EventArgs e) => await SafeStatusRefresh();

    private async void OnLogTick(object? sender, EventArgs e) => await SafeLogRefresh();

    // Skip overlapping ticks so a slow/hung call never stacks up refreshes.
    private async Task SafeStatusRefresh()
    {
        if (_statusBusy) return;
        _statusBusy = true;
        try
        {
            await _vm.RefreshStatusAndMetricsAsync();
            AppLog.Recovered("Dashboard status refresh");
        }
        catch (Exception ex) { AppLog.Failed("Dashboard status refresh", ex.Message); }   // next tick retries
        finally { _statusBusy = false; }
    }

    private async Task SafeLogRefresh()
    {
        if (_logBusy) return;
        _logBusy = true;
        try
        {
            // Refresh the log data regularly, but do NOT move the view — keep the reader
            // where they were (replacing the items would otherwise jump to the top).
            _logScroller ??= VisualTreeUtil.FindScrollViewer(LogList);
            double offset = _logScroller?.VerticalOffset ?? 0;
            await _vm.RefreshLogsAsync();
            if (_logScroller is not null)
                _ = Dispatcher.BeginInvoke(new Action(() => _logScroller.ScrollToVerticalOffset(offset)),
                    DispatcherPriority.Background);
            AppLog.Recovered("Dashboard log refresh");
        }
        catch (Exception ex) { AppLog.Failed("Dashboard log refresh", ex.Message); }   // next tick retries
        finally { _logBusy = false; }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _logTimer.Stop();
    }

    private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_vm.CreateSettingsViewModel()) { Owner = this };
        bool? saved = settings.ShowDialog();
        if (saved == true)
        {
            // Config hot-reloads in the service; pull the fresh values into the dashboard.
            await SafeStatusRefresh();
        }
    }

    private void RecentExport_DoubleClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenOutput(ShellHelper.RowItem(e.OriginalSource), this);

    private void LogsMenuItem_Click(object sender, RoutedEventArgs e)
        => new LogsWindow(_vm.CreateLogsViewModel()) { Owner = this }.Show();

    private void DocumentsMenuItem_Click(object sender, RoutedEventArgs e)
        => new DocumentsWindow(_vm.CreateDocumentsViewModel()) { Owner = this }.Show();

    private void ChangelogMenuItem_Click(object sender, RoutedEventArgs e)
        => new ChangelogWindow(_vm.CreateChangelogViewModel()) { Owner = this }.Show();

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        MessageBox.Show(this,
            $"Hive BDC, by Balsoft\n\nApp version {v}\n\n" +
            "Document data stays 100% on this machine — this dashboard reads only the service's local status API.\n\n" +
            "© Balsoft GmbH",
            "About Hive BDC", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
