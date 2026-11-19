using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>Overview: exports, the service and the licence as cards, and the recent exports (double-click opens the file).</summary>
public partial class OverviewPage : UserControl
{
    private Func<string, string?, Task>? _open;

    public OverviewPage()
    {
        InitializeComponent();
        ListColumns.HideBelow(RecentGrid, 800, RecentSourceColumn, RecentSharePointColumn);
        ListColumns.TabOnce(RecentGrid);
    }

    public void Attach(MainViewModel vm, Func<string, string?, Task> open)
    {
        DataContext = vm;
        _open = open;
    }

    private async void OnOpenExports(object sender, RoutedEventArgs e)
    {
        if (_open is not null) await _open(ShellViewModel.History, "Documents");
    }

    private async void OnOpenService(object sender, RoutedEventArgs e)
    {
        if (_open is not null) await _open(ShellViewModel.Service, "Status");
    }

    private async void OnOpenLicence(object sender, RoutedEventArgs e)
    {
        if (_open is not null) await _open(ShellViewModel.Service, "Licence");
    }

    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenOutput(ShellHelper.RowItem(e.OriginalSource), Window.GetWindow(this)!);

    private void OnRecentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RecentGrid.SelectedItem is not RecentExportItem item) return;
        ShellHelper.OpenOutput(item, Window.GetWindow(this)!);
        e.Handled = true;
    }
}