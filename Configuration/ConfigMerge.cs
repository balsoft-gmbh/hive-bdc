using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Balsoft.Hive.BDC.Configuration;

/// <summary>One setting that differs between two versions of the settings, named by its place.</summary>
public sealed record SettingChange(string Path, string Before, string After);

/// <summary>The merged settings, or the places both sides changed in different ways.</summary>
public sealed record SettingsMerge(JsonObject? Result, IReadOnlyList<string> Conflicts);

/// <summary>
/// Versions, change lists and the three-way merge of the product section (failure case 54): when two administrators
/// save, the second save names what each changed and keeps both when they did not change the same setting. Lists of
/// document types, companies, connections, flows and destinations are matched by their id, company or kind, so a
/// change to one company never collides with a change to another.
/// </summary>
public static class ConfigMerge
{
    /// <summary>Who saved last; kept beside the settings, never part of the version or a change.</summary>
    public const string LastSavedKey = "LastSaved";

    private const string Separator = " › ";
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase) { "Secret", "ClientSecret" };

    public static string Version(JsonObject? section)
    {
        var copy = Strip(section);
        if (copy is null || copy.Count == 0) return "none";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static SettingsMerge Merge3(JsonObject? start, JsonObject? theirs, JsonObject? mine)
    {
        var conflicts = new List<string>();
        var result = Merge(Strip(start), Strip(theirs), Strip(mine), "", "", conflicts) as JsonObject;
        return new SettingsMerge(result, conflicts);
    }

    public static IReadOnlyList<SettingChange> Changes(JsonObject? before, JsonObject? after)
    {
        var changes = new List<SettingChange>();
        Walk(Strip(before), Strip(after), "", "", changes);
        return changes;
    }

    /// <summary>
    /// What each side changed since the start, for the refusal. A setting the start lacked that both sides now hold with
    /// the same value is the shape every save writes (defaults filled in), not a change of either, so neither list shows it.
    /// </summary>
    public static (IReadOnlyList<SettingChange> Theirs, IReadOnlyList<SettingChange> Yours) ChangeLists(JsonObject? start, JsonObject? theirs, JsonObject? mine)
    {
        var their = Changes(start, theirs);
        var yours = Changes(start, mine);
        static bool Shared(SettingChange c, IReadOnlyList<SettingChange> other)
            => c.Before == "(none)" && other.Any(o => o.Path == c.Path && o.After == c.After);
        return (their.Where(c => !Shared(c, yours)).ToList(), yours.Where(c => !Shared(c, their)).ToList());
    }

    private static JsonObject? Strip(JsonObject? section)
    {
        if (section is null) return null;
        var copy = section.DeepClone().AsObject();
        copy.Remove(LastSavedKey);
        return copy;
    }

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    // at = the parent's place; a list element is named after its parent, so the list's own name never shows.
    private static JsonNode? Merge(JsonNode? start, JsonNode? theirs, JsonNode? mine, string at, string key, List<string> conflicts)
    {
        string path = key.Length == 0 ? at : Join(at, key);
        if (Same(mine, start)) return theirs?.DeepClone();
        if (Same(theirs, start)) return mine?.DeepClone();
        if (Same(theirs, mine)) return theirs?.DeepClone();

        if (start is JsonObject s && theirs is JsonObject t && mine is JsonObject m)
        {
            var result = new JsonObject();
            foreach (var name in t.Select(p => p.Key).Concat(m.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList())
            {
                var merged = Merge(s[name], t[name], m[name], path, name, conflicts);
                if (merged is not null) result[name] = merged;
            }
            return result;
        }

        if (start is JsonArray sa && theirs is JsonArray ta && mine is JsonArray ma
            && Keyed(key, sa, out var sKeys) && Keyed(key, ta, out var tKeys) && Keyed(key, ma, out var mKeys))
        {
            var result = new JsonArray();
            foreach (var id in tKeys.Keys.Concat(mKeys.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                sKeys.TryGetValue(id, out var se);
                tKeys.TryGetValue(id, out var te);
                mKeys.TryGetValue(id, out var me);
                var label = Label(key, (te ?? me ?? se)!);
                var merged = Merge(se, te, me, Join(at, label), "", conflicts);
                if (merged is not null) result.Add(merged);
            }
            return result;
        }

        conflicts.Add(path);
        return theirs?.DeepClone();
    }

    private static void Walk(JsonNode? before, JsonNode? after, string at, string key, List<SettingChange> changes)
    {
        if (Same(before, after)) return;
        string path = key.Length == 0 ? at : Join(at, key);
        if (before is JsonObject b && after is JsonObject a)
        {
            foreach (var name in b.Select(p => p.Key).Concat(a.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList())
                Walk(b[name], a[name], path, name, changes);
            return;
        }
        if (before is JsonArray ba && after is JsonArray aa && Keyed(key, ba, out var bKeys) && Keyed(key, aa, out var aKeys))
        {
            foreach (var id in bKeys.Keys.Concat(aKeys.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                bKeys.TryGetValue(id, out var be);
                aKeys.TryGetValue(id, out var ae);
                Walk(be, ae, Join(at, Label(key, (ae ?? be)!)), "", changes);
            }
            return;
        }
        changes.Add(new SettingChange(path, Show(before, after, key, true), Show(after, before, key, false)));
    }

    private static string Show(JsonNode? node, JsonNode? other, string key, bool isBefore)
    {
        if (node is null) return "(none)";
        if (SecretKeys.Contains(key)) return "(hidden)";
        if (node is JsonValue value) return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        if (other is null) return isBefore ? "present" : "added";
        return node.ToJsonString();
    }

    /// <summary>The elements of a list by their key, when every element is an object with a unique key.</summary>
    private static bool Keyed(string container, JsonArray array, out Dictionary<string, JsonNode> byKey)
    {
        byKey = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var element in array)
        {
            if (element is not JsonObject o) return false;
            string? id = container == "Targets"
                ? $"{(string?)o["Kind"]}|{(string?)o["Connection"]}|{o["Settings"]?.ToJsonString()}"
                : Text(o, "Id") ?? Text(o, "DataAreaId");
            if (id is null || !byKey.TryAdd(id, o)) return false;
        }
        return true;
    }

    private static string Label(string container, JsonNode element)
    {
        var o = (JsonObject)element;
        return container switch
        {
            "DocumentTypes" => Text(o, "Id") ?? "",
            "Companies" => "company " + Text(o, "DataAreaId"),
            "Connections" => "connection " + Text(o, "Id"),
            "Flows" => "flow " + Text(o, "Id"),
            "Targets" => string.Join(" ", new[] { Text(o, "Kind"), Text(o, "Connection") }
                .Concat((o["Settings"] as JsonObject)?.Select(p => p.Value?.ToString()) ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))),
            _ => $"{container} {Text(o, "Id") ?? Text(o, "DataAreaId")}",
        };
    }

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string Join(string path, string segment) => path.Length == 0 ? segment : path + Separator + segment;
}