using System.Text.Json;
using Balsoft.Hive.BDC.App.Services;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class LogParserTests
{
    [Fact]
    public void Newest_entry_comes_first_and_a_stack_trace_stays_below_its_line()
    {
        var lines = new[]
        {
            "   at Old.Frame()",                                         // tail cut into an older entry
            "2026-10-12 08:00:00.000 [INF] Service started",
            "2026-10-12 08:01:00.000 [ERR] Upload failed",
            "System.IO.IOException: disk full",
            "   at Upload.Run()",
            "2026-10-12 08:02:00.000 [WRN] Retrying",
        };

        var parsed = LogParser.ParseNewestFirst(lines);

        Assert.Equal(new[]
        {
            "2026-10-12 08:02:00.000 [WRN] Retrying",
            "2026-10-12 08:01:00.000 [ERR] Upload failed",
            "System.IO.IOException: disk full",
            "   at Upload.Run()",
            "2026-10-12 08:00:00.000 [INF] Service started",
            "   at Old.Frame()",
        }, parsed.Select(l => l.Text));
        Assert.Equal("WRN", parsed[0].Level);
        Assert.Equal("ERR", parsed[1].Level);
    }

    [Fact]
    public void A_single_note_line_is_kept()
    {
        var parsed = LogParser.ParseNewestFirst(new[] { "(no log lines yet)" });
        Assert.Equal("(no log lines yet)", Assert.Single(parsed).Text);
    }

    // The sample lines the web console is tested with too (web/tests/logtypes.test.ts), so both sides filter alike.
    private static readonly JsonElement Fixture = LoadFixture();

    private static JsonElement LoadFixture()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "HiveBDC.sln"))) dir = Path.GetDirectoryName(dir)!;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tests", "fixtures", "log-types.json"))).RootElement;
    }

    private static string[] FixtureTexts() => Fixture.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray();

    [Fact]
    public void Every_line_has_the_type_of_its_entry_as_in_the_console()
    {
        var expected = Fixture.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("type").GetString()).ToArray();

        Assert.Equal(expected, LogParser.Parse(FixtureTexts()).Select(l => l.Type));
    }

    [Fact]
    public void The_type_choices_are_the_ones_the_console_offers_in_the_same_order()
    {
        var expected = Fixture.GetProperty("choices").EnumerateArray().Select(c => (c.GetProperty("id").GetString(), c.GetProperty("label").GetString()));

        Assert.Equal(expected, LogTypes.Choices.Select(c => ((string?)c.Id, (string?)c.Label)));
    }

    [Fact]
    public void Each_choice_shows_its_lines_and_keeps_a_stack_trace_with_its_error()
    {
        string[] texts = FixtureTexts();
        foreach (var choice in Fixture.GetProperty("choices").EnumerateArray())
        {
            var expected = choice.GetProperty("lines").EnumerateArray().Select(i => texts[i.GetInt32()]);
            Assert.Equal(expected, LogParser.Filter(LogParser.Parse(texts), choice.GetProperty("id").GetString()!).Select(l => l.Text));
        }
    }

    [Fact]
    public void Newest_first_errors_keep_their_stack_trace_below_them()
    {
        var errors = LogParser.Filter(LogParser.ParseNewestFirst(FixtureTexts()), LogTypes.Error);

        Assert.Equal(new[]
        {
            "2026-11-21 08:04:00.000 [FTL] Service stopped",
            "2026-11-21 08:01:00.000 [ERR] Upload failed for CIV-000171",
            "System.IO.IOException: disk full",
            "   at Upload.Run()",
        }, errors.Select(l => l.Text));
        Assert.Equal("ERR", errors[0].Level);
    }

    [Fact]
    public void An_unknown_choice_shows_everything()
        => Assert.Equal(FixtureTexts().Length, LogParser.Filter(LogParser.Parse(FixtureTexts()), "nonsense").Count);

    [Fact]
    public void The_count_and_the_empty_message_name_the_lines_read()
    {
        Assert.Equal("6 of 170 lines", LogTypes.Count(6, 170));
        Assert.Equal("1 of 1 line", LogTypes.Count(1, 1));
        Assert.Equal("No lines of this type in the last 170 lines.", LogTypes.NoLines(170));
        Assert.Equal("No lines of this type in the last line.", LogTypes.NoLines(1));
    }
}