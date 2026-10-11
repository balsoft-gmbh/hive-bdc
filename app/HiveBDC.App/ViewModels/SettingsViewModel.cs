using System.Collections.ObjectModel;
using System.IO;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// Backs the Settings window. Loads GET /config, exposes the shared connections (SharePoint
/// app, mail), the service settings (poll, log dir) and the list of <see cref="DocumentTypes"/>,
/// each carrying its own company entries (folders + SharePoint target), then pushes changes
/// back via POST /config, POST /config/test-sharepoint and POST /config/test-mail.
///
/// The raw secrets are never held here or read back from the service; they are handled only
/// as new values the user types (passed in from the PasswordBoxes at Save/Test time), and
/// blank means "keep the stored secret". Nothing is ever written to disk by the app.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LoopbackClient _client;

    public SettingsViewModel(LoopbackClient client) => _client = client;

    // ── Shared SharePoint auth ────────────────────────────────────────────────
    [ObservableProperty] private string _tenantId = "";
    [ObservableProperty] private string _clientId = "";
    /// <summary>Whether the service reports a stored shared secret — drives the PasswordBox hint.</summary>
    [ObservableProperty] private bool _secretSet;

    public string SecretPlaceholderHint =>
        SecretSet ? "A secret is stored — leave blank to keep it." : "No secret stored yet.";

    partial void OnSecretSetChanged(bool value) => OnPropertyChanged(nameof(SecretPlaceholderHint));

    // ── Document mail (shared) ────────────────────────────────────────────────
    [ObservableProperty] private bool _mailEnabled;
    [ObservableProperty] private string _mailTenantId = "";
    [ObservableProperty] private string _mailClientId = "";
    [ObservableProperty] private string _mailFrom = "";
    /// <summary>Whether the service reports a stored mail secret — drives the PasswordBox hint.</summary>
    [ObservableProperty] private bool _mailSecretSet;
    /// <summary>
    /// Where documents from a non-production AX go instead of to the partner. One text box
    /// taking either a semicolon-separated list or one address per line, because both are
    /// how people actually paste a set of addresses.
    /// </summary>
    [ObservableProperty] private string _mailRedirectTo = "";
    [ObservableProperty] private bool _mailSaveToSentItems = true;

    public string MailSecretPlaceholderHint =>
        MailSecretSet ? "A secret is stored — leave blank to keep it." : "No secret stored yet.";

    partial void OnMailSecretSetChanged(bool value) => OnPropertyChanged(nameof(MailSecretPlaceholderHint));

    private List<string> RedirectAddresses() =>
        (MailRedirectTo ?? "")
            .Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();

    // ── Global service settings ───────────────────────────────────────────────
    [ObservableProperty] private int _pollSeconds = 30;
    [ObservableProperty] private string _logDir = "";

    // ── Document types (master/detail) ────────────────────────────────────────
    public ObservableCollection<DocumentTypeEditViewModel> DocumentTypes { get; } = new();
    [ObservableProperty] private DocumentTypeEditViewModel? _selectedDocumentType;

    public bool HasDocumentTypeSelection => SelectedDocumentType is not null;
    partial void OnSelectedDocumentTypeChanged(DocumentTypeEditViewModel? value) => OnPropertyChanged(nameof(HasDocumentTypeSelection));

    // ── UI state ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    /// <summary>True when StatusMessage should render as an error (red), else success/info (green).</summary>
    [ObservableProperty] private bool _statusIsError;
    /// <summary>Inline validation errors returned by POST /config.</summary>
    public ObservableCollection<string> ValidationErrors { get; } = new();

    /// <summary>Load current settings from the service. Call once when the window opens.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading settings…";
        StatusIsError = false;
        try
        {
            var res = await _client.GetConfigAsync();
            if (res is not { Reachable: true, Data: { } c })
            {
                StatusMessage = res.Error ?? "Service not reachable — cannot load settings.";
                StatusIsError = true;
                return;
            }

            PollSeconds = c.PollSeconds;
            LogDir = c.LogDir ?? "";

            var auth = c.SharePointAuth;
            TenantId = auth?.TenantId ?? "";
            ClientId = auth?.ClientId ?? "";
            SecretSet = auth?.SecretSet ?? false;

            var mail = c.Mail;
            MailEnabled = mail?.Enabled ?? false;
            MailTenantId = mail?.TenantId ?? "";
            MailClientId = mail?.ClientId ?? "";
            MailFrom = mail?.From ?? "";
            MailSecretSet = mail?.SecretSet ?? false;
            MailRedirectTo = string.Join(Environment.NewLine, mail?.RedirectTo ?? new List<string>());
            MailSaveToSentItems = mail?.SaveToSentItems ?? true;

            DocumentTypes.Clear();
            if (c.DocumentTypes is not null)
                foreach (var t in c.DocumentTypes)
                    DocumentTypes.Add(new DocumentTypeEditViewModel(t));
            SelectedDocumentType = DocumentTypes.FirstOrDefault();

            int entries = DocumentTypes.Sum(t => t.Companies.Count);
            StatusMessage = $"Settings loaded — {DocumentTypes.Count} document type(s), {entries} company entr{(entries == 1 ? "y" : "ies")}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Document type list editing ────────────────────────────────────────────

    [RelayCommand]
    private void AddDocumentType()
    {
        var t = new DocumentTypeEditViewModel();
        // A new type most likely lives where the others do: start it with the sales invoice's
        // (or the first type's) company entries, so the user only adjusts what differs.
        var template = DocumentTypes.FirstOrDefault(x => x.Id.Equals("SalesInvoice", StringComparison.OrdinalIgnoreCase))
                       ?? DocumentTypes.FirstOrDefault();
        if (template is not null)
            foreach (var r in template.Companies) t.AddCompany(r);
        t.SelectedCompany = t.Companies.FirstOrDefault();
        DocumentTypes.Add(t);
        SelectedDocumentType = t;
    }

    [RelayCommand]
    private void RemoveDocumentType()
    {
        if (SelectedDocumentType is null) return;
        int idx = DocumentTypes.IndexOf(SelectedDocumentType);
        DocumentTypes.Remove(SelectedDocumentType);
        SelectedDocumentType = DocumentTypes.Count == 0 ? null : DocumentTypes[Math.Min(idx, DocumentTypes.Count - 1)];
    }

    // ── SharePoint field template import ──────────────────────────────────────

    /// <summary>What the last import could not place (the file has no entry for the selected company).</summary>
    public ObservableCollection<string> ImportNotes { get; } = new();

    /// <summary>
    /// Read a "HiveBDC.SharePointFields" file and apply the entry for the selected document type
    /// and company (<paramref name="row"/>, the button sits in that company's SharePoint card) to
    /// the form. Other entries in the file are left alone: the import is per company, like the
    /// column rows it fills. Nothing is saved: the user reviews the result and saves as usual,
    /// so the normal validation still runs.
    /// </summary>
    [RelayCommand]
    private void ImportFieldTemplates(RouteRow? row)
    {
        if (row is null) return;
        var owner = DocumentTypes.FirstOrDefault(t => t.Companies.Contains(row)) ?? SelectedDocumentType;
        if (owner is null) return;
        string typeId = owner.Id.Trim();
        string area = row.DataAreaId.Trim();
        string scopeLabel = $"{(typeId.Length == 0 ? "(no id yet)" : typeId)} / {(area.Length == 0 ? "(no company yet)" : area)}";

        var dlg = new OpenFileDialog
        {
            Title = "Import SharePoint field templates",
            Filter = "SharePoint field templates (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        ImportNotes.Clear();
        string json;
        try { json = File.ReadAllText(dlg.FileName); }
        catch (Exception ex)
        {
            AppLog.Warn($"Import SharePoint field templates: could not read {dlg.FileName}: {ex.Message}");
            StatusMessage = "Could not read the file: " + ex.Message;
            StatusIsError = true;
            return;
        }

        if (!SharePointFieldImport.TryParse(json, out var file, out var errors))
        {
            foreach (var e in errors) ImportNotes.Add(e);
            AppLog.Warn($"Import SharePoint field templates rejected ({Path.GetFileName(dlg.FileName)}) — " + string.Join(" | ", errors));
            StatusMessage = "Nothing imported: the file has problems (listed above).";
            StatusIsError = true;
            return;
        }

        // Only the entry for this type and company is applied; the rest of the file is ignored.
        var result = SharePointFieldImport.ApplyTo(file!, typeId, row);
        foreach (var s in result.Skipped) ImportNotes.Add(s);
        if (result.Applied.Count == 0)
        {
            AppLog.Warn($"Import SharePoint field templates ({Path.GetFileName(dlg.FileName)}): nothing for {scopeLabel} — " + string.Join(" | ", result.Skipped));
            StatusMessage = $"Nothing imported: the file has no entry for {scopeLabel}.";
            StatusIsError = true;
            return;
        }

        AppLog.Info($"Imported SharePoint field templates from {Path.GetFileName(dlg.FileName)} for {scopeLabel} — applied: " +
                    (result.Applied.Count == 0 ? "none" : string.Join("; ", result.Applied)) +
                    (result.Skipped.Count == 0 ? "" : $"; skipped {result.Skipped.Count}."));
        int n = result.Applied.Count;
        StatusMessage = n == 0
            ? $"Nothing imported for {scopeLabel}."
            : $"Imported the SharePoint target and columns for {scopeLabel}. Review, then Save.";
        StatusIsError = n == 0;
    }

    // ── Global file / folder pickers ──────────────────────────────────────────

    [RelayCommand] private void BrowseLogDir() => LogDir = PickFolder(LogDir) ?? LogDir;

    private static string? PickFolder(string current)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder" };
        if (!string.IsNullOrWhiteSpace(current)) dlg.InitialDirectory = current;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    // ── Save / Test ────────────────────────────────────────────────────────────

    /// <summary>
    /// POST /config with the current form values. <paramref name="newSecret"/> is the raw text
    /// from the shared PasswordBox — blank means keep the stored secret. Returns true on success
    /// (caller closes the window); populates <see cref="ValidationErrors"/> and StatusMessage on failure.
    /// </summary>
    public async Task<bool> SaveAsync(string? newSecret, string? newMailSecret = null)
    {
        _pendingMailSecret = newMailSecret;
        ValidationErrors.Clear();
        ImportNotes.Clear();
        IsBusy = true;
        StatusMessage = "Saving…";
        StatusIsError = false;
        try
        {
            AppLog.Info($"Save settings requested — {DocumentTypes.Count} document type(s), " +
                        $"{DocumentTypes.Sum(t => t.Companies.Count)} company entr(y/ies), poll={PollSeconds}s, " +
                        $"secret={(string.IsNullOrEmpty(newSecret) ? "unchanged" : "changed")}, " +
                        $"mail={(MailEnabled ? "on from " + MailFrom : "off")}, " +
                        $"mailSecret={(string.IsNullOrEmpty(newMailSecret) ? "unchanged" : "changed")}.");
            var dto = BuildConfig(newSecret);
            var res = await _client.SaveConfigAsync(dto);
            if (res is not { Reachable: true, Data: { } r })
            {
                AppLog.Warn("Save settings: " + (res.Error ?? "service not reachable — nothing saved."));
                StatusMessage = res.Error ?? "Service not reachable — nothing saved.";
                StatusIsError = true;
                return false;
            }

            if (!r.Ok)
            {
                if (r.Errors is { Count: > 0 })
                    foreach (var e in r.Errors) ValidationErrors.Add(e);
                AppLog.Warn("Save settings rejected — " + string.Join(" | ", ValidationErrors));
                StatusMessage = "Could not save — please fix the errors above.";
                StatusIsError = true;
                return false;
            }

            AppLog.Info("Save settings: saved and applied live.");
            StatusMessage = "Saved — applied live.";
            StatusIsError = false;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// POST /config/test-sharepoint for the SELECTED company entry of the selected type, tested
    /// against the shared auth. <paramref name="newSecret"/> blank => the service tests with the
    /// stored secret. Shows the {ok,message} result inline.
    /// </summary>
    public async Task TestSharePointAsync(string? newSecret)
    {
        var row = SelectedDocumentType?.SelectedCompany;
        if (row is null)
        {
            StatusMessage = "Select a document type and one of its company entries to test its SharePoint target.";
            StatusIsError = true;
            return;
        }

        string what = $"{SelectedDocumentType!.Label} / {(string.IsNullOrWhiteSpace(row.DataAreaId) ? "(no company)" : row.DataAreaId)}";
        IsBusy = true;
        StatusMessage = $"Testing SharePoint for {what}…";
        StatusIsError = false;
        try
        {
            AppLog.Info($"Test SharePoint requested for {what}.");
            var request = new TestSharePointRequestDto
            {
                SharePoint = row.ToDto().SharePoint,
                // Blank => null so it is omitted on the wire, i.e. test with the stored secret.
                ClientSecret = string.IsNullOrEmpty(newSecret) ? null : newSecret,
            };
            var res = await _client.TestSharePointAsync(request);
            if (res is not { Reachable: true, Data: { } r })
            {
                AppLog.Warn("Test SharePoint: " + (res.Error ?? "service not reachable — test could not run."));
                StatusMessage = res.Error ?? "Service not reachable — test could not run.";
                StatusIsError = true;
                return;
            }

            StatusMessage = string.IsNullOrWhiteSpace(r.Message)
                ? (r.Ok ? "SharePoint connection OK." : "SharePoint test failed.")
                : r.Message!;
            StatusIsError = !r.Ok;
            if (r.Ok) AppLog.Info("Test SharePoint OK — " + StatusMessage);
            else AppLog.Warn("Test SharePoint failed — " + StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// POST /config/test-mail with the current mail form values. <paramref name="newMailSecret"/>
    /// blank => the service tests with the stored secret. Sends no mail.
    /// </summary>
    public async Task TestMailAsync(string? newMailSecret)
    {
        IsBusy = true;
        StatusMessage = "Testing document mail…";
        StatusIsError = false;
        try
        {
            AppLog.Info($"Test document mail requested — from '{MailFrom}'.");
            var request = new TestMailRequestDto
            {
                Mail = BuildMail(newMailSecret),
                TenantId = string.IsNullOrWhiteSpace(MailTenantId) ? TenantId : MailTenantId,
            };
            var res = await _client.TestMailAsync(request);
            if (res is not { Reachable: true, Data: { } r })
            {
                AppLog.Warn("Test document mail: " + (res.Error ?? "service not reachable — test could not run."));
                StatusMessage = res.Error ?? "Service not reachable — test could not run.";
                StatusIsError = true;
                return;
            }

            StatusMessage = string.IsNullOrWhiteSpace(r.Message)
                ? (r.Ok ? "Mail connection OK." : "Mail test failed.")
                : r.Message!;
            StatusIsError = !r.Ok;
            if (r.Ok) AppLog.Info("Test document mail OK — " + StatusMessage);
            else AppLog.Warn("Test document mail failed — " + StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private MailConfigDto BuildMail(string? newMailSecret) => new()
    {
        Enabled = MailEnabled,
        TenantId = MailTenantId,
        ClientId = MailClientId,
        From = MailFrom,
        RedirectTo = RedirectAddresses(),
        SaveToSentItems = MailSaveToSentItems,
        // Blank => null so it is omitted on the wire, i.e. keep the stored secret.
        ClientSecret = string.IsNullOrEmpty(newMailSecret) ? null : newMailSecret,
    };

    private ConfigDto BuildConfig(string? newSecret) => new()
    {
        PollSeconds = PollSeconds,
        LogDir = LogDir,
        // License path is not user-managed — it defaults to %ProgramData%\HiveBDC\license.lic,
        // where "Renew license…" installs the .lic. Send blank to keep that default.
        License = new LicenseConfigDto { File = "" },
        SharePointAuth = new SharePointAuthConfigDto
        {
            TenantId = TenantId,
            ClientId = ClientId,
            // Blank => null so it is omitted on the wire, i.e. keep the stored secret.
            ClientSecret = string.IsNullOrEmpty(newSecret) ? null : newSecret,
        },
        Mail = BuildMail(_pendingMailSecret),
        DocumentTypes = DocumentTypes.Select(t => t.ToDto()).ToList(),
    };

    /// <summary>
    /// The mail PasswordBox content for the save in flight. Set by <see cref="SaveAsync"/>
    /// so <see cref="BuildConfig"/> can reach it without widening its signature everywhere.
    /// </summary>
    private string? _pendingMailSecret;
}
