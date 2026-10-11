namespace Balsoft.Hive.BDC.App.Services;

/// <summary>One parsed log line: the raw text plus a normalized severity used to colour it.</summary>
public sealed record LogLine(string Text, string Level);

/// <summary>
/// Turns raw Serilog lines ("2026-.. [INF] message") into <see cref="LogLine"/>s tagged with a
/// severity the log viewer colours by: INF / DBG / WRN / ERR, plus UI for dashboard-side actions
/// (which carry the "UI · " marker) so a user can tell service activity from what they clicked.
/// </summary>
public static class LogParser
{
    public static List<LogLine> Parse(IEnumerable<string> lines)
        => lines.Select(FromText).ToList();

    /// <summary>
    /// Like <see cref="Parse"/>, but the newest entry first, so the latest activity is always at the
    /// top of the log views. An entry keeps its own line order: the lines after a timestamped line
    /// (an exception and its stack trace) stay below it. Lines before the first timestamp (the tail
    /// cut into an entry) form the oldest entry.
    /// </summary>
    public static List<LogLine> ParseNewestFirst(IEnumerable<string> lines)
    {
        var entries = new List<List<string>>();
        foreach (var line in lines)
        {
            if (entries.Count == 0 || StartsEntry(line)) entries.Add(new List<string>());
            entries[^1].Add(line);
        }

        var result = new List<LogLine>();
        for (int i = entries.Count - 1; i >= 0; i--)
            result.AddRange(entries[i].Select(FromText));
        return result;
    }

    // Serilog template: "yyyy-MM-dd HH:mm:ss.fff [LVL] message".
    private static bool StartsEntry(string? line)
        => line is { Length: >= 19 }
           && char.IsDigit(line[0]) && char.IsDigit(line[3]) && line[4] == '-' && line[7] == '-'
           && line[10] == ' ' && line[13] == ':' && line[16] == ':';

    public static LogLine FromText(string text)
    {
        text ??= "";
        string level = "OTH";

        // Serilog template writes the level as "[INF]" / "[WRN]" / … right after the timestamp.
        int b = text.IndexOf('[');
        if (b >= 0 && b + 4 < text.Length && text[b + 4] == ']')
        {
            level = text.Substring(b + 1, 3).ToUpperInvariant() switch
            {
                "INF" => "INF",
                "DBG" => "DBG",
                "WRN" => "WRN",
                "ERR" => "ERR",
                "FTL" => "ERR",
                _ => "OTH",
            };
        }

        // Dashboard-side lines (AppLog) carry "UI · " — highlight them distinctly, but never
        // downgrade a warning/error to the UI colour.
        if (level is not ("WRN" or "ERR") && text.Contains("UI · "))
            level = "UI";

        return new LogLine(text, level);
    }
}
