using System.Windows;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

/// <summary>
/// Modal settings editor. The view-model owns all state and the loopback calls; this
/// code-behind only bridges the PasswordBox (whose content is not bindable) into the
/// Save / Test commands and closes the window on a successful save. The raw secret is
/// read from the PasswordBox at the moment of the call and never stored anywhere else.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.UnsavedSecrets = () => (DocuWareSecretBox.Password, SecretBox.Password);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToScreen();
        await _vm.LoadAsync();
    }

    /// <summary>
    /// Never open taller or wider than the display. The designed size assumes a desktop
    /// monitor; on a laptop or a remote session at 1366x768 the window would otherwise open
    /// with its footer, and therefore Save, off the bottom of the screen.
    /// </summary>
    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 20) Height = Math.Max(MinHeight, work.Height - 20);
        if (Width > work.Width - 20) Width = Math.Max(MinWidth, work.Width - 20);

        // Re-centre after resizing: WindowStartupLocation placed it for the original size.
        if (Owner is not null)
        {
            Left = Owner.Left + (Owner.Width - Width) / 2;
            Top = Owner.Top + (Owner.Height - Height) / 2;
        }
        if (Top < work.Top) Top = work.Top;
        if (Left < work.Left) Left = work.Left;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        bool ok = await _vm.SaveAsync(SecretBox.Password, MailSecretBox.Password, DocuWareSecretBox.Password);
        if (ok)
        {
            SecretBox.Clear();
            MailSecretBox.Clear();
            DocuWareSecretBox.Clear();
            DialogResult = true;   // closes the modal
        }
    }

    private async void OnTestSharePoint(object sender, RoutedEventArgs e)
        => await _vm.TestSharePointAsync(SecretBox.Password);

    private async void OnTestMail(object sender, RoutedEventArgs e)
        => await _vm.TestMailAsync(MailSecretBox.Password);

    private void OnCheckWordFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Check a Word invoice",
            Filter = "Word document (*.docx)|*.docx",
        };
        if (dlg.ShowDialog(this) != true) return;
        new WordCheckWindow(dlg.FileName) { Owner = this }.Show();
    }

    private async void OnTestDocuWare(object sender, RoutedEventArgs e)
        => await _vm.TestDocuWareAsync(DocuWareSecretBox.Password);

    private void OnAskRemove(object sender, RoutedEventArgs e) => SetConfirm(sender, true);

    private void OnKeepItem(object sender, RoutedEventArgs e) => SetConfirm(sender, false);

    private static void SetConfirm(object sender, bool ask)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case DocumentTypeEditViewModel t: t.ConfirmRemove = ask; break;
            case RouteRow r: r.ConfirmRemove = ask; break;
            case TargetRow d: d.ConfirmRemove = ask; break;
            case ImportFlowRow i: i.ConfirmRemove = ask; break;
            case InboundTypeRow g: g.ConfirmRemove = ask; break;
        }
    }

    private void OnOpenType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTypeEditViewModel type) _vm.OpenType(type);
    }

    private void OnRemoveType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTypeEditViewModel type) _vm.RemoveType(type);
    }

    private void OnAddCompany(object sender, RoutedEventArgs e) => _vm.AddCompanyToOpenType();

    private void OnOpenCompany(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteRow company) _vm.OpenCompany(company);
    }

    private void OnRemoveCompany(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteRow company) _vm.RemoveCompany(company);
    }

    private void OnConnectMail(object sender, RoutedEventArgs e)
    {
        _vm.ShowPageCommand.Execute("Connections");
        _vm.OpenCommand.Execute("mail");
    }

    private async void OnAddDocuWareTarget(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDocumentType?.SelectedCompany is not { } row) return;
        await EditDocuWareTarget(row, null);
        _vm.RefreshSummaries();
    }

    private async void OnEditTargetCard(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.TargetRow target, Tag: ViewModels.RouteRow row }) return;
        if (!target.IsDocuWare)
        {
            MessageBox.Show(this, "This destination was set up with a newer version of Hive BDC and is kept as it is.", "Hive BDC",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await EditDocuWareTarget(row, target);
        _vm.RefreshSummaries();
    }

    private void OnRemoveTargetCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.TargetRow target, Tag: ViewModels.RouteRow row })
            row.RemoveTarget(target);
        _vm.RefreshSummaries();
    }

    private void OnOpenInboundType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InboundTypeRow type) _vm.OpenInboundType(type);
    }

    private void OnRemoveInboundType(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InboundTypeRow type) _vm.RemoveInboundType(type);
    }

    private void OnOpenInboundCompany(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ImportFlowRow row) _vm.OpenInboundCompany(row);
    }

    private void OnRemoveInboundCompany(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ImportFlowRow row) _vm.RemoveInboundCompany(row);
    }

    private void OnPickSource(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SourceInfoDto source) _vm.PickSource(source);
    }

    private async void OnChangeSource(object sender, RoutedEventArgs e) => await _vm.ChangeInboundSource();

    private async Task EditDocuWareTarget(ViewModels.RouteRow row, ViewModels.TargetRow? existing)
    {
        if (string.IsNullOrWhiteSpace(_vm.DocuWareUrl))
        {
            MessageBox.Show(this, "Connect DocuWare under Connections first (address, user name, password), then add it as a destination.",
                "Hive BDC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vm = _vm.CreateDocuWareTarget(DocuWareSecretBox.Password, existing);
        var dialog = new DocuWareTargetWindow(vm) { Owner = this };
        await Task.Yield();
        if (dialog.ShowDialog() == true) row.PutTarget(vm.ToTarget(), existing);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
        => Close();

}
