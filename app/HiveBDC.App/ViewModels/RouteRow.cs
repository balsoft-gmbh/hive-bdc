using System.Collections.ObjectModel;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// One company entry of a document type in Settings: the AX company (dataAreaId), its input
/// and output folder, and its SharePoint target with column templates. Edited in a form
/// (not a grid), one entry at a time.
/// </summary>
public sealed partial class RouteRow : ObservableObject, ISharePointFieldImportRoute
{
    /// <summary>Mail each document of this company to the recipient the ERP names.</summary>
    [ObservableProperty] private bool _mail = true;

    /// <summary>Whether this company of the document type runs; off = its folder is not read for this type.</summary>
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Remove was clicked once; the card asks before the item goes.</summary>
    [ObservableProperty] private bool _confirmRemove;

    [ObservableProperty] private string _dataAreaId = "";
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _output = "";
    /// <summary>The input folder receives filled Word invoices instead of ERP export pairs.</summary>
    [ObservableProperty] private bool _isWord;

    /// <summary>The other radio button of the input choice.</summary>
    public bool IsJson
    {
        get => !IsWord;
        set { if (value) IsWord = false; }
    }
    [ObservableProperty] private bool _sharePointEnabled;
    [ObservableProperty] private string _siteUrl = "";
    [ObservableProperty] private string _library = "";
    [ObservableProperty] private string _folder = "";

    /// <summary>SharePoint column templates (internal column name -> template), editable rows.</summary>
    public ObservableCollection<ColumnRow> Columns { get; } = new();

    /// <summary>Further places this entry delivers to (DocuWare, ...), after SharePoint and before mail.</summary>
    public ObservableCollection<TargetRow> Targets { get; } = new();

    [ObservableProperty] private ColumnRow? _selectedColumn;

    /// <summary>First line in the entry list: the company, or a placeholder.</summary>
    public string Label => string.IsNullOrWhiteSpace(DataAreaId) ? "(new company)" : DataAreaId.Trim();

    /// <summary>Second line in the entry list: the input folder's last segment and the SharePoint state.</summary>
    public string Summary
    {
        get
        {
            string folder = (Input ?? "").TrimEnd('\\', '/');
            int cut = folder.LastIndexOfAny(new[] { '\\', '/' });
            string tail = cut >= 0 ? folder[(cut + 1)..] : folder;
            if (tail.Length == 0) tail = "no input folder";
            if (IsWord) tail = "Word: " + tail;
            string sp = SharePointEnabled
                ? (string.IsNullOrWhiteSpace(Library) ? "SharePoint on" : "SharePoint: " + Library.Trim())
                : "no SharePoint";
            int more = Targets.Count(t => t.Enabled);
            return tail + " · " + sp + (more > 0 ? $" · +{more} more" : "");
        }
    }

