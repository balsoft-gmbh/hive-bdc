using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// One setting of the document system, as the service describes it: a dropdown filled from the system itself, or a text
/// box when the setting has no list or the list could not be read.
/// </summary>
public sealed partial class SourceSettingField : ObservableObject
{
    public SourceSettingField(SourceSettingDto spec, string value)
    {
        Spec = spec;
        _value = value;
    }

    public SourceSettingDto Spec { get; }
    public string Key => Spec.Key ?? "";
    public string Label => Spec.Label ?? Key;
    public string Hint => Spec.Hint ?? "";
    public bool HasLookup => !string.IsNullOrEmpty(Spec.Lookup);

    public ObservableCollection<SourceChoiceDto> Choices { get; } = new();

    [ObservableProperty] private string _value;
    [ObservableProperty] private string _note = "";

    public bool ShowList => Choices.Count > 0;
    public bool ShowText => Choices.Count == 0;

    /// <summary>The shown name of the value (a file cabinet's name rather than its id).</summary>
    public string ValueLabel => Choices.FirstOrDefault(c => c.Value == Value)?.Label ?? Value ?? "";

    internal Action<SourceSettingField>? Changed;
    private bool _quiet;

    /// <summary>The value as text; a dropdown that loses its selection while its list is refilled must not blank it.</summary>
    public string Text => (Value ?? "").Trim();

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(ValueLabel));
        if (!_quiet) Changed?.Invoke(this);
    }

    public void SetChoices(IEnumerable<SourceChoiceDto> choices)
    {
        string keep = Value ?? "";
        _quiet = true;
        try
        {
            Choices.Clear();
            foreach (var c in choices) Choices.Add(c);
            if (Choices.Count > 0 && keep.Length > 0 && Choices.All(c => c.Value != keep))
                Choices.Add(new SourceChoiceDto(keep, keep + " (not in the list now)", ""));
            Value = keep;
        }
        finally { _quiet = false; }
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(ValueLabel));
    }
}

