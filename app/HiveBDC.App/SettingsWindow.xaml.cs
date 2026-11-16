using System.Windows;
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

    private async void OnAddDocuWareTarget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.RouteRow row) return;
        await EditDocuWareTarget(row, null);
    }

    private async void OnEditTarget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.RouteRow row || row.SelectedTarget is not { } target) return;
        if (!target.IsDocuWare)
        {
            MessageBox.Show(this, "This target was set up with a newer version of Hive BDC and is kept as it is.", "Hive BDC",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await EditDocuWareTarget(row, target);
    }

    private async void OnAddImport(object sender, RoutedEventArgs e) => await EditImport(null);

    private async void OnEditImport(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFlow is not { } flow) return;
        if (!flow.IsDocuWare)
        {
            MessageBox.Show(this, "This import was set up with a newer version of Hive BDC and is kept as it is.", "Hive BDC",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await EditImport(flow);
    }

    private async Task EditImport(ViewModels.ImportFlowRow? existing)
    {
        if (string.IsNullOrWhiteSpace(_vm.DocuWareUrl))
        {
            MessageBox.Show(this, "Set up the DocuWare sign-in on the Connections tab first (address, user name, password), then add the import.",
                "Hive BDC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vm = _vm.CreateImportFlow(DocuWareSecretBox.Password, existing);
        var dialog = new ImportFlowWindow(vm) { Owner = this };
        await Task.Yield();
        if (dialog.ShowDialog() == true) _vm.PutFlow(vm, existing);
    }

    private async Task EditDocuWareTarget(ViewModels.RouteRow row, ViewModels.TargetRow? existing)
    {
        if (string.IsNullOrWhiteSpace(_vm.DocuWareUrl))
        {
            MessageBox.Show(this, "Set up the DocuWare sign-in on the Connections tab first (address, user name, password), then add the target.",
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
