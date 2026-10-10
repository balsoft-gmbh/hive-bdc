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
        return new JsonObject
        {
            ["DataAreaId"] = r.DataAreaId,
            ["Input"] = r.Input,
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
    }
}
