using System.Collections.ObjectModel;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// One document type in Settings: what it is (id, name, sent to, produced as, switches) and
/// the AX companies it is picked up for (<see cref="Companies"/>), each with its own input
/// and output folder and SharePoint target. Round-trips to/from <see cref="DocumentTypeConfigDto"/>.
/// </summary>
public sealed partial class DocumentTypeEditViewModel : ObservableObject
{
    public static readonly string[] Parties = { "Customer", "Vendor" };
    public static readonly string[] Handlers = { "Pdf", "EInvoice" };

    public sealed record Choice(string Value, string Label);

    /// <summary>"Produced as" in words; the value stays the handler name the settings file stores.</summary>
    public static readonly Choice[] HandlerChoices =
    {
        new("EInvoice", "E-invoice (XRechnung or ZUGFeRD)"),
        new("Pdf", "PDF as the ERP made it"),
    };

    /// <summary>Remove was clicked once; the card asks before the item goes.</summary>
    [ObservableProperty] private bool _confirmRemove;

    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _party = "Customer";
    [ObservableProperty] private string _handler = "Pdf";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _mail = true;
    [ObservableProperty] private bool _discovered;

    public ObservableCollection<RouteRow> Companies { get; } = new();
    [ObservableProperty] private RouteRow? _selectedCompany;

    public bool HasCompanySelection => SelectedCompany is not null;
    partial void OnSelectedCompanyChanged(RouteRow? value) => OnPropertyChanged(nameof(HasCompanySelection));

    /// <summary>Label for the master list: the display name, else the id, else a placeholder.</summary>
    public string Label => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName.Trim()
                         : !string.IsNullOrWhiteSpace(Id) ? Id.Trim() : "(new document type)";

    /// <summary>Second line of the master list entry: id, party, handler, company count and state.</summary>
    public string Detail =>
        $"{(string.IsNullOrWhiteSpace(Id) ? "no id yet" : Id.Trim())} · {Party} · {(Handler == "EInvoice" ? "e-invoice" : "PDF")}"
        + $" · {Companies.Count} compan{(Companies.Count == 1 ? "y" : "ies")}"
        + (Companies.Any(c => c.IsWord) ? " · Word" : "")
        + (Enabled ? "" : " · disabled") + (Discovered ? " · auto-registered" : "");

    /// <summary>Card lines on the Export page.</summary>
    public string StatusText
    {
        get
        {
            int on = Companies.Count(c => c.Enabled);
            return !Enabled ? "Switched off"
                : Companies.Count == 0 ? "Needs setup · no company yet"
                : on == 0 ? "Active, but every company is switched off"
                : on == Companies.Count ? $"Active · {on} compan{(on == 1 ? "y" : "ies")}"
                : $"Active · {on} of {Companies.Count} companies on";
        }
    }

    /// <summary>ok, setup or off: picks the colour of the status dot.</summary>
    public string StatusKind => !Enabled ? "off" : Companies.Count == 0 ? "setup" : "ok";

    public string SourceText
    {
        get
        {
            var kinds = new List<string>();
            if (Companies.Any(c => c.IsJson)) kinds.Add("ERP export");
            if (Companies.Any(c => c.IsWord)) kinds.Add("Word");
            return kinds.Count == 0 ? "ERP export" : string.Join(" · ", kinds);
        }
    }

    /// <summary>Set by Settings: whether Document mail is connected, for the mail chip on the card.</summary>
    [ObservableProperty] private bool _mailConnected;

    /// <summary>on, waiting (switched on but mail is not connected) or off.</summary>
    public string MailState => !Companies.Any(c => c.Mail && c.Enabled) ? "off" : MailConnected ? "on" : "waiting";

    partial void OnMailConnectedChanged(bool value) => OnPropertyChanged(nameof(MailState));

    public bool IsCustomer
    {
        get => Party != "Vendor";
        set { if (value) Party = "Customer"; }
    }

    public bool IsVendor
    {
        get => Party == "Vendor";
        set { if (value) Party = "Vendor"; }
    }

    public bool MakesEInvoice
    {
        get => Handler == "EInvoice";
        set { if (value) Handler = "EInvoice"; }
    }

    public bool MakesPdf
    {
        get => Handler != "EInvoice";
        set { if (value) Handler = "Pdf"; }
    }

    public string ProducedText => Handler == "EInvoice" ? "E-invoice" : "PDF as it is";

