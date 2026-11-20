using System.Text.Json.Nodes;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// The destination of an inbound company as the settings file holds it. No WPF in here, so the test project links it:
/// a kind this app does not know is written back unchanged (a newer service set it up), a folder keeps its two
/// settings, any other kind gets its connection, the file switch and its own fields.
/// </summary>
public static class InboundTargetJson
{
    public static JsonObject Build(string kind, bool known, JsonObject? original, string folder, bool saveFile, string connection,
        IEnumerable<KeyValuePair<string, string>> fields)
    {
        if (!known && original is not null) return (JsonObject)original.DeepClone();
        string save = saveFile ? "true" : "false";
        if (kind == "folder")
            return new JsonObject { ["Kind"] = "folder", ["Enabled"] = true, ["Settings"] = new JsonObject { ["Folder"] = folder.Trim(), ["SaveFile"] = save } };
        var settings = new JsonObject { ["SaveFile"] = save };
        foreach (var (key, value) in fields) settings[key] = value.Trim();
        return new JsonObject { ["Kind"] = kind, ["Connection"] = connection.Trim(), ["Enabled"] = true, ["Settings"] = settings };
    }
}