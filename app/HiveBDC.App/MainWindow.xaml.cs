using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

/// <summary>
/// The one window: sidebar, top bar and the pages. The dashboard view model polls the service; the settings form is
/// shared by Outbound, Inbound, Connections and Service and is loaded the first time one of them opens. Leaving those
/// pages or closing the window with changes that are not saved asks first (<see cref="ShellViewModel"/>); dropping
/// them starts a fresh form, as closing the former Settings window did.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _savedWatch;
    private SettingsViewModel? _settings;
    private bool _settingsLoaded;
    private bool _statusBusy;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _shell = new ShellViewModel(UnsavedSettings, Environment.UserName, Environment.MachineName, App.IsElevated()) { Dashboard = vm };
        _shell.DiscardSettings += AttachSettings;
        DataContext = _shell;

        OverviewView.Attach(vm, OpenPage);
        HistoryView.Attach(_shell, vm.CreateHistoryViewModel(), vm.CreateDocumentsViewModel());
        ServiceView.Attach(_shell, vm, vm.CreateLogsViewModel(), vm.CreateChangelogViewModel());
        OutboundView.SharePointSecret = () => ConnectionsView.SharePointSecret;
        OutboundView.ShowWordCheck = path => OpenDialog(new Pages.WordCheckDialog(path));
        AttachSettings();

        WindowPlacement.Restore(this);
        FitToScreen();
        Closing += OnWindowClosing;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += async (_, _) => await SafeStatusRefresh();

        // "Saved" stays only while it is true: the first change after a save clears it.
        _savedWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _savedWatch.Tick += (_, _) =>
        {
            if (_settings is { JustSaved: true } s && (ConnectionsView.PasswordTyped || s.FormDiffersFromSaved())) s.StatusMessage = "";
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private bool UnsavedSettings()
        => _settingsLoaded && _settings is { IsBusy: false } s && s.HasUnsavedChanges(ConnectionsView.PasswordTyped);

    /// <summary>A fresh settings form for the SET UP pages; it loads when one of them opens.</summary>
    private void AttachSettings()
    {
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
        _settings = _vm.CreateSettingsViewModel();
        _settingsLoaded = false;
        _settings.PropertyChanged += OnSettingsChanged;
        ConnectionsView.Attach(_settings);
        OutboundView.DataContext = _settings;
        InboundView.DataContext = _settings;
        ServiceView.AttachSettings(_settings);
        _shell.Settings = _settings;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Page) && _settings is not null) _shell.Follow(_settings.Page);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Start();
        _savedWatch.Start();
        await SafeStatusRefresh();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _savedWatch.Stop();
    }

    /// <summary>The window closes at once unless settings would be lost; then the save bar asks. Setup's shutdown is never held up.</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        WindowPlacement.Save(this);
        if (!_shell.RequestClose()) e.Cancel = true;
    }

    // Skip overlapping ticks so a slow or hung call never stacks up refreshes.
    private async Task SafeStatusRefresh()
    {
        if (_statusBusy) return;
        _statusBusy = true;
        try
        {
            await _vm.RefreshStatusAndMetricsAsync();
            AppLog.Recovered("Dashboard status refresh");
        }
        catch (Exception ex) { AppLog.Failed("Dashboard status refresh", ex.Message); }
        finally { _statusBusy = false; }
    }

    private async void OnNav(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string page) await OpenPage(page, null);
    }

    private async void OnBrand(object sender, RoutedEventArgs e) => await OpenPage(ShellViewModel.Overview, null);

    /// <summary>Opens a page, optionally on one of its tabs; a SET UP page shows the settings form, loaded on first use.</summary>
    private async Task OpenPage(string page, string? tab)
    {
        if (tab is not null)
        {
            if (page == ShellViewModel.History) _shell.HistoryTab = tab;
            else if (page == ShellViewModel.Service) _shell.ServiceTab = tab;
        }
        if (_shell.Navigate(page)) await ShowSettingsPage(page);
    }

    private async Task ShowSettingsPage(string page)
    {
        if (!ShellViewModel.IsSettingsPage(page) || _settings is not { } settings) return;
        settings.ShowPageCommand.Execute(page);
        if (_settingsLoaded) return;
        _settingsLoaded = true;
        await settings.LoadAsync();
        if (settings.StatusIsError && ReferenceEquals(settings, _settings)) _settingsLoaded = false;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var (sharePoint, mail, docuWare) = ConnectionsView.TypedSecrets();
        bool ok = await _settings.SaveAsync(sharePoint, mail, docuWare);
        if (!ok) return;
        ConnectionsView.ClearSecrets();
        // Config hot-reloads in the service; pull the fresh values into the dashboard.
        await SafeStatusRefresh();
    }

    private void OnKeepEditing(object sender, RoutedEventArgs e) => _shell.KeepEditing();

    private void OnLeaveWithoutSaving(object sender, RoutedEventArgs e)
    {
        if (_shell.LeaveWithoutSaving()) Close();
    }

    /// <summary>A record dialog over the page, as the ERP opens one; Escape or its Close button ends it.</summary>
    private void OpenDialog(Pages.WordCheckDialog dialog)
    {
        dialog.CloseRequested += CloseDialog;
        DialogHost.Content = dialog;
        DialogLayer.Visibility = Visibility.Visible;
        Layout.IsEnabled = false;
        dialog.Loaded += (_, _) => dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void CloseDialog()
    {
        DialogHost.Content = null;
        DialogLayer.Visibility = Visibility.Collapsed;
        Layout.IsEnabled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DialogLayer.Visibility == Visibility.Visible)
        {
            CloseDialog();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Never open taller or wider than the display: on a laptop or a remote session at 1366x768 the window would
    /// otherwise open with the save bar off the bottom of the screen.
    /// </summary>
    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 20) Height = Math.Max(MinHeight, work.Height - 20);
        if (Width > work.Width - 20) Width = Math.Max(MinWidth, work.Width - 20);
    }
}