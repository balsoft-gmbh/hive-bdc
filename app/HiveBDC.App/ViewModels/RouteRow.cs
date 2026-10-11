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
    [ObservableProperty] private string _dataAreaId = "";
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _sharePointEnabled;
    [ObservableProperty] private string _siteUrl = "";
    [ObservableProperty] private string _library = "";
    [ObservableProperty] private string _folder = "";

    /// <summary>SharePoint column templates (internal column name -> template), editable rows.</summary>
    public ObservableCollection<ColumnRow> Columns { get; } = new();
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
            string sp = SharePointEnabled
                ? (string.IsNullOrWhiteSpace(Library) ? "SharePoint on" : "SharePoint: " + Library.Trim())
                : "no SharePoint";
            return tail + " · " + sp;
        }
    }

    partial void OnDataAreaIdChanged(string value) => OnPropertyChanged(nameof(Label));
    partial void OnInputChanged(string value) => OnPropertyChanged(nameof(Summary));
    partial void OnSharePointEnabledChanged(bool value) => OnPropertyChanged(nameof(Summary));
    partial void OnLibraryChanged(string value) => OnPropertyChanged(nameof(Summary));

    public RouteRow() { }

    public RouteRow(RouteConfigDto dto)
    {
        DataAreaId = dto.DataAreaId ?? "";
        Input = dto.Input ?? "";
        Output = dto.Output ?? "";
        var sp = dto.SharePoint;
        SharePointEnabled = sp?.Enabled ?? false;
        SiteUrl = sp?.SiteUrl ?? "";
        Library = sp?.Library ?? "";
        Folder = sp?.Folder ?? "";
        if (sp?.Fields is not null)
            foreach (var (col, tpl) in sp.Fields)
                Columns.Add(new ColumnRow(col, tpl));
    }

    public RouteConfigDto ToDto() => new()
    {
        DataAreaId = DataAreaId?.Trim() ?? "",
        Input = Input?.Trim() ?? "",
        Output = Output?.Trim() ?? "",
        SharePoint = new CompanySharePointConfigDto
        {
            Enabled = SharePointEnabled,
            SiteUrl = SiteUrl?.Trim() ?? "",
            Library = Library?.Trim() ?? "",
            Folder = Folder?.Trim() ?? "",
            Fields = CollectColumns(),
        },
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
