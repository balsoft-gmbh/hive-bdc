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
        SecretSet ? "A secret is stored. Leave this empty to keep it." : "No secret stored yet.";

    partial void OnSecretSetChanged(bool value) { OnPropertyChanged(nameof(SecretPlaceholderHint)); OnPropertyChanged(nameof(SharePointAppSetUp)); OnPropertyChanged(nameof(SharePointAppDetail)); }

    // ── Document mail (shared) ────────────────────────────────────────────────
    [ObservableProperty] private bool _mailEnabled;
    [ObservableProperty] private string _mailTenantId = "";
    [ObservableProperty] private string _mailClientId = "";
    [ObservableProperty] private string _mailFrom = "";
    /// <summary>Whether the service reports a stored mail secret — drives the PasswordBox hint.</summary>
    [ObservableProperty] private bool _mailSecretSet;

    // ── navigation: one level per screen ──────────────────────────────────────
    // Page is the entry in the left menu. Sub is the level below it: on Export "type", "company" or
    // "sharepoint" (a company's SharePoint library); on Connections "sharepoint-app", "docuware" or "mail".
    [ObservableProperty] private string _page = "Outbound";
    [ObservableProperty] private string _sub = "";

    /// <summary>The screen the content area shows, e.g. "Outbound", "Outbound/company", "Inbound/pick", "Connections/docuware".</summary>
    public string View => Sub.Length == 0 ? Page : Page + "/" + Sub;

    private string _lastView = "Outbound";

    partial void OnPageChanged(string value) => ViewChanged();
    partial void OnSubChanged(string value) => ViewChanged();

    private void ViewChanged()
    {
        if (_lastView == "Inbound/company" && View != "Inbound/company") CommitOpenInbound();
        if (_lastView == "Outbound/destination" && View != "Outbound/destination") CommitOpenDestination();
        if (_lastView == "Connections/conn" && View != "Connections/conn") CommitOpenConnection();
        _lastView = View;
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(CrumbType));
        OnPropertyChanged(nameof(CrumbCompany));
    }

    /// <summary>The breadcrumb's middle and last parts: the opened document type and company, outbound or inbound.</summary>
    public string CrumbType => Page == "Inbound" ? SelectedInboundType?.Name ?? "" : SelectedDocumentType?.Label ?? "";
    public string CrumbCompany => Page == "Inbound" ? OpenInbound?.Title ?? "" : SelectedDocumentType?.SelectedCompany?.TabTitle ?? "";

    [RelayCommand]
    private void ShowPage(string page)
    {
        Page = page;
        Sub = "";
        RefreshSummaries();
    }

    /// <summary>Opens a screen below the current page: a connection, or a level of the opened document type.</summary>
    [RelayCommand]
    private void Open(string sub)
    {
        Sub = sub;
        RefreshSummaries();
    }

    [RelayCommand]
    private void Back()
    {
        Sub = Sub switch { "sharepoint" or "destination" or "pick-destination" => "company", "company" or "pick" => "type", _ => "" };
        RefreshSummaries();
    }

    public void OpenType(DocumentTypeEditViewModel type)
    {
        SelectedDocumentType = type;
        Page = "Outbound";
        Open("type");
    }

    public void OpenCompany(RouteRow company)
    {
        if (SelectedDocumentType is null) return;
        SelectedDocumentType.SelectedCompany = company;
        Open("company");
    }

    [RelayCommand]
    private void NewDocumentType()
    {
        AddDocumentType();
        if (SelectedDocumentType is { } type) OpenType(type);
    }

    public void RemoveType(DocumentTypeEditViewModel type)
    {
        SelectedDocumentType = type;
        RemoveDocumentType();
        RefreshSummaries();
    }

    public void AddCompanyToOpenType()
    {
        if (SelectedDocumentType is not { } type) return;
        type.AddCompany();
        if (type.SelectedCompany is { } row) OpenCompany(row);
    }

    public void RemoveCompany(RouteRow company)
    {
        if (SelectedDocumentType is not { } type) return;
        type.SelectedCompany = company;
        type.RemoveCompanyCommand.Execute(null);
        RefreshSummaries();
    }

    /// <summary>The card lines and connection states are computed; this re-reads them after edits.</summary>
    public void RefreshSummaries()
    {
        foreach (var t in DocumentTypes)
        {
            t.MailConnected = MailSetUp;
            t.RefreshSummary();
        }
        foreach (var name in new[] { nameof(SharePointAppSetUp), nameof(SharePointAppDetail), nameof(MailSetUp), nameof(MailDetail),
                     nameof(DocuWareSetUp), nameof(DocuWareDetail), nameof(ExportDestinations) })
            OnPropertyChanged(name);
    }

    public bool SharePointAppSetUp => TenantId.Trim().Length > 0 && ClientId.Trim().Length > 0 && SecretSet;

    public string SharePointAppDetail
    {
        get
        {
            int used = DocumentTypes.Count(t => t.Companies.Any(c => c.SharePointEnabled));
            return SharePointAppSetUp
                ? $"Microsoft 365 app of your company. Used by {used} document type(s)."
                : "Needed before documents can go to a SharePoint library.";
        }
    }

    public bool MailSetUp => MailEnabled && MailClientId.Trim().Length > 0 && MailFrom.Trim().Length > 0 && MailSecretSet;

    public string MailDetail => MailSetUp
        ? $"Sends from {MailFrom.Trim()}."
        : MailEnabled ? "Switched on, but the app or the sending mailbox is missing." : "Send each document to the customer or vendor from a company mailbox.";

    public bool DocuWareSetUp => DocuWareUrl.Trim().Length > 0 && DocuWareUserName.Trim().Length > 0 && DocuWareSecretSet;

    public string DocuWareDetail => DocuWareSetUp
        ? $"{HostOf(DocuWareUrl)} as {DocuWareUserName.Trim()}."
        : "Store documents in DocuWare file cabinets, or import from them.";

    /// <summary>The places export documents go to, for the direction strip on the Export page.</summary>
    public string ExportDestinations
    {
        get
        {
            var places = new List<string>();
            if (DocumentTypes.Any(t => t.AnySharePoint)) places.Add("SharePoint");
            if (DocumentTypes.Any(t => t.AnyDocuWare)) places.Add("DocuWare");
            if (MailEnabled && DocumentTypes.Any(t => t.Companies.Any(c => c.Mail))) places.Add("Mail");
            return places.Count == 0 ? "no destination yet" : string.Join(" · ", places);
        }
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.Host : url.Trim();

    // DocuWare connection (Connections page). Further connections in the file are kept untouched.
    public const string DocuWareConnectionId = "docuware";
    private List<ConnectionConfigDto> _connections = new();
    [ObservableProperty] private string _docuWareUrl = "";
    [ObservableProperty] private string _docuWareUserName = "";
    [ObservableProperty] private bool _docuWareSecretSet;
    [ObservableProperty] private string _docuWareStatus = "";
    [ObservableProperty] private bool _docuWareStatusIsError;

    public string DocuWareSecretHint =>
        DocuWareSecretSet ? "A password is stored; leave blank to keep it." : "No password stored yet.";

    partial void OnDocuWareSecretSetChanged(bool value) { OnPropertyChanged(nameof(DocuWareSecretHint)); OnPropertyChanged(nameof(DocuWareSetUp)); OnPropertyChanged(nameof(DocuWareDetail)); }
    partial void OnDocuWareUrlChanged(string value) { OnPropertyChanged(nameof(DocuWareSetUp)); OnPropertyChanged(nameof(DocuWareDetail)); }
    partial void OnDocuWareUserNameChanged(string value) { OnPropertyChanged(nameof(DocuWareSetUp)); OnPropertyChanged(nameof(DocuWareDetail)); }
    partial void OnTenantIdChanged(string value) { OnPropertyChanged(nameof(SharePointAppSetUp)); OnPropertyChanged(nameof(SharePointAppDetail)); }
    partial void OnClientIdChanged(string value) { OnPropertyChanged(nameof(SharePointAppSetUp)); OnPropertyChanged(nameof(SharePointAppDetail)); }
    partial void OnMailEnabledChanged(bool value) { OnPropertyChanged(nameof(MailSetUp)); OnPropertyChanged(nameof(MailDetail)); OnPropertyChanged(nameof(ExportDestinations)); }
    partial void OnMailClientIdChanged(string value) { OnPropertyChanged(nameof(MailSetUp)); OnPropertyChanged(nameof(MailDetail)); }
    partial void OnMailFromChanged(string value) { OnPropertyChanged(nameof(MailSetUp)); OnPropertyChanged(nameof(MailDetail)); }

    /// <summary>The DocuWare connection as the form shows it now; a blank password means the stored one.</summary>
    public DocuWareRequestDto CurrentDocuWare(string? password) => new()
    {
        Id = DocuWareConnectionId,
        Url = DocuWareUrl.Trim(),
        UserName = DocuWareUserName.Trim(),
        Secret = string.IsNullOrEmpty(password) ? null : password,
    };


    public async Task TestDocuWareAsync(string? password)
    {
        DocuWareStatus = "Signing in to DocuWare…";
        DocuWareStatusIsError = false;
        var res = await _client.TestDocuWareAsync(CurrentDocuWare(password));
        DocuWareStatusIsError = res.Data?.Ok != true;
        DocuWareStatus = res.Data?.Message ?? res.Error ?? "DocuWare could not be reached.";
        AppLog.Info($"Test DocuWare: {(DocuWareStatusIsError ? "failed" : "ok")}");
    }

    private List<ConnectionConfigDto> BuildConnections(string? docuWarePassword)
    {
        var list = _connections.Where(c => !string.Equals(c.Id, DocuWareConnectionId, StringComparison.OrdinalIgnoreCase))
            .Select(c => new ConnectionConfigDto { Id = c.Id, Kind = c.Kind, Settings = c.Settings, Secret = _pendingSecrets.GetValueOrDefault(c.Id ?? "") })
            .ToList();
        bool used = DocumentTypes.Any(t => t.Companies.Any(r => r.Targets.Any(x => x.IsDocuWare))) || Flows.Any(f => f.IsDocuWare);
        if (DocuWareUrl.Trim().Length > 0 || DocuWareSecretSet || used)
            list.Add(new ConnectionConfigDto
            {
                Id = DocuWareConnectionId,
                Kind = TargetRow.DocuWareKind,
                Settings = new() { ["Url"] = DocuWareUrl.Trim(), ["UserName"] = DocuWareUserName.Trim() },
                Secret = string.IsNullOrEmpty(docuWarePassword) ? null : docuWarePassword,
            });
        return list;
    }

    private string? _pendingDocuWareSecret;

    // ── Outbound destinations and connections of any kind the service reports ─────────────

    public System.Collections.ObjectModel.ObservableCollection<TargetInfoDto> Destinations { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<ConnectionKindDto> ConnectionKinds { get; } = new();

    /// <summary>Connection kinds without a page of their own (DocuWare and the SharePoint app have one).</summary>
    public System.Collections.ObjectModel.ObservableCollection<ConnectionCard> OtherConnections { get; } = new();

    [ObservableProperty] private DestinationViewModel? _openDestination;
    [ObservableProperty] private ConnectionViewModel? _openConnection;

    /// <summary>The window hands over the secret typed on the generic connection page, and clears that box.</summary>
    public Func<string>? UnsavedConnectionSecret { get; set; }
    public Action? ClearConnectionSecret { get; set; }

    private readonly Dictionary<string, string> _pendingSecrets = new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadCatalogAsync()
    {
        await LoadSourcesAsync();
        await LoadDestinationsAsync();
        var kinds = await _client.GetConnectionKindsAsync();
        ConnectionKinds.Clear();
        foreach (var k in kinds.Data?.Kinds ?? new()) ConnectionKinds.Add(k);
        RebuildConnectionCards();
    }

    private async Task LoadDestinationsAsync()
    {
        var res = await _client.GetTargetsAsync();
        var (docuWare, sharePoint) = UnsavedSecrets?.Invoke() ?? ("", "");
        Destinations.Clear();
        foreach (var d in res.Data?.Targets ?? new())
        {
            d.Connected |= d.ConnectionKind switch
            {
                "docuware" => DocuWareUrl.Trim().Length > 0 && DocuWareUserName.Trim().Length > 0 && (DocuWareSecretSet || docuWare.Length > 0),
                "sharepoint" => TenantId.Trim().Length > 0 && ClientId.Trim().Length > 0 && (SecretSet || sharePoint.Length > 0),
                _ => _connections.Any(c => string.Equals(c.Kind, d.ConnectionKind, StringComparison.OrdinalIgnoreCase)),
            };
            Destinations.Add(d);
        }
        foreach (var t in DocumentTypes.SelectMany(t => t.Companies).SelectMany(c => c.Targets))
            t.KindName = Destinations.FirstOrDefault(d => string.Equals(d.Kind, t.Kind, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
    }

    private void RebuildConnectionCards()
    {
        OtherConnections.Clear();
        foreach (var k in ConnectionKinds.Where(k => !string.Equals(k.Kind, "docuware", StringComparison.OrdinalIgnoreCase)))
        {
            var existing = _connections.FirstOrDefault(c => string.Equals(c.Kind, k.Kind, StringComparison.OrdinalIgnoreCase));
            OtherConnections.Add(new ConnectionCard(k, existing is not null,
                existing is null ? k.Description ?? "" : $"Connection \"{existing.Id}\" is set up."));
        }
    }

    /// <summary>"+ Add a destination": first the kind, from what the service reports.</summary>
    [RelayCommand]
    private async Task AddDestination()
    {
        await LoadDestinationsAsync();
        Open("pick-destination");
    }

    public void PickDestination(TargetInfoDto kind)
    {
        var company = SelectedDocumentType?.SelectedCompany;
        if (company is null) return;
        if (!kind.Connected)
        {
            ShowPage("Connections");
            if (kind.ConnectionKind == "sharepoint") Open("sharepoint-app");
            else if (kind.ConnectionKind == "docuware") Open("docuware");
            else OpenConnectionKind(kind.ConnectionKind ?? "");
            StatusMessage = $"Connect {kind.Name} first, then add it as a destination under Outbound.";
            StatusIsError = false;
            return;
        }
        if (kind.Kind == "sharepoint")
        {
            company.SharePointEnabled = true;
            Open("sharepoint");
            return;
        }
        string connection = kind.ConnectionKind == "docuware" ? DocuWareConnectionId
            : _connections.FirstOrDefault(c => string.Equals(c.Kind, kind.ConnectionKind, StringComparison.OrdinalIgnoreCase))?.Id ?? kind.ConnectionKind ?? "";
        var row = new TargetRow { Kind = kind.Kind ?? "", Connection = connection, Enabled = true, KindName = kind.Name ?? "" };
        company.PutTarget(row, null);
        OpenDestinationFor(kind, row);
    }

    public void EditDestination(TargetRow row)
    {
        var kind = Destinations.FirstOrDefault(d => string.Equals(d.Kind, row.Kind, StringComparison.OrdinalIgnoreCase));
        if (kind is null)
        {
            StatusMessage = "This destination was set up with a newer version of Hive BDC and is kept as it is.";
            StatusIsError = false;
            return;
        }
        OpenDestinationFor(kind, row);
    }

    private void OpenDestinationFor(TargetInfoDto kind, TargetRow row)
    {
        CommitOpenDestination();
        OpenDestination = new DestinationViewModel(_client, WithDrafts, kind, row);
        Open("destination");
        _ = OpenDestination.LoadAsync();
    }

    public void CommitOpenDestination()
    {
        if (OpenDestination is not { } open) return;
        SelectedDocumentType?.SelectedCompany?.PutTarget(open.ToTarget(), open.Row);
        OpenDestination = null;
        RefreshSummaries();
    }

    public void OpenConnectionKind(string kind)
    {
        var k = ConnectionKinds.FirstOrDefault(c => string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (k is null) return;
        CommitOpenConnection();
        var existing = _connections.FirstOrDefault(c => string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase));
        OpenConnection = new ConnectionViewModel(_client, k, existing,
            existing?.HasSecret == true || _pendingSecrets.ContainsKey(existing?.Id ?? k.Kind ?? ""));
        Page = "Connections";
        Open("conn");
    }

    /// <summary>Keeps the generic connection page's values for Save; a typed secret is sent only with Save.</summary>
    public void CommitOpenConnection()
    {
        if (OpenConnection is not { } open) return;
        string secret = UnsavedConnectionSecret?.Invoke() ?? "";
        var values = open.SettingValues();
        var entry = _connections.FirstOrDefault(c => string.Equals(c.Id, open.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null && secret.Length == 0 && values.Values.All(string.IsNullOrWhiteSpace))
        {
            OpenConnection = null;
            return;
        }
        if (entry is null)
        {
            entry = new ConnectionConfigDto { Id = open.Id, Kind = open.Kind.Kind };
            _connections.Add(entry);
        }
        entry.Settings = values;
        if (secret.Length > 0) _pendingSecrets[open.Id] = secret;
        ClearConnectionSecret?.Invoke();
        OpenConnection = null;
        RebuildConnectionCards();
    }

    public async Task TestOpenConnectionAsync()
    {
        if (OpenConnection is { } open) await open.TestAsync(UnsavedConnectionSecret?.Invoke() ?? "");
    }

    // ── Inbound: document types, each with its companies (one Flows entry per company) ───────

    /// <summary>Every inbound flow as the file holds it, one per company.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ImportFlowRow> Flows { get; } = new();

    /// <summary>The document systems the service reads from, for the "Where from?" tiles and the company page.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SourceInfoDto> Sources { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<InboundTypeRow> InboundTypes { get; } = new();

    [ObservableProperty] private InboundTypeRow? _selectedInboundType;
    [ObservableProperty] private InboundFlowViewModel? _openInbound;

    partial void OnSelectedInboundTypeChanged(InboundTypeRow? value) => OnPropertyChanged(nameof(CrumbType));
    partial void OnOpenInboundChanged(InboundFlowViewModel? value) => OnPropertyChanged(nameof(CrumbCompany));

    /// <summary>The window hands over the passwords typed but not saved, so a dropdown can be filled before Save.</summary>
    public Func<(string DocuWare, string SharePoint)>? UnsavedSecrets { get; set; }

    private ImportFlowRow? _changeSourceOf;

    public async Task LoadSourcesAsync()
    {
        var res = await _client.GetSourcesAsync();
        var (docuWare, sharePoint) = UnsavedSecrets?.Invoke() ?? ("", "");
        Sources.Clear();
        foreach (var s in res.Data?.Sources ?? new())
        {
            // Filled in on the form counts too: the dropdowns use the form's values before Save.
            if (s.ConnectionKind == "docuware")
                s.Connected |= DocuWareUrl.Trim().Length > 0 && DocuWareUserName.Trim().Length > 0 && (DocuWareSecretSet || docuWare.Length > 0);
            else if (s.ConnectionKind == "sharepoint")
                s.Connected |= TenantId.Trim().Length > 0 && ClientId.Trim().Length > 0 && (SecretSet || sharePoint.Length > 0);
            Sources.Add(s);
        }
        RebuildInbound();
    }

    private SourceInfoDto? SourceFor(string kind)
        => Sources.FirstOrDefault(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));

    private SourceRequestDto WithDrafts(SourceRequestDto request)
    {
        var (docuWare, sharePoint) = UnsavedSecrets?.Invoke() ?? ("", "");
        request.DocuWare = CurrentDocuWare(docuWare);
        request.SharePoint = new SharePointDraftDto { TenantId = TenantId.Trim(), ClientId = ClientId.Trim(), Secret = sharePoint.Length == 0 ? null : sharePoint };
        return request;
    }

    /// <summary>Groups the flows by document type and words each company card from its source's setting labels.</summary>
    public void RebuildInbound()
    {
        string? selected = SelectedInboundType?.Name;
        var pending = InboundTypes.Where(t => t.Companies.Count == 0).Select(t => t.Name).ToList();
        InboundTypes.Clear();
        foreach (var group in Flows.GroupBy(f => f.TypeLabel, StringComparer.OrdinalIgnoreCase))
        {
            var type = new InboundTypeRow(group.Key, ApplyTypeName);
            foreach (var flow in group)
            {
                Describe(flow);
                type.Companies.Add(flow);
            }
            type.SystemsText = string.Join(" · ", group.Select(f => SourceFor(f.SourceKind)?.Name ?? f.SourceKind).Distinct());
            InboundTypes.Add(type);
        }
        foreach (var name in pending.Where(n => InboundTypes.All(t => !t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            InboundTypes.Add(new InboundTypeRow(name, ApplyTypeName));
        SelectedInboundType = InboundTypes.FirstOrDefault(t => t.Name.Equals(selected ?? "", StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CrumbType));
    }

    private void Describe(ImportFlowRow flow)
    {
        var source = SourceFor(flow.SourceKind);
        if (source is null) { flow.CardSource = $"{flow.SourceKind}: set up with a newer version of Hive BDC, kept as it is"; flow.CardReady = ""; return; }
        string Shown(string key) => flow.Setting("Source", key + "Name") is { Length: > 0 } n ? n : flow.Setting("Source", key);
        var which = (source.Settings ?? new()).Where(s => s.Step != "after").ToList();
        var place = which.FirstOrDefault(s => !string.IsNullOrEmpty(s.Lookup));
        flow.CardSource = $"From {source.Name}" + (place is null || Shown(place.Key ?? "").Length == 0 ? "" : ", " + Shown(place.Key ?? ""))
                          + (flow.Folder.Length > 0 ? $", CSV to {flow.Folder}" : ", no folder set yet");
        flow.CardReady = which.Count >= 2 && Shown(which[^2].Key ?? "").Length > 0
            ? $"{which[^2].Label} {Shown(which[^2].Key ?? "")} {which[^1].Label} {Shown(which[^1].Key ?? "")}"
            : "Not set up yet";
    }

    /// <summary>Renaming an inbound document type renames it on every company of the type.</summary>
    private void ApplyTypeName(InboundTypeRow type, string oldName)
    {
        foreach (var flow in type.Companies)
        {
            var json = flow.ToJson();
            json["DocumentType"] = type.Name.Trim();
            string company = flow.DataAreaId;
            json["Name"] = company.Length > 0 ? $"{type.Name.Trim()} ({company})" : type.Name.Trim();
            flow.Replace(json);
        }
        OnPropertyChanged(nameof(CrumbType));
    }

    [RelayCommand]
    private void NewInboundType()
    {
        string name = "New document type";
        for (int n = 2; InboundTypes.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"New document type {n}";
        var type = new InboundTypeRow(name, ApplyTypeName);
        InboundTypes.Add(type);
        OpenInboundType(type);
    }

    public void OpenInboundType(InboundTypeRow type)
    {
        SelectedInboundType = type;
        Page = "Inbound";
        Open("type");
    }

    public void RemoveInboundType(InboundTypeRow type)
    {
        foreach (var flow in type.Companies.ToList()) Flows.Remove(flow);
        InboundTypes.Remove(type);
        RebuildInbound();
    }

    /// <summary>"+ Add company": first the document system it comes from.</summary>
    [RelayCommand]
    private async Task AddInboundCompany()
    {
        _changeSourceOf = null;
        await LoadSourcesAsync();
        Open("pick");
    }

    public async Task ChangeInboundSource()
    {
        _changeSourceOf = OpenInbound?.Row;
        CommitOpenInbound();
        await LoadSourcesAsync();
        Open("pick");
    }

    /// <summary>A tile on "Where from?": a new company of the opened type, or a new source for the company being changed.</summary>
    public void PickSource(SourceInfoDto source)
    {
        if (!source.Connected)
        {
            ShowPage("Connections");
            Open(source.ConnectionKind == "sharepoint" ? "sharepoint-app" : source.ConnectionKind ?? "");
            StatusMessage = $"Connect {source.Name} first, then add the company under Inbound.";
            StatusIsError = false;
            return;
        }
        var type = SelectedInboundType;
        if (type is null) return;
        ImportFlowRow row;
        if (_changeSourceOf is { } existing)
        {
            var json = existing.ToJson();
            json["Source"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = source.Kind, ["Connection"] = source.ConnectionKind == "docuware" ? "docuware" : "", ["Settings"] = new System.Text.Json.Nodes.JsonObject() };
            existing.Replace(json);
            row = existing;
        }
        else
        {
            string id = UniqueFlowId(type.Name);
            row = new ImportFlowRow(new System.Text.Json.Nodes.JsonObject
            {
                ["Id"] = id, ["Name"] = type.Name, ["Enabled"] = true, ["DocumentType"] = type.Name, ["DataAreaId"] = "",
                ["Source"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = source.Kind, ["Connection"] = source.ConnectionKind == "docuware" ? "docuware" : "", ["Settings"] = new System.Text.Json.Nodes.JsonObject() },
            });
            Flows.Add(row);
        }
        _changeSourceOf = null;
        RebuildInbound();
        if (SelectedInboundType?.Companies.Contains(row) == true) OpenInboundCompany(row);
    }

    public void OpenInboundCompany(ImportFlowRow row)
    {
        CommitOpenInbound();
        var source = SourceFor(row.SourceKind);
        if (source is null)
        {
            StatusMessage = "This company was set up with a newer version of Hive BDC and is kept as it is.";
            StatusIsError = false;
            return;
        }
        OpenInbound = new InboundFlowViewModel(_client, WithDrafts, source, row);
        Open("company");
        _ = OpenInbound.LoadAsync();
    }

    /// <summary>Writes the open company page back into its flow.</summary>
    public void CommitOpenInbound()
    {
        if (OpenInbound is not { } open || SelectedInboundType is not { } type) return;
        string id = open.Row.Id.Length > 0 ? open.Row.Id : UniqueFlowId(type.Name);
        open.Row.Replace(open.ToJson(id, type.Name.Trim()));
        OpenInbound = null;
        RebuildInbound();
    }

    public void RemoveInboundCompany(ImportFlowRow row)
    {
        Flows.Remove(row);
        RebuildInbound();
    }

    private string UniqueFlowId(string name)
    {
        string slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "inbound";
        string id = slug;
        for (int n = 2; Flows.Any(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); n++) id = $"{slug}-{n}";
        return id;
    }
    /// <summary>
    /// Where documents from a non-production AX go instead of to the partner. One text box
    /// taking either a semicolon-separated list or one address per line, because both are
    /// how people actually paste a set of addresses.
    /// </summary>
    [ObservableProperty] private string _mailRedirectTo = "";
    [ObservableProperty] private bool _mailSaveToSentItems = true;

    public string MailSecretPlaceholderHint =>
        MailSecretSet ? "A secret is stored. Leave this empty to keep it." : "No secret stored yet.";

    partial void OnMailSecretSetChanged(bool value) { OnPropertyChanged(nameof(MailSecretPlaceholderHint)); OnPropertyChanged(nameof(MailSetUp)); OnPropertyChanged(nameof(MailDetail)); }

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
    /// <summary>True right after a successful save, until another message replaces the saved one.</summary>
    [ObservableProperty] private bool _justSaved;
    /// <summary>True while the footer asks whether to close with changes that are not saved.</summary>
    [ObservableProperty] private bool _confirmClose;

    private const string SavedMessage = "Changes saved";
    private readonly UnsavedChanges _unsaved = new();

    partial void OnStatusMessageChanged(string value)
    {
        if (!value.StartsWith(SavedMessage, StringComparison.Ordinal)) JustSaved = false;
    }

    /// <summary>The form as it stands differs from what was loaded or saved (an open page's values not yet included).</summary>
    public bool FormDiffersFromSaved() => _unsaved.Differ(BuildConfig(null), passwordTyped: false);

    /// <summary>True when the form holds anything the service does not have yet; an open page's values count.</summary>
    public bool HasUnsavedChanges(bool passwordTyped)
    {
        CommitOpenPages();
        return _unsaved.Differ(BuildConfig(null), passwordTyped);
    }

    /// <summary>Takes the values of an open company, destination or connection page into the settings and leaves the page.</summary>
    private void CommitOpenPages()
    {
        CommitOpenInbound();
        CommitOpenDestination();
        CommitOpenConnection();
        if (View is "Inbound/company" or "Outbound/destination" or "Connections/conn") Back();
    }

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
                StatusMessage = res.Error ?? "The service is not reachable, so the settings could not be loaded.";
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

            Flows.Clear();
            foreach (var f in (c.Flows ?? new()).OfType<System.Text.Json.Nodes.JsonObject>())
                Flows.Add(new ImportFlowRow((System.Text.Json.Nodes.JsonObject)f.DeepClone()));
            _ = LoadCatalogAsync();

            _connections = c.Connections ?? new();
            var docuWare = _connections.FirstOrDefault(x => string.Equals(x.Id, DocuWareConnectionId, StringComparison.OrdinalIgnoreCase));
            DocuWareUrl = docuWare?.Settings?.GetValueOrDefault("Url") ?? "";
            DocuWareUserName = docuWare?.Settings?.GetValueOrDefault("UserName") ?? "";
            DocuWareSecretSet = docuWare?.HasSecret ?? false;
            DocuWareStatus = "";

            DocumentTypes.Clear();
            if (c.DocumentTypes is not null)
                foreach (var t in c.DocumentTypes)
                    DocumentTypes.Add(new DocumentTypeEditViewModel(t));
            SelectedDocumentType = DocumentTypes.FirstOrDefault();
            RefreshSummaries();

            int entries = DocumentTypes.Sum(t => t.Companies.Count);
            StatusMessage = $"Settings loaded: {DocumentTypes.Count} outbound document type(s) with {entries} compan{(entries == 1 ? "y" : "ies")}, {Flows.Count} inbound flow(s).";
            _unsaved.Remember(BuildConfig(null));
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

    /// <summary>
    /// Save a "HiveBDC.SharePointFields" sample for the selected document type and company
    /// (<paramref name="row"/>): what the entry has now, examples where it is empty. The user edits
    /// it and brings it back with "Import columns…". Writes only the file the user picks.
    /// </summary>
    [RelayCommand]
    private void SaveFieldTemplateSample(RouteRow? row)
    {
        if (row is null) return;
        var owner = DocumentTypes.FirstOrDefault(t => t.Companies.Contains(row)) ?? SelectedDocumentType;
        string typeId = owner?.Id.Trim() ?? "";
        string area = row.DataAreaId.Trim();

        string json = SharePointFieldImport.CreateSample(typeId, area, row.CurrentSharePoint());
        string stem = string.Join("-", new[] { "hivebdc-sharepoint-fields", typeId, area }.Where(p => p.Length > 0));
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');

        var dlg = new SaveFileDialog
        {
            Title = "Save a SharePoint field template sample",
            Filter = "SharePoint field templates (*.json)|*.json|All files (*.*)|*.*",
            FileName = stem + ".json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, json);
            AppLog.Info($"Saved a SharePoint field template sample for {typeId} / {area} to {Path.GetFileName(dlg.FileName)}.");
            StatusMessage = $"Sample saved: {Path.GetFileName(dlg.FileName)}. Edit it, then use \"Import columns…\".";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Save SharePoint field template sample to {dlg.FileName} failed: {ex.Message}");
            StatusMessage = "Could not save the sample: " + ex.Message;
            StatusIsError = true;
        }
    }

    /// <summary>
    /// Save the generic Hive BDC Word invoice template (German labels, every value a content control tagged with its
    /// BT number, E-Rechnung drop-down). Template authors copy their layout into it or add the same tags to their own.
    /// </summary>
    [RelayCommand]
    private void SaveWordTemplate()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save the Hive BDC Word invoice template",
            Filter = "Word document (*.docx)|*.docx",
            FileName = "HiveBDC-Rechnungsvorlage.docx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using (var fs = File.Create(dlg.FileName)) Documents.Word.WordTemplateBuilder.Write(fs);
            AppLog.Info($"Saved the Word invoice template to {Path.GetFileName(dlg.FileName)}.");
            StatusMessage = $"Template saved: {Path.GetFileName(dlg.FileName)}. Fill it in Word, then check it with \"Check a Word file…\".";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Save Word template to {dlg.FileName} failed: {ex.Message}");
            StatusMessage = "Could not save the template: " + ex.Message;
            StatusIsError = true;
        }
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
    /// (the window stays open and says so); populates <see cref="ValidationErrors"/> and StatusMessage on failure.
    /// </summary>
    public async Task<bool> SaveAsync(string? newSecret, string? newMailSecret = null, string? newDocuWareSecret = null)
    {
        CommitOpenPages();
        _pendingDocuWareSecret = newDocuWareSecret;
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
                StatusMessage = res.Error ?? "The service is not reachable. Nothing was saved.";
                StatusIsError = true;
                return false;
            }

            if (!r.Ok)
            {
                if (r.Errors is { Count: > 0 })
                    foreach (var e in r.Errors) ValidationErrors.Add(e);
                AppLog.Warn("Save settings rejected — " + string.Join(" | ", ValidationErrors));
                StatusMessage = "Not saved. Fix the points at the top first.";
                StatusIsError = true;
                return false;
            }

            AppLog.Info("Save settings: saved and applied live.");
            _pendingDocuWareSecret = null;
            _pendingMailSecret = null;
            _unsaved.Remember(BuildConfig(null));
            StatusMessage = $"{SavedMessage} at {DateTime.Now:HH:mm}. The service uses them from now on.";
            StatusIsError = false;
            JustSaved = true;
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
                StatusMessage = res.Error ?? "The service is not reachable, so the test could not run.";
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
                StatusMessage = res.Error ?? "The service is not reachable, so the test could not run.";
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
        Connections = BuildConnections(_pendingDocuWareSecret),
        Flows = new System.Text.Json.Nodes.JsonArray(Flows.Select(f => (System.Text.Json.Nodes.JsonNode)f.ToJson()).ToArray()),
    };

    /// <summary>
    /// The mail PasswordBox content for the save in flight. Set by <see cref="SaveAsync"/>
    /// so <see cref="BuildConfig"/> can reach it without widening its signature everywhere.
    /// </summary>
    private string? _pendingMailSecret;
}

/// <summary>An inbound document type: its name and its companies (each one Flows entry).</summary>
public sealed partial class InboundTypeRow : ObservableObject
{
    private readonly Action<InboundTypeRow, string> _renamed;

    public InboundTypeRow(string name, Action<InboundTypeRow, string> renamed)
    {
        _name = name;
        _renamed = renamed;
        Companies.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusKind)); };
    }

    public System.Collections.ObjectModel.ObservableCollection<ImportFlowRow> Companies { get; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _confirmRemove;
    [ObservableProperty] private string _systemsText = "";

    partial void OnNameChanged(string? oldValue, string newValue)
    {
        if (oldValue is not null && newValue.Trim().Length > 0) _renamed(this, oldValue);
    }

    public string StatusText
    {
        get
        {
            int on = Companies.Count(c => c.Enabled);
            return Companies.Count == 0 ? "Needs setup · no company yet"
                : on == 0 ? "Switched off"
                : on == Companies.Count ? $"Active · {on} compan{(on == 1 ? "y" : "ies")}"
                : $"Active · {on} of {Companies.Count} companies on";
        }
    }

    public string StatusKind => Companies.Count == 0 ? "setup" : Companies.Any(c => c.Enabled) ? "ok" : "off";
}

/// <summary>A card on Connections for a kind without a page of its own.</summary>
public sealed record ConnectionCard(ConnectionKindDto Kind, bool SetUp, string Detail)
{
    public string Name => Kind.Name ?? Kind.Kind ?? "";
    public string Initial => Kind.Initial;
}