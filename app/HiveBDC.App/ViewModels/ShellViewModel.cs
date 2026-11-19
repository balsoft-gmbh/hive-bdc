using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// The one window's navigation: the page the sidebar shows, the tabs of Document history and Service, and the question
/// before settings that are not saved get lost. Outbound, Inbound, Connections and Service share one settings form, so
/// moving between them keeps it; leaving them for Overview or Document history, or closing the window, asks first when
/// the form holds changes the service does not have. Free of WPF types so the tests can run it on any platform.
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    public const string Overview = "Overview";
    public const string History = "History";
    public const string Outbound = "Outbound";
    public const string Inbound = "Inbound";
    public const string Connections = "Connections";
    public const string Service = "Service";

    private readonly Func<bool> _unsavedSettings;
    private string _page = Overview;
    private string _historyTab = "Steps";
    private string _serviceTab = "Status";
    private string? _pendingPage;
    private bool _pendingClose;
    private bool _closeConfirmed;
    private object? _settings;

    public ShellViewModel(Func<bool> unsavedSettings, string userName, string machineName, bool elevated)
    {
        _unsavedSettings = unsavedSettings;
        UserName = userName;
        MachineName = machineName;
        Role = elevated ? "Administrator" : "Standard user";
        Initials = InitialsOf(userName);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asked to start a fresh settings form, after the user chose to drop the changes.</summary>
    public event Action? DiscardSettings;

    public string UserName { get; }
    public string MachineName { get; }
    public string Role { get; }
    public string Initials { get; }

    /// <summary>The dashboard view model (status, licence, exports), for the sidebar and the top bar.</summary>
    public object? Dashboard { get; init; }

    /// <summary>The settings form the SET UP pages edit; replaced after the user drops changes.</summary>
    public object? Settings
    {
        get => _settings;
        set { _settings = value; Raise(); }
    }

    public string Page
    {
        get => _page;
        private set
        {
            if (_page == value) return;
            _page = value;
            Raise();
            Raise(nameof(IsSettingsView));
            Raise(nameof(ShowSaveBar));
        }
    }

    public bool IsSettingsView => IsSettingsPage(Page);

    /// <summary>"Steps" or "Documents".</summary>
    public string HistoryTab
    {
        get => _historyTab;
        set { if (_historyTab == value) return; _historyTab = value; Raise(); Raise(nameof(HistoryTabTitle)); }
    }

    public string HistoryTabTitle => HistoryTab == "Documents" ? "Documents" : "Steps";

    /// <summary>"Status", "Licence", "Updates" or "Log".</summary>
    public string ServiceTab
    {
        get => _serviceTab;
        set
        {
            if (_serviceTab == value) return;
            _serviceTab = value;
            Raise();
            Raise(nameof(ServiceTabTitle));
            Raise(nameof(ShowSaveBar));
        }
    }

    public string ServiceTabTitle => ServiceTab == "Log" ? "Technical log" : ServiceTab;

    /// <summary>The save bar belongs to the pages that edit settings (on Service only the Status tab holds them), and carries the question.</summary>
    public bool ShowSaveBar => (IsSettingsPage(Page) && (Page != Service || ServiceTab == "Status")) || ConfirmLeave;

    public bool ConfirmLeave => _pendingPage is not null || _pendingClose;

    public string LeaveQuestion => _pendingClose
        ? "Some changes are not saved yet. Close without saving them?"
        : "Some changes are not saved yet. Leave without saving them?";

    public string LeaveAction => _pendingClose ? "Close without saving" : "Leave without saving";

    public static bool IsSettingsPage(string page) => page is Outbound or Inbound or Connections or Service;

    /// <summary>Opens a page. Returns false while the question about unsaved settings waits for an answer.</summary>
    public bool Navigate(string page)
    {
        if (page == Page)
        {
            ClearPending();
            return true;
        }
        if (IsSettingsPage(Page) && !IsSettingsPage(page) && _unsavedSettings())
        {
            _pendingPage = page;
            _pendingClose = false;
            RaisePending();
            return false;
        }
        ClearPending();
        Page = page;
        return true;
    }

    /// <summary>The settings form moved to another of its own pages (a destination that needs a connection opens Connections).</summary>
    public void Follow(string settingsPage)
    {
        if (IsSettingsPage(settingsPage) && IsSettingsPage(Page)) Page = settingsPage;
    }

    /// <summary>True when the window may close now; otherwise the question shows first.</summary>
    public bool RequestClose()
    {
        if (_closeConfirmed || !_unsavedSettings()) return true;
        _pendingClose = true;
        _pendingPage = null;
        RaisePending();
        return false;
    }

    public void KeepEditing() => ClearPending();

    /// <summary>Drops the changes and does what the question held back. Returns true when the window should close now.</summary>
    public bool LeaveWithoutSaving()
    {
        string? page = _pendingPage;
        bool close = _pendingClose;
        if (page is null && !close) return false;
        ClearPending();
        DiscardSettings?.Invoke();
        if (close)
        {
            _closeConfirmed = true;
            return true;
        }
        Page = page!;
        return false;
    }

    /// <summary>"AnnaWeber" or "anna.weber" as "AW"; one word gives one letter.</summary>
    public static string InitialsOf(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '_', '-', '\\', '@' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1) return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        if (parts.Length == 0) return "?";
        var capitals = parts[0].Where(char.IsUpper).Take(2).ToArray();
        return capitals.Length == 2 ? new string(capitals) : char.ToUpperInvariant(parts[0][0]).ToString();
    }

    private void ClearPending()
    {
        if (_pendingPage is null && !_pendingClose) return;
        _pendingPage = null;
        _pendingClose = false;
        RaisePending();
    }

    private void RaisePending()
    {
        Raise(nameof(ConfirmLeave));
        Raise(nameof(ShowSaveBar));
        Raise(nameof(LeaveQuestion));
        Raise(nameof(LeaveAction));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}