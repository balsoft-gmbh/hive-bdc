using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Resolves the {property}, {property:date} and {property:yyyy} (any of y, M, d, - and .) placeholders of the configured
/// column templates and of a folder template against the top-level properties of the document JSON. Works on the
/// already-parsed <see cref="JsonElement"/> of the dropped file, so the JSON is read and parsed once per document, not
/// once per consumer.
/// </summary>
public static partial class SharePointFieldTemplate
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(?::(date|[yMd][yMd.\-]*))?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[""*:<>?|#%]")]
    private static partial Regex RefusedInFolder();

    [GeneratedRegex(@"\.{2,}")]
    private static partial Regex DotRuns();

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyyMMdd", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss",
    };

    public static Dictionary<string, string> Resolve(
        JsonElement root, IReadOnlyDictionary<string, string> mappings, Action<string>? warn = null)
    {
        var result = new Dictionary<string, string>();
        if (mappings.Count == 0) return result;

        var props = TopLevelStrings(root);
        foreach (var (column, template) in mappings)
        {
            string value = Placeholder().Replace(template, m => Value(m, props, $"SharePoint field '{column}'", warn)).Trim();
            if (value.Length > 0)
                result[column] = value;
        }
        return result;
    }

    /// <summary>
    /// A folder inside the library from a template such as {IssueDate:yyyy}/{IssueDate:MM} (failure case 75): a value never
    /// adds a folder level ("/" and "\" in it become "-"), every segment loses what SharePoint refuses and runs of dots,
    /// and empty segments are dropped, so the result always stays inside the library.
    /// </summary>
    public static string ResolvePath(JsonElement root, string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var props = TopLevelStrings(root);
        string filled = Placeholder().Replace(template, m => Value(m, props, null, null).Replace('/', '-').Replace('\\', '-'));
        var segments = filled.Replace('\\', '/').Split('/')
            .Select(part => DotRuns().Replace(RefusedInFolder().Replace(part, ""), ".").Trim(' ', '.'))
            .Where(part => part.Length > 0);
        return string.Join("/", segments);
    }

    public static string ResolvePath(string documentJson, string? template)
    {
        using var doc = JsonDocument.Parse(documentJson, AxDocument.DocumentOptions);
        return ResolvePath(doc.RootElement, template);
    }

    /// <summary>The top-level scalar properties of a document, for showing what a template would take.</summary>
    public static Dictionary<string, string> Fields(JsonElement root)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return fields;
        foreach (var p in root.EnumerateObject())
            if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                fields[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return fields;
    }

    private static string Value(Match m, Dictionary<string, string> props, string? where, Action<string>? warn)
    {
        if (!props.TryGetValue(m.Groups[1].Value, out var raw))
        {
            if (where is not null) warn?.Invoke($"{where}: document JSON has no property '{m.Groups[1].Value}'.");
            return "";
        }
        string format = m.Groups[2].Value;
        if (format.Length == 0 || raw.Length == 0) return raw;
        if (TryParseDate(raw, out var d))
            return d.ToString(format == "date" ? "yyyy-MM-dd" : format, CultureInfo.InvariantCulture);
        if (where is not null) warn?.Invoke($"{where}: '{raw}' is not a recognizable date; sending as-is.");
        return raw;
    }

    /// <summary>Convenience for callers that only hold the text (tests, tools).</summary>
    public static Dictionary<string, string> Resolve(
        string documentJson, IReadOnlyDictionary<string, string> mappings, Action<string>? warn = null)
    {
        using var doc = JsonDocument.Parse(documentJson, AxDocument.DocumentOptions);
        return Resolve(doc.RootElement, mappings, warn);
    }

    /// <summary>Top-level scalar properties as strings (numbers/booleans as their JSON text), case-insensitive keys.</summary>
    private static Dictionary<string, string> TopLevelStrings(JsonElement root)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return props;
        foreach (var p in root.EnumerateObject())
        {
            props[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                _ => "",
            };
        }
        return props;
    }

    private static bool TryParseDate(string s, out DateTime date)
    {
        if (DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
