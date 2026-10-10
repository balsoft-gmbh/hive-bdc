using System.IO;
using System.Reflection;
using Balsoft.Hive.Installer.Windows;

namespace Balsoft.Hive.BDC.Setup;

/// <summary>
/// Hive BDC's Setup: the product data for Balsoft.Hive.Installer.Windows. Hive BDC is installed
/// fresh: it has its own AppId and does not take over an earlier installation of another product.
/// </summary>
internal static class Program
{
    private static ProductDefinition CreateProduct() => new()
    {
        AppId = "{A536E22D-D00A-4FEF-A717-05E2FDC20F9A}",
        ProductName = "Hive BDC",
        DisplayName = "Hive BDC, by Balsoft",
        Publisher = "Balsoft GmbH",
        PublisherUrl = new Uri("https://balsoft.de"),
        HelpUrl = new Uri("https://balsoft.de/products"),
        InstallFolder = @"Balsoft\Hive BDC",
        MainExecutable = "HiveBDC.exe",
        UninstallerFileName = "HiveBDC-Setup.exe",
        KeepOnUpgrade = new[] { "appsettings.json" },
        // The dashboard's "Check for updates" starts Setup and then closes itself.
        CloseBeforeInstall = new[] { "HiveBDC.App.exe" },
        LaunchAfterInstall = "HiveBDC.App.exe",
        LaunchLabel = "Open the Hive BDC dashboard",
        Service = new ServiceDefinition
        {
            Name = "HiveBDC",
            Executable = "HiveBDC.exe",
            // An upgrade keeps the service and its run-as account; the account is passed on a fresh install only.
            InstallArguments = (exists, account, password) => exists || string.IsNullOrWhiteSpace(account)
                ? new[] { "install", "--quiet" }
                : new[] { "install", "--quiet", "--account", account.Trim(), "--password", password ?? "" },
            UninstallArguments = new[] { "uninstall", "--quiet" },
            AccountHint = "Leave both fields empty to run the service as LocalSystem, which is fine for local folders. " +
                          "A domain account (DOMAIN\\user) is needed when the input folders are on a network share. " +
                          "The account is checked with Windows before anything is installed.",
        },
        Shortcuts = new[]
        {
            new ShortcutDefinition { Name = "Hive BDC Dashboard", Target = "HiveBDC.App.exe", Description = "Hive BDC dashboard" },
        },
        Branding = new InstallerBranding
        {
            Logo = new Uri("pack://application:,,,/Assets/hive-bdc-logo.png"),
            SideImage = new Uri("pack://application:,,,/Assets/wizard-image.png"),
            Description = "Hive BDC turns the documents Dynamics AX / Dynamics 365 exports into e-invoices " +
                          "(XRechnung, ZUGFeRD) and PDFs, and delivers them by mail and to SharePoint.",
            LicenseText = ReadResource("eula.txt"),
            AfterInstallHint = "Open the dashboard to set up input folders, SharePoint and mail.",
            UninstallKeepsNote = "The licence, the logs and the export ledger in C:\\ProgramData\\HiveBDC are kept.",
        },
    };

    [STAThread]
    private static int Main(string[] args)
    {
        // pack:// URIs (the branding images) only parse once WPF has started, so the Application is
        // created before the definition. InstallerApp.Run uses this Application.
        _ = new System.Windows.Application();
        return InstallerApp.Run(CreateProduct(), args);
    }

    private static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return s is null ? "" : new StreamReader(s).ReadToEnd();
    }
}
