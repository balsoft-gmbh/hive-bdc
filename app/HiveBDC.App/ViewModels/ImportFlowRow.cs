using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// One import flow on the Imports tab. The flow is kept as the JSON the service sent, so properties this dashboard does
/// not know survive a save; flows it cannot edit are sent back unchanged.
/// </summary>
public sealed partial class ImportFlowRow : ObservableObject
{
    [ObservableProperty] private bool _enabled;

    public JsonObject Json { get; }

    public ImportFlowRow(JsonObject json)
    {
        Json = json;
        _enabled = Get(json, "Enabled")?.GetValue<bool>() ?? true;
    }

    public string Id => Text(Json, "Id");
    public string Name => Text(Json, "Name") is { Length: > 0 } n ? n : Id;
    public bool IsDocuWare => Text(Get(Json, "Source") as JsonObject, "Kind").Equals("docuware", StringComparison.OrdinalIgnoreCase);

    public string Setting(string section, string name) => Text(Get(Get(Json, section) as JsonObject, "Settings") as JsonObject, name);

    public string Summary
    {
        get
        {
            if (!IsDocuWare) return "set up with a newer version of Hive BDC";
            string cabinet = Setting("Source", "FileCabinetName") is { Length: > 0 } c ? c : Setting("Source", "FileCabinet");
            string folder = Text(Get((Get(Json, "Targets") as JsonArray)?.FirstOrDefault() as JsonObject, "Settings") as JsonObject, "Folder");
            return $"DocuWare \"{cabinet}\" where {Setting("Source", "ReadyField")} = {Setting("Source", "ReadyValue")} → CSV in {folder}";
        }
    }

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