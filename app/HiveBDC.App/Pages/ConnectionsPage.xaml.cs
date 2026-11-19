using System.Windows;
using System.Windows.Controls;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>
/// Connections: the SharePoint app, DocuWare, document mail and every other kind the service reports. This page holds
/// the password boxes (their content is not bindable); the settings form reads a typed secret only at Save or Test and
/// never stores it. A fresh form (after the user dropped changes) clears them.
/// </summary>
public partial class ConnectionsPage : UserControl
{
    public ConnectionsPage() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    public void Attach(SettingsViewModel vm)
    {
        DataContext = vm;
        ClearSecrets();
        vm.UnsavedSecrets = () => (DocuWareSecretBox.Password, SecretBox.Password);
        vm.UnsavedConnectionSecret = () => ConnectionSecretBox.Password;
        vm.ClearConnectionSecret = () => ConnectionSecretBox.Clear();
    }

    public bool PasswordTyped
        => new[] { SecretBox, MailSecretBox, DocuWareSecretBox, ConnectionSecretBox }.Any(b => b.Password.Length > 0);

    public string SharePointSecret => SecretBox.Password;

    public (string SharePoint, string Mail, string DocuWare) TypedSecrets()
        => (SecretBox.Password, MailSecretBox.Password, DocuWareSecretBox.Password);

    public void ClearSecrets()
    {
        SecretBox.Clear();
        MailSecretBox.Clear();
        DocuWareSecretBox.Clear();
        ConnectionSecretBox.Clear();
    }

    private async void OnTestMail(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.TestMailAsync(MailSecretBox.Password);
    }

    private async void OnTestDocuWare(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.TestDocuWareAsync(DocuWareSecretBox.Password);
    }

    private void OnEditConnectionKind(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionCard card) Vm?.OpenConnectionKind(card.Kind.Kind ?? "");
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.TestOpenConnectionAsync();
    }
}