/// <summary>
/// One company of an inbound document type: where its records come from (any document system the service reports,
/// with that system's own settings), and the CSV the ERP reads plus the folder it goes to. Edits the flow's JSON, so
/// keys this dashboard does not know survive a save.
/// </summary>
public sealed partial class InboundFlowViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> NumberFormats = new[]
    {
        "German: 1234,5 and 31.12.2026",
        "English (US): 1234.5 and 12/31/2026",
        "ISO: 1234.5 and 2026-12-31",
    };

    private readonly LoopbackClient _client;
    private readonly Func<SourceRequestDto, SourceRequestDto> _withDrafts;
    private bool _loading;

    public InboundFlowViewModel(LoopbackClient client, Func<SourceRequestDto, SourceRequestDto> withDrafts, SourceInfoDto source, ImportFlowRow row)
    {
        _client = client;
        _withDrafts = withDrafts;
        Source = source;
        Row = row;
        _dataAreaId = row.DataAreaId;
        _enabled = row.Enabled;
        foreach (var spec in source.Settings ?? new())
        {
            var field = new SourceSettingField(spec, row.Setting("Source", spec.Key ?? "")) { Changed = OnSettingChanged };
            (spec.Step == "after" ? AfterSettings : WhichSettings).Add(field);
        }
        _rowsFrom = row.Setting("Produce", "RowsFrom");
        _delimiter = row.Setting("Produce", "Delimiter") is { Length: > 0 } d ? d : ";";
        _fileName = row.Setting("Produce", "FileName") is { Length: > 0 } f ? f : source.DefaultFileName ?? "{ID}.csv";
        string culture = row.Setting("Produce", "Culture");
        _numberFormat = culture.StartsWith("de", StringComparison.OrdinalIgnoreCase) || culture.Length == 0 ? NumberFormats[0]
            : culture.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? NumberFormats[1] : NumberFormats[2];
        var targetSettings = ImportFlowRow.Get((ImportFlowRow.Get(row.Json, "Targets") as JsonArray)?.FirstOrDefault() as JsonObject, "Settings") as JsonObject;
        _folder = ImportFlowRow.Text(targetSettings, "Folder");
        string saveFile = ImportFlowRow.Text(targetSettings, "SaveFile");
        _saveFile = saveFile.Length == 0 || saveFile.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (ImportFlowRow.Get(ImportFlowRow.Get(row.Json, "Produce") as JsonObject, "Columns") is JsonArray columns)
            foreach (var c in columns.OfType<JsonObject>())
                Columns.Add(new ColumnRow(ImportFlowRow.Text(c, "Name"), ImportFlowRow.Text(c, "Value")));
    }

    public SourceInfoDto Source { get; }
    public ImportFlowRow Row { get; }
    public string SourceName => Source.Name ?? Source.Kind ?? "";

    public ObservableCollection<SourceSettingField> WhichSettings { get; } = new();
    public ObservableCollection<SourceSettingField> AfterSettings { get; } = new();
    public ObservableCollection<SourceChoiceDto> Fields { get; } = new();
    public ObservableCollection<string> TableChoices { get; } = new() { "" };
    public ObservableCollection<ColumnRow> Columns { get; } = new();
    public ObservableCollection<string> Preview { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private string _dataAreaId;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _rowsFrom;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private bool _saveFile;
    [ObservableProperty] private string _delimiter;
    [ObservableProperty] private string _fileName;
    [ObservableProperty] private string _numberFormat;
    [ObservableProperty] private ColumnRow? _selectedColumn;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    public string Title => string.IsNullOrWhiteSpace(DataAreaId) ? "New company" : "Company " + DataAreaId.Trim();
    public bool HasTables => TableChoices.Count > 1;

    private IEnumerable<SourceSettingField> AllSettings => WhichSettings.Concat(AfterSettings);

    private SourceRequestDto Request(string lookup) => _withDrafts(new SourceRequestDto
    {
        Kind = Source.Kind,
        Lookup = lookup,
        Connection = Source.ConnectionKind == "docuware" ? "docuware" : "",
        Settings = AllSettings.ToDictionary(s => s.Key, s => s.Text, StringComparer.OrdinalIgnoreCase),
    });

    /// <summary>Fills every dropdown whose setting it depends on is set, and the list of fields for the columns.</summary>
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            foreach (var field in AllSettings.Where(f => f.HasLookup)) await LoadChoicesAsync(field);
            await LoadFieldsAsync();
        }
        finally { _loading = false; }
    }

    private bool Ready(SourceSettingField field)
        => string.IsNullOrEmpty(field.Spec.DependsOn)
           || AllSettings.FirstOrDefault(s => s.Key == field.Spec.DependsOn)?.Text.Length > 0;

    private async Task LoadChoicesAsync(SourceSettingField field)
    {
        if (!Ready(field)) { field.SetChoices(Array.Empty<SourceChoiceDto>()); field.Note = ""; return; }
        var res = await _client.SourceChoicesAsync(Request(field.Spec.Lookup!));
        if (res is { Reachable: true, Data: { Ok: true } data })
        {
            field.SetChoices(data.Choices ?? new());
            field.Note = "";
        }
        else
        {
            field.SetChoices(Array.Empty<SourceChoiceDto>());
            field.Note = res.Data?.Message ?? res.Error ?? "The list could not be read; type the value.";
        }
    }

    private async Task LoadFieldsAsync()
    {
        var res = await _client.SourceChoicesAsync(Request(Source.FieldsLookup ?? "fields"));
        Fields.Clear();
        foreach (var f in res.Data?.Choices ?? new()) Fields.Add(f);
        string rows = RowsFrom;
        TableChoices.Clear();
        TableChoices.Add("");
        foreach (var t in Fields.Where(f => f.Detail == "table")) TableChoices.Add(t.Value ?? "");
        RowsFrom = TableChoices.Contains(rows) ? rows : "";
        OnPropertyChanged(nameof(HasTables));
    }

    private async void OnSettingChanged(SourceSettingField changed)
    {
        if (_loading) return;
        foreach (var dependent in AllSettings.Where(s => s.Spec.DependsOn == changed.Key)) await LoadChoicesAsync(dependent);
        if (changed.HasLookup) await LoadFieldsAsync();
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

    /// <summary>One column per field of the document system, and per column of the table the rows come from.</summary>
    [RelayCommand]
    private void FillFromFields()
    {
        var known = Columns.Select(c => c.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string template) { if (name.Length > 0 && known.Add(name)) Columns.Add(new ColumnRow(name, template)); }
        foreach (var f in Fields.Where(f => f.Detail != "table" && !(f.Value ?? "").Contains('.'))) Add(f.Value ?? "", "{" + f.Value + "}");
        if (RowsFrom.Length > 0)
            foreach (var c in Fields.Where(f => (f.Value ?? "").StartsWith(RowsFrom + ".", StringComparison.OrdinalIgnoreCase)))
                Add(c.Value![(RowsFrom.Length + 1)..], "{" + c.Value + "}");
        Show(Fields.Count == 0
            ? $"{SourceName} listed no fields yet. Fill the settings on the left first."
            : $"Columns added for every field. Rename them to what the ERP expects and remove the ones it does not read.", error: Fields.Count == 0);
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
            if (known.Add(name))
            {
                var field = Fields.FirstOrDefault(f => string.Equals(f.Value, name, StringComparison.OrdinalIgnoreCase));
                Columns.Add(new ColumnRow(name, field is null ? "" : "{" + field.Value + "}"));
                added++;
            }
        Show($"{added} column(s) taken from {Path.GetFileName(dlg.FileName)}. Say what fills each one.", error: false);
    }

    /// <summary>Reads one ready record, so the columns can be checked against real values.</summary>
    [RelayCommand]
    private async Task PreviewAsync()
    {
        Preview.Clear();
        Show($"Reading one ready {Source.RecordNoun ?? "record"} from {SourceName}…", error: false);
        var res = await _client.SourceSampleAsync(Request(""));
        if (res is not { Reachable: true, Data: { Ok: true } data })
        {
            Show(res.Data?.Message ?? res.Error ?? $"{SourceName} could not be read.", error: true);
            return;
        }
        foreach (var v in data.Values ?? new()) Preview.Add($"{v.Name}: {v.Value}");
        Show($"{data.Message} Its values are listed below the columns.", error: false);
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

    /// <summary>The flow as the settings file holds it; keys this dashboard does not know are kept.</summary>
    public JsonObject ToJson(string id, string documentType)
    {
        var json = Row.ToJson();
        foreach (var key in json.Select(p => p.Key)
                     .Where(k => k.ToLowerInvariant() is "id" or "name" or "enabled" or "documenttype" or "dataareaid" or "source" or "produce" or "targets").ToList())
            json.Remove(key);
        string culture = NumberFormat == NumberFormats[0] ? "de-DE" : NumberFormat == NumberFormats[1] ? "en-US" : "invariant";
        string company = DataAreaId.Trim();
        var settings = new JsonObject();
        foreach (var s in AllSettings)
        {
            settings[s.Key] = s.Text;
            if (s.ValueLabel != s.Value && !string.IsNullOrEmpty(s.ValueLabel)) settings[s.Key + "Name"] = s.ValueLabel;
        }
        json["Id"] = id;
        json["Name"] = company.Length > 0 ? $"{documentType} ({company})" : documentType;
        json["Enabled"] = Enabled;
        json["DocumentType"] = documentType;
        json["DataAreaId"] = company;
        json["Source"] = new JsonObject
        {
            ["Kind"] = Source.Kind,
            ["Connection"] = Source.ConnectionKind == "docuware" ? "docuware" : "",
            ["Settings"] = settings,
        };
        json["Produce"] = new JsonObject
        {
            ["Kind"] = "csv",
            ["Settings"] = new JsonObject
            {
                ["Delimiter"] = string.IsNullOrEmpty(Delimiter) ? ";" : Delimiter,
                ["Culture"] = culture,
                ["RowsFrom"] = RowsFrom ?? "",
                ["FileName"] = string.IsNullOrWhiteSpace(FileName) ? Source.DefaultFileName ?? "{ID}.csv" : FileName.Trim(),
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