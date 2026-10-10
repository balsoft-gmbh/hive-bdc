using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class LogsWindow : Window
{
    private readonly LogsViewModel _vm;
    private readonly DispatcherTimer _timer;
    private bool _busy;
    private ScrollViewer? _scroller;

    public LogsWindow(LogsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await Tick();
        Loaded += async (_, _) => { _timer.Start(); await Tick(); };
        Closed += (_, _) => _timer.Stop();
    }

    // Refresh regularly, but keep the reader's scroll position (no auto-scroll).
    private async Task Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _scroller ??= VisualTreeUtil.FindScrollViewer(LogList);
            double offset = _scroller?.VerticalOffset ?? 0;
            await _vm.RefreshAsync();
            if (_scroller is not null)
                _ = Dispatcher.BeginInvoke(new Action(() => _scroller.ScrollToVerticalOffset(offset)),
                    DispatcherPriority.Background);
            AppLog.Recovered("Log window refresh");
        }
        catch (Exception ex) { AppLog.Failed("Log window refresh", ex.Message); }   // next tick retries
        finally { _busy = false; }
    }
}
