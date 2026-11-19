using System.Windows;
using System.Windows.Controls;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>
/// Inbound: the document types brought into the ERP, one type with its companies, the document system tiles and one
/// company with where its documents come from and the CSV the ERP reads. Remove asks inside the record first.
/// </summary>
public partial class InboundPage : UserControl
{
    public InboundPage() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnOpenInboundType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InboundTypeRow type) Vm?.OpenInboundType(type);
    }

    private void OnAskRemove(object sender, RoutedEventArgs e) => RecordActions.SetConfirm(sender, true);

    private void OnKeepItem(object sender, RoutedEventArgs e) => RecordActions.SetConfirm(sender, false);

    private void OnRemoveInboundType(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not InboundTypeRow type) return;
        vm.RemoveInboundType(type);
        vm.ShowPageCommand.Execute(ShellViewModel.Inbound);
    }

    private void OnOpenInboundCompany(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ImportFlowRow row) Vm?.OpenInboundCompany(row);
    }

    /// <summary>Remove on the company's own screen: the page closes without writing it back, then the type drops it.</summary>
    private void OnRemoveInboundCompany(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not ImportFlowRow row) return;
        vm.OpenInbound = null;
        vm.RemoveInboundCompany(row);
        vm.BackCommand.Execute(null);
    }

    private void OnPickSource(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SourceInfoDto source) Vm?.PickSource(source);
    }

    private async void OnChangeSource(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.ChangeInboundSource();
    }
}