using System.Text.Json;
using System.Text.Json.Nodes;

namespace Balsoft.Hive.BDC.Configuration;

/// <summary>
/// The one place that reads and rewrites appsettings.json on disk. Used by the loopback
/// /config endpoint (dashboard saves) and by <see cref="DocumentTypeRegistry"/> (a newly
/// discovered document type is appended). Writes are atomic (temp file + move-over) so the
/// host's reload watcher never sees a half-written file, and serialized through one lock so
/// the two writers cannot interleave.
/// </summary>
public static class SettingsFile
{
    public const string Section = "HiveBDC";

    /// <summary>Pre-2.0 section name (Hive eInvoice); read as a fallback and migrated on the first write.</summary>
    public const string LegacySection = "HiveDocFlow";

    private static readonly object Gate = new();

    /// <summary>Parse the current file (tolerant of // comments), or null if absent/unreadable.</summary>
    public static JsonObject? ReadRoot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var o = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonNode.Parse(File.ReadAllText(path), null, o) as JsonObject;
        }
        catch { return null; }
    }

    /// <summary>The product section of <paramref name="root"/>, falling back to the legacy one.</summary>
    public static JsonObject? SectionOf(JsonObject? root)
        => (root?[Section] ?? root?[LegacySection]) as JsonObject;

    /// <summary>Write a temp file next to the target, then move-replace it. Callers hold no lock; this does.</summary>
    public static void AtomicWrite(string path, JsonObject root)
    {
        lock (Gate)
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, $".appsettings.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            try { File.Move(tmp, path, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { /* best effort */ } throw; }
        }
    }

    /// <summary>
    /// Read-modify-write under the lock: <paramref name="mutate"/> receives the product
    /// section (created when absent) and returns whether anything changed; only then is the
    /// file rewritten. The legacy section, when that is what exists, is renamed on the way.
    /// </summary>
    public static bool Update(string path, Func<JsonObject, bool> mutate)
    {
        lock (Gate)
        {
            var root = ReadRoot(path);
            if (root is null && File.Exists(path))
                throw new InvalidOperationException($"{path} could not be read as JSON, so it was not changed.");
            root ??= new JsonObject();
            var section = SectionOf(root);
            if (section is null)
            {
                section = new JsonObject();
                root[Section] = section;
            }
            else if (root[Section] is null)
            {
                root.Remove(LegacySection);
                root[Section] = section;
            }

            if (!mutate(section)) return false;
            AtomicWrite(path, root);
            return true;
        }
    }

    /// <summary>Serialize one document type the way the dashboard writes it, so both writers produce the same shape.</summary>
    public static JsonObject DocumentTypeNode(DocumentTypeOptions t)
    {
        var companies = new JsonArray();
        foreach (var r in t.Companies) companies.Add(RouteNode(r));
        return new JsonObject
        {
            ["Id"] = t.Id,
            ["DisplayName"] = t.DisplayName,
            ["Party"] = t.Party.ToString(),
            ["Handler"] = t.Handler.ToString(),
            ["Enabled"] = t.Enabled,
            ["Mail"] = t.Mail,
            ["Discovered"] = t.Discovered,
            ["Companies"] = companies,
        };
    }

    /// <summary>One company entry of a document type (folders + SharePoint target).</summary>
    public static JsonObject RouteNode(DocumentRoute r)
    {
        var fields = new JsonObject();
        foreach (var (k, v) in r.SharePoint.Fields) fields[k] = v ?? "";
        var node = new JsonObject
        {
            ["DataAreaId"] = r.DataAreaId,
            ["Input"] = r.Input,
            ["InputFormat"] = r.InputFormat,
            ["Output"] = r.Output,
            ["SharePoint"] = new JsonObject
            {
                ["Enabled"] = r.SharePoint.Enabled,
                ["SiteUrl"] = r.SharePoint.SiteUrl,
                ["Library"] = r.SharePoint.Library,
                ["Folder"] = r.SharePoint.Folder,
                ["Fields"] = fields,
            },
        };
        // Written only once a company has its own switch; without it the entry follows the type, as before.
        if (r.Enabled is { } enabled) node["Enabled"] = enabled;
        if (r.Mail is { } mail) node["Mail"] = mail;
        if (r.Source is not null)
        {
            var sourceSettings = new JsonObject();
            foreach (var (k, v) in r.Source.Settings) sourceSettings[k] = v ?? "";
            node["Source"] = new JsonObject { ["Kind"] = r.Source.Kind, ["Connection"] = r.Source.Connection, ["Settings"] = sourceSettings };
        }
        // Written only when there is one, so an entry without further targets keeps the shape
        // every earlier version wrote and reads.
        if (r.Targets.Count > 0)
        {
            var targets = new JsonArray();
            foreach (var t in r.Targets) targets.Add(TargetNode(t));
            node["Targets"] = targets;
        }
        return node;
    }

    /// <summary>
    /// The further targets the settings file holds for one company entry (type, company, input
    /// folder), read from the product section as it is on disk. Empty when there are none.
    /// </summary>
    public static List<TargetOptions> StoredTargets(JsonObject? section, string typeId, string dataAreaId, string input)
    {
        static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        static string Folder(string p) => p.Trim().TrimEnd('\\', '/');

        var result = new List<TargetOptions>();
        if (section?["DocumentTypes"] is not JsonArray types) return result;
        var type = types.OfType<JsonObject>().FirstOrDefault(t => DocumentTypeCatalog.SameId(Text(t["Id"]), typeId));
        var entry = (type?["Companies"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c =>
            string.Equals(Text(c["DataAreaId"]).Trim(), dataAreaId.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Folder(Text(c["Input"])), Folder(input), StringComparison.OrdinalIgnoreCase));
        if (entry?["Targets"] is not JsonArray targets) return result;

        foreach (var node in targets.OfType<JsonObject>())
        {
            var target = new TargetOptions
            {
                Kind = Text(node["Kind"]),
                Connection = Text(node["Connection"]),
                Enabled = node["Enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on,
            };
            if (node["Settings"] is JsonObject settings)
                foreach (var (k, v) in settings) target.Settings[k] = Text(v);
            if (node["Fields"] is JsonObject fields)
                foreach (var (k, v) in fields) target.Fields[k] = Text(v);
            target.Normalize();
            if (target.Kind.Length > 0) result.Add(target);
        }
        return result;
    }

    /// <summary>The stored Source of one company entry, for a save from a dashboard that does not send it.</summary>
    public static SourceOptions? StoredSource(JsonObject? section, string typeId, string dataAreaId, string input)
    {
        static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        static string Folder(string p) => p.Trim().TrimEnd('\\', '/');
        if (section?["DocumentTypes"] is not JsonArray types) return null;
        var type = types.OfType<JsonObject>().FirstOrDefault(t => DocumentTypeCatalog.SameId(Text(t["Id"]), typeId));
        var entry = (type?["Companies"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c =>
            string.Equals(Text(c["DataAreaId"]).Trim(), dataAreaId.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Folder(Text(c["Input"])), Folder(input), StringComparison.OrdinalIgnoreCase));
        if (entry?["Source"] is not JsonObject node) return null;
        var source = new SourceOptions { Kind = Text(node["Kind"]), Connection = Text(node["Connection"]) };
        if (node["Settings"] is JsonObject settings)
            foreach (var (k, v) in settings) source.Settings[k] = Text(v);
        source.Normalize();
        return source.Kind.Length == 0 ? null : source;
    }

    public static JsonObject TargetNode(TargetOptions t)
    {
        var settings = new JsonObject();
        foreach (var (k, v) in t.Settings) settings[k] = v ?? "";
        var fields = new JsonObject();
        foreach (var (k, v) in t.Fields) fields[k] = v ?? "";
        return new JsonObject
        {
            ["Kind"] = t.Kind,
            ["Connection"] = t.Connection,
            ["Enabled"] = t.Enabled,
            ["Settings"] = settings,
            ["Fields"] = fields,
        };
    }
}
