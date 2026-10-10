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
        + (Enabled ? "" : " · disabled") + (Discovered ? " · auto-registered" : "");

    partial void OnIdChanged(string value) { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Detail)); }
    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Label));
    partial void OnPartyChanged(string value) => OnPropertyChanged(nameof(Detail));
    partial void OnHandlerChanged(string value) => OnPropertyChanged(nameof(Detail));
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(Detail));

    public DocumentTypeEditViewModel()
    {
        Companies.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Detail));
    }

    public DocumentTypeEditViewModel(DocumentTypeConfigDto dto) : this()
    {
        Id = dto.Id ?? "";
        DisplayName = dto.DisplayName ?? "";
        Party = Parties.FirstOrDefault(p => p.Equals(dto.Party, StringComparison.OrdinalIgnoreCase)) ?? "Customer";
        Handler = Handlers.FirstOrDefault(h => h.Equals(dto.Handler, StringComparison.OrdinalIgnoreCase)) ?? "Pdf";
        Enabled = dto.Enabled;
        Mail = dto.Mail;
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
        Companies = Companies.Select(r => r.ToDto()).ToList(),
    };

    // ── company entries ───────────────────────────────────────────────────────

    /// <summary>Add a company entry; <paramref name="template"/> (another type's entry) pre-fills company, folder and target.</summary>
    public void AddCompany(RouteRow? template = null)
    {
        var row = template is null ? new RouteRow() : new RouteRow(template.ToDto()) { Output = "" };
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
