namespace Balsoft.Hive.BDC.App.Services;

/// <summary>
/// One parsed log line: the raw text, a normalized severity used to colour it, and the type of its entry used by the
/// Type filter (a stack trace line has the type of the line above it).
/// </summary>
public sealed record LogLine(string Text, string Level, string Type = LogTypes.Info);

/// <summary>
/// The types of the technical log and the Type filter, the same as the web console's (web/src/lib/logtypes.ts);
/// tests/fixtures/log-types.json checks both sides.
/// </summary>
public static class LogTypes
{
    public const string All = "all";
    public const string Problems = "problems";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
    public const string Debug = "debug";
    public const string Dashboard = "dashboard";

    public sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public static IReadOnlyList<Choice> Choices { get; } = new[]
    {
        new Choice(All, "All types"),
        new Choice(Problems, "Errors and warnings"),
        new Choice(Error, "Errors"),
        new Choice(Warning, "Warnings"),
        new Choice(Info, "Info"),
        new Choice(Debug, "Debug"),
        new Choice(Dashboard, "Dashboard actions"),
    };

    public static string OfLevel(string level) => level switch
    {
        "ERR" => Error,
        "WRN" => Warning,
        "DBG" => Debug,
        "UI" => Dashboard,
        _ => Info,
    };

    public static bool Matches(string? choice, string type) => choice switch
    {
        Problems => type is Error or Warning,
        Error or Warning or Info or Debug or Dashboard => choice == type,
        _ => true,
    };

    public static string Count(int shown, int total) => $"{shown} of {total} {(total == 1 ? "line" : "lines")}";

    public static string NoLines(int total) => $"No lines of this type in the last {(total == 1 ? "line" : $"{total} lines")}.";
}

/// <summary>
/// Turns raw Serilog lines ("2026-.. [INF] message") into <see cref="LogLine"/>s tagged with a
/// severity the log viewer colours by: INF / DBG / WRN / ERR, plus UI for dashboard-side actions
/// (which carry the "UI · " marker) so a user can tell service activity from what they clicked.
/// </summary>
public static class LogParser
{
    /// <summary>The lines in file order, each with the type of its entry.</summary>
    public static List<LogLine> Parse(IEnumerable<string> lines)
    {
        string type = LogTypes.Info;
        var result = new List<LogLine>();
        foreach (var text in lines)
        {
            var line = FromText(text);
            if (StartsEntry(text)) type = LogTypes.OfLevel(line.Level);
            result.Add(line with { Type = type });
        }
        return result;
    }

    /// <summary>The lines of the chosen type (an id of <see cref="LogTypes.Choices"/>); an unknown choice keeps all.</summary>
    public static List<LogLine> Filter(IEnumerable<LogLine> lines, string? choice)
        => lines.Where(l => LogTypes.Matches(choice, l.Type)).ToList();

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
            result.AddRange(Parse(entries[i]));
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
