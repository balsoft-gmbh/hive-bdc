using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Resolves the {property} / {property:date} placeholders of the configured column
/// templates against the top-level properties of the document JSON. Works on the
/// already-parsed <see cref="JsonElement"/> of the dropped file, so the JSON is read and
/// parsed once per document, not once per consumer.
/// </summary>
public static partial class SharePointFieldTemplate
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(?::(date))?\}")]
    private static partial Regex Placeholder();

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
            string value = Placeholder().Replace(template, m =>
            {
                if (!props.TryGetValue(m.Groups[1].Value, out var raw))
                {
                    warn?.Invoke($"SharePoint field '{column}': document JSON has no property '{m.Groups[1].Value}'.");
                    return "";
                }
                if (m.Groups[2].Value == "date" && raw.Length > 0)
                {
                    if (TryParseDate(raw, out var d)) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    warn?.Invoke($"SharePoint field '{column}': '{raw}' is not a recognizable date; sending as-is.");
                }
                return raw;
            }).Trim();

            if (value.Length > 0)
                result[column] = value;
        }
        return result;
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
