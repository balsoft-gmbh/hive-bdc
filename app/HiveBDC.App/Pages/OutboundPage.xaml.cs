using System.Windows;
using System.Windows.Controls;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>
/// Outbound: the document types, one type with its companies, one company with where its documents come from and go,
/// its SharePoint library, the destination tiles and one destination. The settings form owns all state; Remove asks
/// inside the record first and takes effect with Save, like the rest of the form.
/// </summary>
public partial class OutboundPage : UserControl
{
    public OutboundPage() => InitializeComponent();

    /// <summary>The SharePoint secret typed on Connections but not saved yet, for Test this library.</summary>
    public Func<string>? SharePointSecret { get; set; }

    /// <summary>Opens the check of a Word file over the page.</summary>
    public Action<string>? ShowWordCheck { get; set; }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnOpenType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTypeEditViewModel type) Vm?.OpenType(type);
    }

    /// <summary>
    /// A two-way choice shows the record's value one way and writes only the choice made on screen. Bound two ways, the
    /// radio group unchecks the other button while the page switches records and writes "false" into the record
    /// shown before: a Word company turned into an ERP export company just by opening the next company.
    /// </summary>
    private void OnSegmentChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string property, DataContext: { } record })
            record.GetType().GetProperty(property)?.SetValue(record, true);
    }

    private void OnAskRemove(object sender, RoutedEventArgs e) => RecordActions.SetConfirm(sender, true);

    private void OnKeepItem(object sender, RoutedEventArgs e) => RecordActions.SetConfirm(sender, false);

    private void OnRemoveType(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not DocumentTypeEditViewModel type) return;
        vm.RemoveType(type);
        vm.ShowPageCommand.Execute(ShellViewModel.Outbound);
    }

    private void OnAddCompany(object sender, RoutedEventArgs e) => Vm?.AddCompanyToOpenType();

    private void OnOpenCompany(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteRow company) Vm?.OpenCompany(company);
    }

    private void OnRemoveCompany(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not RouteRow company) return;
        vm.RemoveCompany(company);
        vm.BackCommand.Execute(null);
    }

    private void OnConnectMail(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ShowPageCommand.Execute(ShellViewModel.Connections);
        vm.OpenCommand.Execute("mail");
    }

    private void OnEditTargetCard(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TargetRow target) Vm?.EditDestination(target);
    }

    private void OnPickDestination(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TargetInfoDto kind) Vm?.PickDestination(kind);
    }

    /// <summary>Remove on a destination's own screen: the page closes without writing it back, then the company drops it.</summary>
    private void OnRemoveDestination(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not TargetRow target) return;
        vm.OpenDestination = null;
        vm.SelectedDocumentType?.SelectedCompany?.RemoveTarget(target);
        vm.BackCommand.Execute(null);
        vm.RefreshSummaries();
    }

    private async void OnTestSharePoint(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.TestSharePointAsync(SharePointSecret?.Invoke() ?? "");
    }

    private void OnCheckWordFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Check a Word invoice",
            Filter = "Word document (*.docx)|*.docx",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        ShowWordCheck?.Invoke(dlg.FileName);
    }
}