    public string ProducedDetail => Handler == "EInvoice"
        ? "XRechnung or ZUGFeRD, as each document says."
        : "The PDF the ERP made, unchanged.";

    public string PartyText => Party == "Vendor" ? "to vendors" : "to customers";

    public bool AnySharePoint => Companies.Any(c => c.SharePointEnabled);
    public bool AnyDocuWare => Companies.Any(c => c.Targets.Any(t => t.Enabled && t.IsDocuWare));

    public void RefreshSummary()
    {
        foreach (var name in new[] { nameof(StatusText), nameof(StatusKind), nameof(SourceText), nameof(ProducedText), nameof(ProducedDetail),
                     nameof(PartyText), nameof(AnySharePoint), nameof(AnyDocuWare), nameof(Label), nameof(Detail), nameof(MailState) })
            OnPropertyChanged(name);
        foreach (var c in Companies) c.RefreshSummary();
    }

    partial void OnIdChanged(string value) { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Detail)); }
    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Label));
    partial void OnPartyChanged(string value)
    {
        foreach (var name in new[] { nameof(Detail), nameof(PartyText), nameof(IsCustomer), nameof(IsVendor) })
            OnPropertyChanged(name);
    }
    partial void OnHandlerChanged(string value)
    {
        foreach (var name in new[] { nameof(Detail), nameof(ProducedText), nameof(ProducedDetail), nameof(MakesEInvoice), nameof(MakesPdf) })
            OnPropertyChanged(name);
    }
    partial void OnEnabledChanged(bool value) { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusKind)); }

    public DocumentTypeEditViewModel()
    {
        Companies.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<RouteRow>() ?? Enumerable.Empty<RouteRow>())
                row.PropertyChanged += (_, p) => { if (p.PropertyName == nameof(RouteRow.IsWord)) OnPropertyChanged(nameof(Detail)); };
            OnPropertyChanged(nameof(Detail));
        };
    }

    public DocumentTypeEditViewModel(DocumentTypeConfigDto dto) : this()
    {
        Id = dto.Id ?? "";
        DisplayName = dto.DisplayName ?? "";
        Party = Parties.FirstOrDefault(p => p.Equals(dto.Party, StringComparison.OrdinalIgnoreCase)) ?? "Customer";
        Handler = Handlers.FirstOrDefault(h => h.Equals(dto.Handler, StringComparison.OrdinalIgnoreCase)) ?? "Pdf";
        Enabled = dto.Enabled;
        Mail = dto.Mail;
        _compressPdf = dto.CompressPdf;
        Discovered = dto.Discovered;
        if (dto.Companies is not null)
            foreach (var r in dto.Companies)
                Companies.Add(new RouteRow(r));
        SelectedCompany = Companies.FirstOrDefault();
    }

    public DocumentTypeConfigDto ToDto() => new()
    {
        Id = Id?.Trim() ?? "",
        DisplayName = DisplayName?.Trim() ?? "",
        Party = Party,
        Handler = Handler,
        Enabled = Enabled,
        Mail = Mail,
        CompressPdf = _compressPdf,
        Companies = Companies.Select(r => r.ToDto()).ToList(),
    };

    /// <summary>The type's own Compress PDF switch as the service sent it; each company has its effective value.</summary>
    private bool? _compressPdf;

    // ── company entries ───────────────────────────────────────────────────────

    public void AddCompany()
    {
        var row = new RouteRow();
        Companies.Add(row);
        SelectedCompany = row;
    }

    [RelayCommand] private void AddCompanyRow() => AddCompany();

    [RelayCommand]
    private void RemoveCompany()
    {
        if (SelectedCompany is null) return;
        int idx = Companies.IndexOf(SelectedCompany);
        Companies.Remove(SelectedCompany);
        SelectedCompany = Companies.Count == 0 ? null : Companies[Math.Min(idx, Companies.Count - 1)];
    }

    [RelayCommand]
    private void BrowseInput()
    {
        if (SelectedCompany is null) return;
        SelectedCompany.Input = PickFolder(SelectedCompany.Input) ?? SelectedCompany.Input;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        if (SelectedCompany is null) return;
        SelectedCompany.Output = PickFolder(SelectedCompany.Output) ?? SelectedCompany.Output;
    }

    private static string? PickFolder(string current)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder" };
        if (!string.IsNullOrWhiteSpace(current)) dlg.InitialDirectory = current;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
