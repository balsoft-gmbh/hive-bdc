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
}
