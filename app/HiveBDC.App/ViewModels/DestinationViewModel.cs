using System.Collections.ObjectModel;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>One field of the destination (a DocuWare index field, a column) and the template that fills it.</summary>
public sealed partial class DestinationFieldRow : ObservableObject
{
    public string Field { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    [ObservableProperty] private string _template = "";
}

/// <summary>
/// One destination of a company, of any kind the service reports: its settings with lists read from the system, then one
/// row per field of the system with the template that fills it. Settings and fields this dashboard does not know are kept.
/// </summary>
public sealed partial class DestinationViewModel : ObservableObject
{
    private readonly LoopbackClient _client;
    private readonly Func<SourceRequestDto, SourceRequestDto> _withDrafts;
    private bool _loading;

    public DestinationViewModel(LoopbackClient client, Func<SourceRequestDto, SourceRequestDto> withDrafts, TargetInfoDto kind, TargetRow row)
    {
        _client = client;
        _withDrafts = withDrafts;
        Kind = kind;
        Row = row;
        _enabled = row.Enabled;
        foreach (var spec in kind.Settings ?? new())
            Settings.Add(new SourceSettingField(spec, row.Setting(spec.Key ?? "")) { Changed = OnSettingChanged });
    }

    public TargetInfoDto Kind { get; }
    public TargetRow Row { get; }
    public string Title => Kind.Name ?? Kind.Kind ?? "";
    public string Description => Kind.Description ?? "";
    public bool HasFields => !string.IsNullOrEmpty(Kind.FieldsLookup);

    public ObservableCollection<SourceSettingField> Settings { get; } = new();
    public ObservableCollection<DestinationFieldRow> Fields { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    private SourceRequestDto Request(string lookup) => _withDrafts(new SourceRequestDto
    {
        Kind = Kind.Kind,
        Lookup = lookup,
        Connection = Row.Connection,
        Settings = Settings.ToDictionary(s => s.Key, s => s.Text, StringComparer.OrdinalIgnoreCase),
    });

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            foreach (var field in Settings.Where(f => f.HasLookup)) await LoadChoicesAsync(field);
            await LoadFieldsAsync();
        }
        finally { _loading = false; }
    }

    private async Task LoadChoicesAsync(SourceSettingField field)
    {
        bool ready = string.IsNullOrEmpty(field.Spec.DependsOn) || Settings.FirstOrDefault(s => s.Key == field.Spec.DependsOn)?.Text.Length > 0;
        if (!ready) { field.SetChoices(Array.Empty<SourceChoiceDto>()); field.Note = ""; return; }
        var res = await _client.TargetChoicesAsync(Request(field.Spec.Lookup!));
        if (res is { Reachable: true, Data: { Ok: true } data }) { field.SetChoices(data.Choices ?? new()); field.Note = ""; }
        else
        {
            field.SetChoices(Array.Empty<SourceChoiceDto>());
            field.Note = res.Data?.Message ?? res.Error ?? "The list could not be read; type the value.";
        }
    }

    /// <summary>One row per field of the system, keeping the templates already set (also for fields the system no longer lists).</summary>
    private async Task LoadFieldsAsync()
    {
        if (!HasFields) return;
        var kept = Fields.Where(f => f.Template.Length > 0).ToDictionary(f => f.Field, f => f.Template, StringComparer.OrdinalIgnoreCase);
        foreach (var (field, template) in Row.Fields) kept.TryAdd(field, template);
        var res = await _client.TargetChoicesAsync(Request(Kind.FieldsLookup!));
        Fields.Clear();
        foreach (var c in res.Data?.Choices ?? new())
        {
            string name = c.Value ?? "";
            Fields.Add(new DestinationFieldRow { Field = name, Label = c.Label ?? name, Kind = c.Detail ?? "", Template = kept.GetValueOrDefault(name, "") });
            kept.Remove(name);
        }
        foreach (var (field, template) in kept)
            Fields.Add(new DestinationFieldRow { Field = field, Label = field + " (not in the list now)", Kind = "", Template = template });
        if (res.Data is { Ok: false } failed || !res.Reachable)
            Show(res.Data?.Message ?? res.Error ?? "The fields could not be read.", error: true);
        else
            Show(Fields.Count == 0 ? "Fill the settings above; the fields follow." : "Fill the fields you want from the document; empty ones stay empty.", error: false);
    }

    private async void OnSettingChanged(SourceSettingField changed)
    {
        if (_loading) return;
        foreach (var dependent in Settings.Where(s => s.Spec.DependsOn == changed.Key)) await LoadChoicesAsync(dependent);
        if (changed.HasLookup) await LoadFieldsAsync();
    }

    private void Show(string message, bool error)
    {
        StatusMessage = message;
        StatusIsError = error;
    }

    /// <summary>The destination as the file holds it; settings this dashboard does not know are kept.</summary>
    public TargetRow ToTarget()
    {
        var settings = new Dictionary<string, string>(Row.Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var s in Settings)
        {
            settings[s.Key] = s.Text;
            if (s.ValueLabel != s.Value && !string.IsNullOrEmpty(s.ValueLabel)) settings[s.Key + "Name"] = s.ValueLabel;
        }
        var fields = HasFields
            ? Fields.Where(f => !string.IsNullOrWhiteSpace(f.Template)).ToDictionary(f => f.Field, f => f.Template.Trim())
            : new Dictionary<string, string>(Row.Fields);
        return new TargetRow { Kind = Row.Kind, Connection = Row.Connection, Enabled = Enabled, Settings = settings, Fields = fields, KindName = Title };
    }
}

/// <summary>A connection of any kind kept under Connections (DocuWare and the SharePoint app have their own pages).</summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    private readonly LoopbackClient _client;

    public ConnectionViewModel(LoopbackClient client, ConnectionKindDto kind, ConnectionConfigDto? existing, bool secretStored)
    {
        _client = client;
        Kind = kind;
        Id = existing?.Id ?? kind.Kind ?? "";
        SecretStored = secretStored;
        foreach (var spec in kind.Settings ?? new())
            Settings.Add(new SourceSettingField(spec, existing?.Settings?.GetValueOrDefault(spec.Key ?? "") ?? ""));
        _extra = existing?.Settings ?? new();
    }

    private readonly Dictionary<string, string> _extra;

    public ConnectionKindDto Kind { get; }
    public string Id { get; }
    public bool SecretStored { get; }
    public string Title => Kind.Name ?? Kind.Kind ?? "";
    public string SecretLabel => Kind.SecretLabel ?? "Secret";
    public string SecretHint => (SecretStored ? "One is stored. Leave this empty to keep it. " : "None stored yet. ") + (Kind.SecretHint ?? "");
    public ObservableCollection<SourceSettingField> Settings { get; } = new();

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    public Dictionary<string, string> SettingValues()
    {
        var values = new Dictionary<string, string>(_extra, StringComparer.OrdinalIgnoreCase);
        foreach (var s in Settings) values[s.Key] = s.Text;
        return values;
    }

    public async Task TestAsync(string typedSecret)
    {
        StatusMessage = $"Signing in to {Title}…";
        StatusIsError = false;
        var res = await _client.TestConnectionAsync(new ConnectionTestRequestDto
        {
            Id = Id, Kind = Kind.Kind, Settings = SettingValues(), Secret = typedSecret.Length == 0 ? null : typedSecret,
        });
        StatusIsError = res.Data?.Ok != true;
        StatusMessage = res.Data?.Message ?? res.Error ?? $"{Title} could not be reached.";
    }
}