    partial void OnDataAreaIdChanged(string value) { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(TabTitle)); }
    partial void OnInputChanged(string value) => OnPropertyChanged(nameof(Summary));
    partial void OnMailChanged(bool value) => OnPropertyChanged(nameof(DestinationLine));
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(DestinationLine));
    partial void OnIsWordChanged(bool value) { OnPropertyChanged(nameof(IsJson)); OnPropertyChanged(nameof(Summary)); }
    partial void OnSharePointEnabledChanged(bool value) { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(SharePointDetail)); }
    partial void OnLibraryChanged(string value) { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(SharePointDetail)); }

    public RouteRow() { }

    public RouteRow(RouteConfigDto dto)
    {
        DataAreaId = dto.DataAreaId ?? "";
        Input = dto.Input ?? "";
        IsWord = InputFormats.IsWord(dto.InputFormat);
        Output = dto.Output ?? "";
        Mail = dto.Mail ?? true;
        Enabled = dto.Enabled ?? true;
        var sp = dto.SharePoint;
        SharePointEnabled = sp?.Enabled ?? false;
        SiteUrl = sp?.SiteUrl ?? "";
        Library = sp?.Library ?? "";
        Folder = sp?.Folder ?? "";
        if (sp?.Fields is not null)
            foreach (var (col, tpl) in sp.Fields)
                Columns.Add(new ColumnRow(col, tpl));
        foreach (var t in dto.Targets ?? new())
            Targets.Add(TargetRow.FromDto(t));
        Targets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public string TabTitle => string.IsNullOrWhiteSpace(DataAreaId) ? "New company" : "Company " + DataAreaId.Trim();

    /// <summary>The SharePoint line in "Goes to".</summary>
    public string SharePointDetail
    {
        get
        {
            if (!SharePointEnabled) return "Off";
            if (string.IsNullOrWhiteSpace(SiteUrl) || string.IsNullOrWhiteSpace(Library)) return "On, but the site or library is missing. Configure it.";
            string site = SiteUrl.Trim().TrimEnd('/');
            site = site[(site.LastIndexOf('/') + 1)..];
            string place = site + " › " + Library.Trim() + (string.IsNullOrWhiteSpace(Folder) ? "" : " › " + Folder.Trim());
            return $"{place} · {Columns.Count(c => !string.IsNullOrWhiteSpace(c.Column))} column(s)";
        }
    }

    /// <summary>Lines on the company card of a document type.</summary>
    public string SourceLine => (IsWord ? "Word documents" : "ERP export")
        + (string.IsNullOrWhiteSpace(Input) ? ", no folder set yet" : " from " + Input.Trim());

    public string DestinationLine
    {
        get
        {
            var places = new List<string>();
            if (SharePointEnabled) places.Add("SharePoint");
            places.AddRange(Targets.Where(t => t.Enabled).Select(t => t.KindLabel));
            if (Mail) places.Add("Mail");
            string line = places.Count == 0 ? "No destination yet" : "Goes to " + string.Join(", ", places);
            return Enabled ? line : "Switched off for this document type. " + line;
        }
    }

    public void RefreshSummary()
    {
        foreach (var name in new[] { nameof(SharePointDetail), nameof(TabTitle), nameof(Summary), nameof(SourceLine), nameof(DestinationLine) })
            OnPropertyChanged(name);
    }

    /// <summary>Adds <paramref name="target"/>, or puts it in place of <paramref name="replaces"/>.</summary>
    public void PutTarget(TargetRow target, TargetRow? replaces)
    {
        int at = replaces is null ? -1 : Targets.IndexOf(replaces);
        if (at >= 0) Targets[at] = target; else Targets.Add(target);
        OnPropertyChanged(nameof(Summary));
    }

    public void RemoveTarget(TargetRow target)
    {
        Targets.Remove(target);
        OnPropertyChanged(nameof(Summary));
    }

    public RouteConfigDto ToDto() => new()
    {
        DataAreaId = DataAreaId?.Trim() ?? "",
        Input = Input?.Trim() ?? "",
        InputFormat = IsWord ? InputFormats.Word : InputFormats.Json,
        Output = Output?.Trim() ?? "",
        Mail = Mail,
        Enabled = Enabled,
        SharePoint = new CompanySharePointConfigDto
        {
            Enabled = SharePointEnabled,
            SiteUrl = SiteUrl?.Trim() ?? "",
            Library = Library?.Trim() ?? "",
            Folder = Folder?.Trim() ?? "",
            Fields = CollectColumns(),
        },
        Targets = Targets.Select(t => t.ToDto()).ToList(),
    };

    /// <summary>Collapse the rows to a map, skipping blank column names; last write wins on duplicates.</summary>
    private Dictionary<string, string> CollectColumns()
    {
        var map = new Dictionary<string, string>();
        foreach (var row in Columns)
        {
            var col = row.Column?.Trim();
            if (string.IsNullOrEmpty(col)) continue;
            map[col] = row.Template ?? "";
        }
        return map;
    }

    /// <summary>The SharePoint target as the form shows it now (unsaved edits included), for a sample file.</summary>
    public SharePointFieldImportTarget CurrentSharePoint() => new()
    {
        Enabled = SharePointEnabled,
        SiteUrl = SiteUrl?.Trim() ?? "",
        Library = Library?.Trim() ?? "",
        Folder = Folder?.Trim() ?? "",
        Fields = CollectColumns(),
    };

    /// <summary>Apply an imported target: given values replace the form's, given fields replace all column rows.</summary>
    public void ApplySharePoint(SharePointFieldImportTarget target)
    {
        if (target.Enabled is bool on) SharePointEnabled = on;
        if (target.SiteUrl is not null) SiteUrl = target.SiteUrl.Trim();
        if (target.Library is not null) Library = target.Library.Trim();
        if (target.Folder is not null) Folder = target.Folder.Trim();
        if (target.Fields is not null)
        {
            Columns.Clear();
            foreach (var (col, tpl) in SharePointFieldImport.CleanFields(target.Fields))
                Columns.Add(new ColumnRow(col, tpl));
            SelectedColumn = null;
        }
    }

    [RelayCommand]
    private void AddColumn()
    {
        var row = new ColumnRow("", "");
        Columns.Add(row);
        SelectedColumn = row;
    }

    [RelayCommand]
    private void RemoveColumn()
    {
        if (SelectedColumn is not null)
            Columns.Remove(SelectedColumn);
    }
}

/// <summary>One SharePoint column template row: internal column name and the template that fills it.</summary>
public sealed partial class ColumnRow : ObservableObject
{
    [ObservableProperty] private string _column = "";
    [ObservableProperty] private string _template = "";

    public ColumnRow() { }

    public ColumnRow(string column, string template)
    {
        _column = column;
        _template = template;
    }
}
