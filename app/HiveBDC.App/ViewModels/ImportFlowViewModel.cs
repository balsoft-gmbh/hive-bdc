using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Balsoft.Hive.BDC.App.Services;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// The "Import from DocuWare" dialog: which file cabinet, which documents are ready, what to write into the field once
/// they are imported, the folder the ERP reads, and the CSV columns. Columns can be taken from a CSV the ERP already
/// reads, or one per DocuWare field.
/// </summary>
public sealed partial class ImportFlowViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> NumberFormats = new[]
    {
        "German: 1234,5 and 31.12.2026",
        "English (US): 1234.5 and 12/31/2026",
        "ISO: 1234.5 and 2026-12-31",
    };

    private readonly LoopbackClient _client;
    private readonly DocuWareRequestDto _connection;
    private readonly ImportFlowRow? _existing;
    private bool _loading;
    private List<DocuWareFieldDto> _fields = new();

    public ImportFlowViewModel(LoopbackClient client, DocuWareRequestDto connection, ImportFlowRow? existing)
    {
        _client = client;
        _connection = connection;
        _existing = existing;
        _name = existing?.Name ?? "Purchase requisitions from DocuWare";
        _readyField = existing?.Setting("Source", "ReadyField") ?? "";
        _readyValue = existing?.Setting("Source", "ReadyValue") ?? "";
        _doneValue = existing?.Setting("Source", "DoneValue") ?? "";
        _rowsFrom = existing?.Setting("Produce", "RowsFrom") ?? "";
        _delimiter = existing?.Setting("Produce", "Delimiter") is { Length: > 0 } d ? d : ";";
        _fileName = existing?.Setting("Produce", "FileName") is { Length: > 0 } f ? f : "{DWDOCID}.csv";
        string culture = existing?.Setting("Produce", "Culture") ?? "de-DE";
        _numberFormat = culture.StartsWith("de", StringComparison.OrdinalIgnoreCase) || culture.Length == 0 ? NumberFormats[0]
            : culture.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? NumberFormats[1] : NumberFormats[2];
        var target = (ImportFlowRow.Get(existing?.Json, "Targets") as JsonArray)?.FirstOrDefault() as JsonObject;
        var targetSettings = ImportFlowRow.Get(target, "Settings") as JsonObject;
        _folder = ImportFlowRow.Text(targetSettings, "Folder");
        _saveFile = existing is null || ImportFlowRow.Text(targetSettings, "SaveFile").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (ImportFlowRow.Get(ImportFlowRow.Get(existing?.Json, "Produce") as JsonObject, "Columns") is JsonArray columns)
            foreach (var c in columns.OfType<JsonObject>())
                Columns.Add(new ColumnRow(ImportFlowRow.Text(c, "Name"), ImportFlowRow.Text(c, "Value")));
        Columns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
    }

    public ObservableCollection<DocuWareCabinetDto> Cabinets { get; } = new();
    public ObservableCollection<string> ReadyFieldChoices { get; } = new();
    public ObservableCollection<string> TableChoices { get; } = new() { "" };
    public ObservableCollection<ColumnRow> Columns { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private string _name;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private DocuWareCabinetDto? _selectedCabinet;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private string _readyField;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private string _readyValue;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private string _doneValue;
    [ObservableProperty] private string _rowsFrom;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private string _folder;
    [ObservableProperty] private bool _saveFile;
    [ObservableProperty] private string _delimiter;
    [ObservableProperty] private string _fileName;
    [ObservableProperty] private string _numberFormat;
    [ObservableProperty] private ColumnRow? _selectedColumn;
    [ObservableProperty] private string _statusMessage = "Signing in to DocuWare…";
    [ObservableProperty] private bool _statusIsError;

    public bool CanSave => SelectedCabinet is not null && Name.Trim().Length > 0 && ReadyField.Trim().Length > 0
                           && ReadyValue.Trim().Length > 0 && DoneValue.Trim().Length > 0 && Folder.Trim().Length > 0 && Columns.Count > 0;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var res = await _client.TestDocuWareAsync(_connection);
            if (res is not { Reachable: true, Data: { } data } || !data.Ok)
            {
                Show(res.Data?.Message ?? res.Error ?? "DocuWare could not be reached.", error: true);
                return;
            }
            foreach (var c in data.Cabinets ?? new()) Cabinets.Add(c);
            string wanted = _existing?.Setting("Source", "FileCabinet") ?? "";
            SelectedCabinet = Cabinets.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase))
                              ?? (Cabinets.Count == 1 ? Cabinets[0] : null);
            Show(SelectedCabinet is null ? "Choose the file cabinet the documents come from." : data.Message ?? "", error: false);
        }
        finally { _loading = false; }
        if (SelectedCabinet is not null) await LoadFieldsAsync();
    }

    partial void OnSelectedCabinetChanged(DocuWareCabinetDto? value)
    {
        if (!_loading && value is not null) _ = LoadFieldsAsync();
    }

    private async Task LoadFieldsAsync()
    {
        if (SelectedCabinet is null) return;
        var res = await _client.DocuWareFieldsAsync(new DocuWareRequestDto
        {
            Id = _connection.Id, Url = _connection.Url, UserName = _connection.UserName, Secret = _connection.Secret,
            CabinetId = SelectedCabinet.Id,
        });
        if (res is not { Reachable: true, Data: { } data } || !data.Ok)
        {
            Show(res.Data?.Message ?? res.Error ?? "The fields could not be read.", error: true);
            return;
        }
        _fields = data.Fields ?? new();
        string ready = ReadyField, rows = RowsFrom;
        ReadyFieldChoices.Clear();
        foreach (var f in _fields.Where(f => f.Type is "Text" or "Keyword")) ReadyFieldChoices.Add(f.Name ?? "");
        TableChoices.Clear();
        TableChoices.Add("");
        foreach (var f in _fields.Where(f => f.Type == "Table")) TableChoices.Add(f.Name ?? "");
        ReadyField = ready;
        RowsFrom = TableChoices.Contains(rows) ? rows : "";
        Show($"{data.Message} Set which documents are ready and what the ERP's CSV holds.", error: false);
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
        if (SelectedColumn is not null) Columns.Remove(SelectedColumn);
    }

    /// <summary>One column per DocuWare field, and per column of the table the rows come from.</summary>
    [RelayCommand]
    private void FillFromFields()
    {
        var known = Columns.Select(c => c.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string template) { if (known.Add(name)) Columns.Add(new ColumnRow(name, template)); }
        Add("DWDOCID", "{DWDOCID}");
        foreach (var f in _fields.Where(f => f.Type != "Table")) Add(f.Name ?? "", "{" + f.Name + "}");
        var table = _fields.FirstOrDefault(f => f.Type == "Table" && string.Equals(f.Name, RowsFrom, StringComparison.OrdinalIgnoreCase));
        foreach (var c in table?.Columns ?? new()) Add(c.Name ?? "", "{" + table!.Name + "." + c.Name + "}");
        Show("Columns added for every DocuWare field. Rename them to what the ERP expects and remove the ones it does not read.", error: false);
    }

    /// <summary>Takes the column names from the first line of a CSV the ERP already reads.</summary>
    [RelayCommand]
    private void ColumnsFromCsv()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "A CSV file the ERP already reads", Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        string header;
        try { header = File.ReadLines(dlg.FileName).FirstOrDefault() ?? ""; }
        catch (IOException ex) { Show("The file could not be read: " + ex.Message, error: true); return; }
        string delimiter = header.Contains(';') ? ";" : header.Contains('\t') ? "\t" : ",";
        Delimiter = delimiter;
        var known = Columns.Select(c => c.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var name in header.Split(delimiter).Select(n => n.Trim().Trim('"').TrimStart('﻿')).Where(n => n.Length > 0))
            if (known.Add(name)) { Columns.Add(new ColumnRow(name, DefaultTemplate(name))); added++; }
        Show($"{added} column(s) taken from {Path.GetFileName(dlg.FileName)}. Say what fills each one.", error: false);
    }

    private string DefaultTemplate(string column)
    {
        if (column.Equals("DWDOCID", StringComparison.OrdinalIgnoreCase)) return "{DWDOCID}";
        var field = _fields.FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.OrdinalIgnoreCase));
        return field is null ? "" : "{" + field.Name + "}";
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "The folder the ERP reads the CSV files from" };
        if (Directory.Exists(Folder)) dlg.InitialDirectory = Folder;
        if (dlg.ShowDialog() == true) Folder = dlg.FolderName;
    }

    private void Show(string message, bool error)
    {
        StatusMessage = message;
        StatusIsError = error;
    }

    public JsonObject ToJson(string id)
    {
        var json = _existing?.ToJson() ?? new JsonObject();
        foreach (var key in json.Select(p => p.Key).Where(k => k.ToLowerInvariant() is "id" or "name" or "enabled" or "source" or "produce" or "targets").ToList())
            json.Remove(key);
        string culture = NumberFormat == NumberFormats[0] ? "de-DE" : NumberFormat == NumberFormats[1] ? "en-US" : "invariant";
        json["Id"] = id;
        json["Name"] = Name.Trim();
        json["Enabled"] = _existing?.Enabled ?? true;
        json["Source"] = new JsonObject
        {
            ["Kind"] = "docuware",
            ["Connection"] = _connection.Id,
            ["Settings"] = new JsonObject
            {
                ["FileCabinet"] = SelectedCabinet?.Id ?? "",
                ["FileCabinetName"] = SelectedCabinet?.Name ?? "",
                ["ReadyField"] = ReadyField.Trim(),
                ["ReadyValue"] = ReadyValue.Trim(),
                ["DoneValue"] = DoneValue.Trim(),
            },
        };
        json["Produce"] = new JsonObject
        {
            ["Kind"] = "csv",
            ["Settings"] = new JsonObject
            {
                ["Delimiter"] = string.IsNullOrEmpty(Delimiter) ? ";" : Delimiter,
                ["Culture"] = culture,
                ["RowsFrom"] = RowsFrom ?? "",
                ["FileName"] = string.IsNullOrWhiteSpace(FileName) ? "{DWDOCID}.csv" : FileName.Trim(),
            },
            ["Columns"] = new JsonArray(Columns.Where(c => !string.IsNullOrWhiteSpace(c.Column))
                .Select(c => (JsonNode)new JsonObject { ["Name"] = c.Column.Trim(), ["Value"] = c.Template ?? "" }).ToArray()),
        };
        json["Targets"] = new JsonArray(new JsonObject
        {
            ["Kind"] = "folder",
            ["Enabled"] = true,
            ["Settings"] = new JsonObject { ["Folder"] = Folder.Trim(), ["SaveFile"] = SaveFile ? "true" : "false" },
        });
        return json;
    }
}