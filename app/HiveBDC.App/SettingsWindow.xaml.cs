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
        bool ok = await _vm.SaveAsync(SecretBox.Password, MailSecretBox.Password);
        if (ok)
        {
            SecretBox.Clear();
            MailSecretBox.Clear();
            DialogResult = true;   // closes the modal
        }
    }

    private async void OnTestSharePoint(object sender, RoutedEventArgs e)
        => await _vm.TestSharePointAsync(SecretBox.Password);

    private async void OnTestMail(object sender, RoutedEventArgs e)
        => await _vm.TestMailAsync(MailSecretBox.Password);

    private void OnCancel(object sender, RoutedEventArgs e)
        => Close();

}
