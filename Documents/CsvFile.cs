using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>One record read from a source: its id, single values and table rows, typed as the source delivered them.</summary>
public sealed record SourceRecord(
    string Id,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Tables);

/// <summary>
/// Builds the CSV an ERP reads: a header row of the column names, then one row per row of the RowsFrom table (or one
/// row when there is none), every value from its template. Fields are quoted per RFC 4180 when they hold the
/// delimiter, a quote or a line break; numbers and dates use the configured culture.
/// </summary>
public static partial class CsvFile
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(?:\.([A-Za-z0-9_]+))?\}")]
    private static partial Regex Placeholder();

    public static string Build(SourceRecord record, IReadOnlyList<ColumnOptions> columns, string? rowsFrom, string delimiter,
        CultureInfo culture, IReadOnlyDictionary<string, string> extra, List<string> problems)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(delimiter, columns.Select(c => Quote(c.Name, delimiter)))).Append("\r\n");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = new[] { new Dictionary<string, object?>() };
        if (!string.IsNullOrWhiteSpace(rowsFrom))
        {
            var table = record.Tables.FirstOrDefault(t => t.Key.Equals(rowsFrom, StringComparison.OrdinalIgnoreCase));
            if (table.Value is null || table.Value.Count == 0)
                problems.Add($"The table {rowsFrom} has no rows, so there is nothing to write.");
            else rows = table.Value;
        }

        foreach (var row in rows)
        {
            var cells = columns.Select(c => Quote(Resolve(c.Value, record, rowsFrom, row, culture, extra, c.Name, problems), delimiter));
            sb.Append(string.Join(delimiter, cells)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Fills {FIELD}, {TABLE.COLUMN} (the current row of RowsFrom), {DWDOCID} and the extra values such as {FilePath}.</summary>
    public static string Resolve(string template, SourceRecord record, string? rowsFrom, IReadOnlyDictionary<string, object?> row,
        CultureInfo culture, IReadOnlyDictionary<string, string> extra, string column, List<string>? problems)
        => Placeholder().Replace(template, m =>
        {
            string name = m.Groups[1].Value;
            if (m.Groups[2].Success)
            {
                if (rowsFrom is null || !name.Equals(rowsFrom, StringComparison.OrdinalIgnoreCase))
                {
                    problems?.Add($"Column {column}: {m.Value} names the table {name}, but rows come from {(string.IsNullOrWhiteSpace(rowsFrom) ? "no table" : rowsFrom)}.");
                    return "";
                }
                var cell = row.FirstOrDefault(c => c.Key.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                return Format(cell.Value, culture);
            }
            if (name.Equals("DWDOCID", StringComparison.OrdinalIgnoreCase)) return record.Id;
            var known = extra.FirstOrDefault(e => e.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (known.Key is not null) return known.Value;
            var value = record.Values.FirstOrDefault(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (value.Key is null) { problems?.Add($"Column {column}: the record has no field {name}."); return ""; }
            return Format(value.Value, culture);
        });

    public static string Format(object? value, CultureInfo culture) => value switch
    {
        null => "",
        decimal d => d.ToString("0.############", culture),
        double d => d.ToString("0.############", culture),
        int i => i.ToString(culture),
        long l => l.ToString(culture),
        DateTime t when t.TimeOfDay == TimeSpan.Zero => t.ToString(culture.DateTimeFormat.ShortDatePattern, culture),
        DateTime t => t.ToString(culture.DateTimeFormat.ShortDatePattern + " HH:mm:ss", culture),
        IEnumerable<string> list => string.Join(", ", list),
        _ => value.ToString() ?? "",
    };

    public static string Quote(string value, string delimiter)
        => value.Contains(delimiter) || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    /// <summary>Writes under a temporary name and renames, so a reader never sees half a file.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllBytes(tmp, content);
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch (IOException) { } throw; }
    }

    /// <summary>A file name made safe from a template result.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = new[] { '"', '*', ':', '<', '>', '?', '/', '\\', '|' };
        string clean = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        return clean.Length == 0 ? "record" : clean;
    }
}