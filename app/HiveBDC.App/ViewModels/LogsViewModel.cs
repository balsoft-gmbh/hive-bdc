using System.Diagnostics;
using System.IO;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// Backs Service > Technical log: the tail of the current day's log file (up to 5000 lines) over the loopback API,
/// newest entry first, filtered by type (design 2026-11-21). Never reads invoice content, only the service's log lines.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly LoopbackClient _client;

    public LogsViewModel(LoopbackClient client) => _client = client;

    [ObservableProperty] private IReadOnlyList<LogLine> _logLines = new List<LogLine> { new("(loading…)", "OTH") };
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>The chosen type, an id of <see cref="LogTypes.Choices"/>; kept while the app runs.</summary>
    [ObservableProperty] private string _logType = LogTypes.All;

    public IReadOnlyList<LogTypes.Choice> TypeChoices => LogTypes.Choices;

    private List<LogLine>? _all;
    private string? _file;

    partial void OnLogTypeChanged(string value) => ApplyType();

    private void ApplyType()
    {
        if (_all is null) return;
        var shown = LogParser.Filter(_all, LogType);
        LogLines = shown.Count > 0 ? shown : LogParser.Parse(new[] { LogTypes.NoLines(_all.Count) });
        StatusMessage = LogTypes.Count(shown.Count, _all.Count) + (string.IsNullOrEmpty(_file) ? "" : $", {_file}");
    }

    /// <summary>Fetch the log tail. Called on open, by the auto-refresh timer, and by the Refresh button.</summary>
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var res = await _client.GetLogsAsync(5000);
            if (res is { Reachable: true, Data: { } d })
            {
                if (d.Lines is { Count: > 0 })
                {
                    _all = LogParser.ParseNewestFirst(d.Lines);
                    _file = d.File;
                    ApplyType();
                }
                else
                {
                    _all = null;
                    LogLines = LogParser.Parse(new[] { d.Note ?? d.Error ?? "(no log lines yet)" });
                    StatusMessage = "";
                }
            }
            else
            {
                _all = null;
                LogLines = LogParser.Parse(new[] { "(service not reachable, logs unavailable)" });
                StatusMessage = res.Error ?? "";
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private Task Refresh() => RefreshAsync();

    /// <summary>Open the log folder in Explorer (configured LogDir, else %ProgramData%\HiveBDC\logs).</summary>
    [RelayCommand]
    private async Task OpenLogFolder()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HiveBDC", "logs");
        var cfg = await _client.GetConfigAsync();
        if (cfg is { Reachable: true, Data.LogDir: { Length: > 0 } configured })
            dir = configured;
        try
        {
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Warn($"Open log folder {dir} failed: {ex.Message}"); }
    }
}
