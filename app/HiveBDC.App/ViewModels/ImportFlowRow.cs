using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// One company of an inbound document type (one entry of the file's Flows). The flow is kept as the JSON the service sent,
/// so properties this dashboard does not know survive a save; flows it cannot edit are sent back unchanged.
/// </summary>
public sealed partial class ImportFlowRow : ObservableObject
{
    /// <summary>Remove was clicked once; the card asks before the item goes.</summary>
    [ObservableProperty] private bool _confirmRemove;

    [ObservableProperty] private bool _enabled;

    public JsonObject Json { get; }

    public ImportFlowRow(JsonObject json)
    {
        Json = json;
        _enabled = Get(json, "Enabled")?.GetValue<bool>() ?? true;
    }

    public string Id => Text(Json, "Id");
    public string Name => Text(Json, "Name") is { Length: > 0 } n ? n : Id;
    public string SourceKind => Text(Get(Json, "Source") as JsonObject, "Kind");
    public bool IsDocuWare => SourceKind.Equals("docuware", StringComparison.OrdinalIgnoreCase);
    public string DocumentType => Text(Json, "DocumentType");
    public string DataAreaId => Text(Json, "DataAreaId");

    /// <summary>The document type it is listed under; an entry saved before 1.5.0 stands under its own name.</summary>
    public string TypeLabel => DocumentType.Length > 0 ? DocumentType : Name;

    public string CardTitle => DataAreaId.Length > 0 ? "Company " + DataAreaId : Name;
    public string CompanyBadge => DataAreaId.Length > 0 ? DataAreaId : "?";
    public string Folder => Text(Get((Get(Json, "Targets") as JsonArray)?.FirstOrDefault() as JsonObject, "Settings") as JsonObject, "Folder");

    /// <summary>Card lines, worded from the source's own setting labels by Settings.</summary>
    [ObservableProperty] private string _cardSource = "";
    [ObservableProperty] private string _cardReady = "";

    /// <summary>Puts the edited flow in place of this one's JSON.</summary>
    public void Replace(JsonObject json)
    {
        Json.Clear();
        foreach (var key in json.Select(p => p.Key).ToList())
        {
            var node = json[key];
            json.Remove(key);
            Json[key] = node;
        }
        Enabled = Get(Json, "Enabled")?.GetValue<bool>() ?? true;
        OnPropertyChanged(string.Empty);
    }

    public string Setting(string section, string name) => Text(Get(Get(Json, section) as JsonObject, "Settings") as JsonObject, name);

    partial void OnEnabledChanged(bool value) => Set(Json, "Enabled", value);

    public JsonObject ToJson() => (JsonObject)Json.DeepClone();

    internal static JsonNode? Get(JsonObject? o, string name)
        => o?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    internal static string Text(JsonObject? o, string name)
        => Get(o, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : Get(o, name)?.ToString() ?? "";

    private static void Set(JsonObject o, string name, JsonNode? value)
    {
        string key = o.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
        o[key] = value;
    }
}