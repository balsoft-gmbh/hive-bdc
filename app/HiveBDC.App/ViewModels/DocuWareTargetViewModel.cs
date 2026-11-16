using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Balsoft.Hive.BDC.App.Services;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>One cabinet field in the target form: what it is, and the template that fills it (blank = not filled).</summary>
public sealed partial class DocuWareFieldRow : ObservableObject
{
    [ObservableProperty] private string _template = "";

    public string Field { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Required { get; init; }
}

/// <summary>
/// The "DocuWare target" dialog: pick a file cabinet by name, see its fields, say what fills each one, and optionally
/// which field holds the document number so a document is never stored twice.
/// </summary>
public sealed partial class DocuWareTargetViewModel : ObservableObject
{
    private readonly LoopbackClient _client;
    private readonly DocuWareRequestDto _connection;
    private readonly TargetRow? _existing;
    private bool _loading;

    public DocuWareTargetViewModel(LoopbackClient client, DocuWareRequestDto connection, TargetRow? existing)
    {
        _client = client;
        _connection = connection;
        _existing = existing;
        _matchField = existing?.Setting("MatchField") ?? "";
    }

    public ObservableCollection<DocuWareCabinetDto> Cabinets { get; } = new();
    public ObservableCollection<DocuWareFieldRow> Fields { get; } = new();
    public ObservableCollection<string> MatchFieldChoices { get; } = new();

    [ObservableProperty] private DocuWareCabinetDto? _selectedCabinet;
    [ObservableProperty] private string _matchField;
    [ObservableProperty] private string _statusMessage = "Signing in to DocuWare…";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;

    public bool CanSave => SelectedCabinet is not null && Fields.Any(f => !string.IsNullOrWhiteSpace(f.Template));

    public async Task LoadAsync()
    {
        IsBusy = true;
        _loading = true;
        try
        {
            var res = await _client.TestDocuWareAsync(_connection);
            if (res is not { Reachable: true, Data: { } data } || !data.Ok)
            {
                Show(res.Data?.Message ?? res.Error ?? "DocuWare could not be reached.", error: true);
                return;
            }
            Cabinets.Clear();
            foreach (var c in data.Cabinets ?? new()) Cabinets.Add(c);
            string wanted = _existing?.Setting("FileCabinet") ?? "";
            SelectedCabinet = Cabinets.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase))
                              ?? (Cabinets.Count == 1 ? Cabinets[0] : null);
            Show(SelectedCabinet is null ? "Choose the file cabinet the documents go to." : data.Message ?? "", error: false);
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
        if (SelectedCabinet is not null) await LoadFieldsAsync();
    }

    partial void OnSelectedCabinetChanged(DocuWareCabinetDto? value)
    {
        OnPropertyChanged(nameof(CanSave));
        if (!_loading && value is not null) _ = LoadFieldsAsync();
    }

    [RelayCommand]
    private async Task LoadFieldsAsync()
    {
        if (SelectedCabinet is null) return;
        IsBusy = true;
        try
        {
            var request = new DocuWareRequestDto
            {
                Id = _connection.Id, Url = _connection.Url, UserName = _connection.UserName, Secret = _connection.Secret,
                CabinetId = SelectedCabinet.Id,
            };
            var res = await _client.DocuWareFieldsAsync(request);
            if (res is not { Reachable: true, Data: { } data } || !data.Ok)
            {
                Show(res.Data?.Message ?? res.Error ?? "The fields could not be read.", error: true);
                return;
            }
            var previous = Fields.Where(f => !string.IsNullOrWhiteSpace(f.Template)).ToDictionary(f => f.Field, f => f.Template, StringComparer.OrdinalIgnoreCase);
            foreach (var (field, template) in _existing?.Fields ?? new()) previous.TryAdd(field, template);

            Fields.Clear();
            MatchFieldChoices.Clear();
            MatchFieldChoices.Add("");
            foreach (var f in data.Fields ?? new())
            {
                if (string.Equals(f.Type, "Table", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var c in f.Columns ?? new())
                        Fields.Add(Row($"{f.Name}.{c.Name}", $"{f.DisplayName} › {c.DisplayName}", c.Type, false, previous));
                    continue;
                }
                Fields.Add(Row(f.Name ?? "", f.DisplayName ?? f.Name ?? "", f.Type, f.Required, previous));
                if (f.Type is "Text" or "Numeric") MatchFieldChoices.Add(f.Name ?? "");
            }
            if (!MatchFieldChoices.Contains(MatchField)) MatchField = "";
            Show($"{data.Message} Fill the fields you want; required ones are marked. Blank fields stay empty in DocuWare.", error: false);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanSave)); }
    }

    private DocuWareFieldRow Row(string field, string label, string? type, bool required, Dictionary<string, string> previous)
    {
        var row = new DocuWareFieldRow
        {
            Field = field,
            Label = label + (required ? " (required)" : ""),
            Kind = KindLabel(type),
            Required = required,
            Template = previous.TryGetValue(field, out var t) ? t : "",
        };
        row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
        return row;
    }

    private static string KindLabel(string? type) => type switch
    {
        "Date" => "date",
        "DateTime" => "date and time",
        "Decimal" => "amount",
        "Numeric" => "number",
        "Keyword" => "several values, separate with ;",
        "Table" => "table",
        _ => "text",
    };

    private void Show(string message, bool error)
    {
        StatusMessage = message;
        StatusIsError = error;
    }

    public TargetRow ToTarget() => new()
    {
        Kind = TargetRow.DocuWareKind,
        Connection = _connection.Id ?? TargetRow.DocuWareKind,
        Enabled = _existing?.Enabled ?? true,
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FileCabinet"] = SelectedCabinet?.Id ?? "",
            ["FileCabinetName"] = SelectedCabinet?.Name ?? "",
            ["MatchField"] = MatchField ?? "",
        },
        Fields = Fields.Where(f => !string.IsNullOrWhiteSpace(f.Template)).ToDictionary(f => f.Field, f => f.Template.Trim()),
    };
}