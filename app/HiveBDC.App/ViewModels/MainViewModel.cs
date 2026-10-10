using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// Dashboard view-model. Polls the loopback API (status + metrics every 3s, logs
/// every 5s — timers live in the window) and projects the JSON onto bindable
/// properties. Also drives Start/Stop of the Windows service and the About / Renew
/// actions. All service data arrives over the loopback HTTP API; the app never reads
/// document content or the ledger file itself.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LoopbackClient _client;
    private readonly ServiceControl _service;
    private readonly UpdateService _updates;

    public MainViewModel(LoopbackClient client, ServiceControl service, UpdateService updates)
    {
        _client = client;
        _service = service;
        _updates = updates;
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    /// <summary>Build the Settings view-model, sharing this window's loopback client.</summary>
    public SettingsViewModel CreateSettingsViewModel() => new(_client);

    /// <summary>Full-logs and all-documents view-models for the pop-out windows (shared client).</summary>
    public LogsViewModel CreateLogsViewModel() => new(_client);
    public DocumentsViewModel CreateDocumentsViewModel() => new(_client);

    /// <summary>Release-notes view-model, sharing the update service (embedded + feed changelog).</summary>
    public ChangelogViewModel CreateChangelogViewModel() => new(_updates);

    // ---- Connectivity ----
    [ObservableProperty] private bool _serviceReachable;
    [ObservableProperty] private string _connectionText = "Connecting…";

    // ---- Service status card ----
    [ObservableProperty] private string _serviceStatus = "Unknown";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _version = "—";
    [ObservableProperty] private string _companyCount = "—";
    [ObservableProperty] private string _pollInterval = "—";
    [ObservableProperty] private string _sharePoint = "—";
    /// <summary>"6 types" plus a hint when some are disabled, auto-registered or unlicensed.</summary>
    [ObservableProperty] private string _documentTypes = "—";
    [ObservableProperty] private string _documentTypesDetail = "";

    // ---- License card ----
    [ObservableProperty] private string _licenseState = "—";
    [ObservableProperty] private Brush _licenseColor = Brushes.Gray;
    [ObservableProperty] private string _licenseMessage = "";
    [ObservableProperty] private string _licenseCustomer = "—";
    [ObservableProperty] private string _licenseExpires = "—";
    /// <summary>True when the service reports processing is halted for a license reason (missing/invalid/expired past grace).</summary>
    [ObservableProperty] private bool _licenseBlocked;

    // ---- Metrics card ----
    [ObservableProperty] private string _exportedToday = "—";
    [ObservableProperty] private string _exportedMonth = "—";
    [ObservableProperty] private string _exportedTotal = "—";
    public ObservableCollection<RecentExportItem> Recent { get; } = new();

    // ---- Logs (parsed into colour-coded lines for the viewer) ----
    [ObservableProperty] private IReadOnlyList<LogLine> _logLines = new List<LogLine> { new("(no logs yet)", "OTH") };

    // ---- About ----
    [ObservableProperty] private string _appVersion;

    // ---- Action feedback ----
    [ObservableProperty] private string _actionMessage = "";

    /// <summary>Refresh /status + /metrics. Called on the UI thread by the 3s timer.</summary>
    public async Task RefreshStatusAndMetricsAsync()
    {
        // Service-control status is authoritative for the Running/Stopped pill.
        ServiceStatus = _service.GetStatus();

        var status = await _client.GetStatusAsync();
        if (status is { Reachable: true, Data: { } s })
        {
            AppLog.Recovered("Connection to the Hive BDC service");
            ServiceReachable = true;
            IsRunning = s.Running;
            ConnectionText = "Connected to service";
            Version = s.Version ?? "—";
            CompanyCount = s.CompanyCount == 1 ? "1 company" : $"{s.CompanyCount} companies";
            PollInterval = $"{s.PollSeconds}s";
            SharePoint = s.SharePoint ? "On" : "Off";
            ApplyDocumentTypes(s);
            ApplyLicense(s.License);
        }
        else
        {
            AppLog.Failed("Connection to the Hive BDC service", status.Error ?? "not reachable");
            ServiceReachable = false;
            IsRunning = _service.IsRunning();
            ConnectionText = status.Error ?? "Service not reachable";
            Version = "—";
            CompanyCount = "—";
            PollInterval = "—";
            SharePoint = "—";
            DocumentTypes = "—";
            DocumentTypesDetail = "";
            ApplyLicense(null);
        }

        var metrics = await _client.GetMetricsAsync();
        if (metrics is { Reachable: true, Data: { } m })
        {
            ExportedToday = m.Today.ToString();
            ExportedMonth = m.Month?.ToString() ?? "—";
            ExportedTotal = m.Total.ToString();
            SyncRecent(m.Recent);
        }
        else
        {
            ExportedToday = "—";
            ExportedMonth = "—";
            ExportedTotal = "—";
            Recent.Clear();
        }
    }

    /// <summary>Refresh /logs?tail=200. Called on the UI thread by the 5s timer.</summary>
    public async Task RefreshLogsAsync()
    {
        var logs = await _client.GetLogsAsync(200);
        if (logs is { Reachable: true, Data: { } d })
        {
            if (d.Lines is { Count: > 0 })
                LogLines = LogParser.Parse(d.Lines);
            else
                LogLines = LogParser.Parse(new[] { d.Note ?? d.Error ?? "(no log lines yet)" });
        }
        else
        {
            LogLines = LogParser.Parse(new[] { "(service not reachable — logs unavailable)" });
        }
    }

    private void ApplyDocumentTypes(StatusDto s)
    {
        var types = s.DocumentTypes ?? new List<DocumentTypeStatusDto>();
        int count = types.Count > 0 ? types.Count : s.DocumentTypeCount;
        DocumentTypes = count == 1 ? "1 type" : $"{count} types";

        var notes = new List<string>();
        int disabled = types.Count(t => !t.Enabled);
        int discovered = types.Count(t => t.Discovered);
        var unlicensed = types.Where(t => t.Enabled && !t.Licensed).Select(t => t.Id).ToList();
        int unrouted = types.Count(t => t.Enabled && t.CompanyCount == 0);
        if (disabled > 0) notes.Add($"{disabled} disabled");
        if (unrouted > 0) notes.Add($"{unrouted} without a company entry (not picked up)");
        if (discovered > 0) notes.Add($"{discovered} auto-registered, review in Settings");
        if (unlicensed.Count > 0) notes.Add("not licensed: " + string.Join(", ", unlicensed));
        DocumentTypesDetail = string.Join(" · ", notes);
    }

    private void ApplyLicense(LicenseDto? lic)
    {
        if (lic is null)
        {
            LicenseState = "Unknown";
            LicenseColor = Brushes.Gray;
            LicenseMessage = "";
            LicenseCustomer = "—";
            LicenseExpires = "—";
            LicenseBlocked = false;
            return;
        }

        LicenseState = lic.State ?? "Unknown";
        LicenseColor = ColorForState(lic.State);
        LicenseMessage = lic.Message ?? "";
        LicenseCustomer = string.IsNullOrWhiteSpace(lic.Customer) ? "—" : lic.Customer!;
        LicenseExpires = string.IsNullOrWhiteSpace(lic.Expires) ? "—" : lic.Expires!;
        LicenseBlocked = lic.Blocked;
    }

    private static Brush ColorForState(string? state) => state switch
    {
        "Valid" => Brushes.SeaGreen,
        "Warning" or "Grace" => Brushes.Goldenrod,
        "Expired" or "Invalid" => Brushes.Firebrick,
        "Missing" => Brushes.Gray,
        _ => Brushes.Gray,
    };

    private void SyncRecent(List<RecentExportDto>? recent)
    {
        Recent.Clear();
        if (recent is null) return;
        foreach (var e in recent)
            Recent.Add(RecentExportItem.From(e));
    }

    internal static string HumanSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int u = 0;
        while (value >= 1024 && u < units.Length - 1) { value /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{value:0.#} {units[u]}";
    }

    // Start is available only while stopped, Stop only while running. IsRunning changing
    // re-evaluates both (see OnIsRunningChanged), so the buttons enable/disable live.
    private bool CanStartService() => !IsRunning;
    private bool CanStopService() => IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        StartServiceCommand.NotifyCanExecuteChanged();
        StopServiceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartService))]
    private async Task StartService()
    {
        AppLog.Info("Start service requested.");
        ActionMessage = "Starting service…";
        var result = await Task.Run(_service.Start);
        ActionMessage = result.Message;
        if (result.Ok) AppLog.Info("Start service: " + result.Message);
        else AppLog.Warn("Start service failed: " + result.Message);
        await RefreshStatusAndMetricsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStopService))]
    private async Task StopService()
    {
        AppLog.Info("Stop service requested.");
        ActionMessage = "Stopping service…";
        var result = await Task.Run(_service.Stop);
        ActionMessage = result.Message;
        if (result.Ok) AppLog.Info("Stop service: " + result.Message);
        else AppLog.Warn("Stop service failed: " + result.Message);
        await RefreshStatusAndMetricsAsync();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshStatusAndMetricsAsync();
        await RefreshLogsAsync();
    }

    [RelayCommand]
    private async Task RenewLicense()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select the license file Balsoft emailed you",
            Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        AppLog.Info($"Renew license — selected file '{Path.GetFileName(dlg.FileName)}'.");

        string content;
        try { content = await File.ReadAllTextAsync(dlg.FileName); }
        catch (Exception ex)
        {
            AppLog.Warn("Renew license: could not read the file — " + ex.Message);
            ActionMessage = "Could not read the license file: " + ex.Message;
            return;
        }

        ActionMessage = "Installing license…";
        var res = await _client.UploadLicenseAsync(new LicenseUploadRequestDto
        {
            Content = content,
            FileName = Path.GetFileName(dlg.FileName),
        });

        if (res is not { Reachable: true, Data: { } r })
        {
            AppLog.Warn("Renew license: " + (res.Error ?? "service not reachable — not installed."));
            ActionMessage = res.Error ?? "Service not reachable — license not installed.";
            return;
        }

        // The service validates before installing; a rejection (bad signature, wrong
        // tenant, expired) comes back ok:false with the reason.
        if (r.Ok) AppLog.Info("Renew license: installed — " + r.Message);
        else AppLog.Warn("Renew license: rejected — " + r.Message);
        ActionMessage = r.Ok ? $"License installed — {r.Message}" : $"License rejected — {r.Message}";
        await RefreshStatusAndMetricsAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        AppLog.Info($"Check for updates requested (current v{AppVersion}).");
        ActionMessage = "Checking for updates…";
        var res = await _updates.CheckAsync(AppVersion);
        if (res.Error is not null)
        {
            AppLog.Warn("Update check failed: " + res.Error);
            ActionMessage = "Update check failed: " + res.Error;
            return;
        }

        if (!res.Available || res.Manifest is null)
        {
            AppLog.Info($"Update check: already up to date (v{AppVersion}).");
            ActionMessage = $"You're up to date (v{AppVersion}).";
            MessageBox.Show($"You're running the latest version (v{AppVersion}).",
                "No updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var m = res.Manifest;
        AppLog.Info($"Update available: v{m.Version} (current v{res.Current}).");
        var choice = MessageBox.Show(
            $"Version {m.Version} is available (you have {res.Current}).\n\n{m.Notes}\n\n" +
            "The Hive BDC service and dashboard will be updated. Continue?",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (choice != MessageBoxResult.Yes)
        {
            AppLog.Info("Update postponed by the user.");
            ActionMessage = "Update postponed.";
            return;
        }

        try
        {
            var progress = new Progress<int>(p => ActionMessage = $"Downloading update… {p}%");
            AppLog.Info($"Downloading update v{m.Version}…");
            string setup = await _updates.DownloadVerifiedAsync(m, progress);
            AppLog.Info($"Update v{m.Version} downloaded and verified — starting the installer.");
            ActionMessage = "Launching installer…";
            var pending = _updates.LaunchInstaller(setup, m.Version!, AppVersion, AppLog.Directory);
            AppLog.Info($"Installer running (PID {pending.SetupPid}), Setup log {pending.LogPath}; the dashboard closes so its files can be replaced.");
            // Setup waits for this process to exit, then stops the service, installs and reopens the dashboard.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Error("Update failed.", ex);
            ActionMessage = "Update failed: " + ex.Message;
        }
    }
}
