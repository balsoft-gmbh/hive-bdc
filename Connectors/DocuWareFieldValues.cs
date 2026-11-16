using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// Turns resolved field values into DocuWare index values, typed by the file cabinet's own field list. Every problem is
/// collected first, so a document is stored with all its fields or not at all.
/// </summary>
public static partial class DocuWareFieldValues
{
    private static readonly string[] DateFormats =
        { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyyMMdd", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss" };

    [GeneratedRegex(@"[;\r\n]+")]
    private static partial Regex KeywordSeparator();

    public static JsonArray Build(DocuWareCabinet cabinet, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables, List<string> problems)
    {
        var fields = new JsonArray();
        foreach (var (name, value) in values)
        {
            var field = cabinet.Field(name);
            if (field is null) { problems.Add($"Field {name} does not exist in file cabinet \"{cabinet.Name}\"."); continue; }
            if (field.Type == "Table") { problems.Add($"Field {name} is a table; fill it with {name}.<column> entries."); continue; }
            var item = Value(field, value, $"Field {name}", problems);
            if (item is not null) fields.Add(item);
        }

        foreach (var (name, rows) in tables)
        {
            var field = cabinet.Field(name);
            if (field is null) { problems.Add($"Table field {name} does not exist in file cabinet \"{cabinet.Name}\"."); continue; }
            if (field.Type != "Table") { problems.Add($"Field {name} is not a table field in file cabinet \"{cabinet.Name}\"."); continue; }
            var rowArray = new JsonArray();
            foreach (var row in rows)
            {
                var columns = new JsonArray();
                foreach (var (columnName, value) in row)
                {
                    var column = field.Columns.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                    if (column is null) { problems.Add($"Table field {name} has no column {columnName}."); continue; }
                    var item = Value(column, value, $"Column {name}.{columnName}", problems);
                    if (item is not null) columns.Add(item);
                }
                if (columns.Count > 0) rowArray.Add(new JsonObject { ["ColumnValue"] = columns });
            }
            if (rowArray.Count == 0) continue;
            fields.Add(new JsonObject
            {
                ["FieldName"] = field.Name,
                ["Item"] = new JsonObject { ["$type"] = "DocumentIndexFieldTable", ["Row"] = rowArray },
                ["ItemElementName"] = "Table",
            });
        }

        foreach (var required in cabinet.Fields.Where(f => f.Required))
            if (!fields.Any(f => string.Equals((string?)f!["FieldName"], required.Name, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"Field {required.Name} ({required.DisplayName}) is required in file cabinet \"{cabinet.Name}\" but gets no value.");
        return fields;
    }

    private static JsonObject? Value(DocuWareField field, string raw, string label, List<string> problems)
    {
        string text = raw.Trim();
        JsonNode? item;
        string element;
        switch (field.Type)
        {
            case "Numeric" or "Decimal":
                if (!TryParseNumber(text, out var number)) { problems.Add($"{label}: '{text}' is not a number."); return null; }
                bool whole = field.Type == "Numeric" && number == decimal.Truncate(number) && Math.Abs(number) <= int.MaxValue;
                item = whole ? JsonValue.Create((int)number) : JsonValue.Create(number);
                element = whole ? "Int" : "Decimal";
                break;
            case "Date":
                if (!TryParseDate(text, out var date)) { problems.Add($"{label}: '{text}' is not a date."); return null; }
                item = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                element = "Date";
                break;
            case "DateTime":
                if (!TryParseDate(text, out var moment)) { problems.Add($"{label}: '{text}' is not a date and time."); return null; }
                item = moment.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                element = "DateTime";
                break;
            case "Keyword":
                var keywords = KeywordSeparator().Split(text).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();
                if (keywords.Count == 0) return null;
                var tooLong = keywords.FirstOrDefault(k => field.Length > 0 && k.Length > field.Length);
                if (tooLong is not null) { problems.Add($"{label}: '{tooLong}' is longer than the {field.Length} characters the field holds."); return null; }
                item = new JsonObject { ["$type"] = "DocumentIndexFieldKeywords", ["Keyword"] = new JsonArray(keywords.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) };
                element = "Keywords";
                break;
            default:
                if (field.Length > 0 && text.Length > field.Length)
                { problems.Add($"{label}: the value has {text.Length} characters, the field holds {field.Length}."); return null; }
                item = text;
                element = field.Type == "Memo" ? "Memo" : "String";
                break;
        }
        return new JsonObject { ["FieldName"] = field.Name, ["Item"] = item, ["ItemElementName"] = element };
    }

    /// <summary>Accepts "1234.56", "1234,56", "1.234,56" and "1,234.56": the last separator is the decimal one.</summary>
    public static bool TryParseNumber(string text, out decimal number)
    {
        string s = text.Replace(" ", "").Replace(" ", "");
        int comma = s.LastIndexOf(','), dot = s.LastIndexOf('.');
        if (comma > dot) s = s.Replace(".", "").Replace(',', '.');
        else if (dot > comma && comma >= 0) s = s.Replace(",", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    public static bool TryParseDate(string text, out DateTime date)
        => DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
           || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
}