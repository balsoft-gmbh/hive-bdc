using Xunit;
using Balsoft.Hive.BDC.App.Services;

namespace Balsoft.Hive.BDC.Tests;

public class UpdateHandoffTests
{
    [Fact]
    public void SilentUpdateRunsSetupWithoutPromptsAndLogsNextToTheHiveBDCLog()
    {
        string log = UpdateHandoff.SetupLogPath(@"C:\ProgramData\HiveBDC\logs", "v2.2.2", new DateTime(2026, 9, 29, 17, 5, 9));
        Assert.Equal(@"C:\ProgramData\HiveBDC\logs\setup-v2.2.2-20260929-170509.log", log);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"" + log + "\"", UpdateHandoff.InstallerArguments(log, silent: true));
        // The service's log reader globs hive-bdc-*.log; a Setup log must never match it.
        Assert.DoesNotContain("hive-bdc-", Path.GetFileName(log));
    }

    [Fact]
    public void RetryShowsProgressAndErrors()
    {
        string args = UpdateHandoff.InstallerArguments(@"C:\Temp\a b\setup.log", silent: false);
        Assert.Equal("/SILENT /NORESTART /LOG=\"C:\\Temp\\a b\\setup.log\"", args);
        Assert.DoesNotContain("SUPPRESSMSGBOXES", args);
    }

    [Theory]
    [InlineData("2.2.2.0", false, PreviousUpdateState.Completed)]
    [InlineData("2.2.3.0", false, PreviousUpdateState.Completed)]
    [InlineData("2.2.1.0", true, PreviousUpdateState.StillRunning)]
    [InlineData("2.2.1.0", false, PreviousUpdateState.Failed)]
    public void PreviousAttemptIsJudgedByTheRunningVersion(string current, bool setupRunning, PreviousUpdateState expected)
    {
        var pending = new PendingUpdate("2.2.2", "2.2.1.0", @"C:\Temp\s.exe", @"C:\Temp\s.log", 42, DateTime.UtcNow);
        Assert.Equal(expected, UpdateHandoff.Evaluate(pending, current, _ => setupRunning));
        Assert.Equal(PreviousUpdateState.None, UpdateHandoff.Evaluate(null, current, _ => setupRunning));
    }

    [Fact]
    public void PendingUpdateSurvivesADashboardRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hd-handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pending = new PendingUpdate("2.2.2", "2.2.1.0", @"C:\Temp\s.exe", @"C:\Temp\s.log", 42, new DateTime(2026, 9, 29, 15, 0, 0, DateTimeKind.Utc));
            UpdateHandoff.Save(pending, dir);
            Assert.Equal(pending, UpdateHandoff.Load(dir));
            UpdateHandoff.Clear(dir);
            Assert.Null(UpdateHandoff.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LogTailNamesAMissingLogAndKeepsTheLastLines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hd-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Contains("did not start", UpdateHandoff.LogTail(Path.Combine(dir, "missing.log")));
            string log = Path.Combine(dir, "setup.log");
            File.WriteAllText(log, string.Join("\r\n", Enumerable.Range(1, 20).Select(i => "line " + i)) + "\r\n");
            string tail = UpdateHandoff.LogTail(log, 3);
            Assert.Equal(string.Join(Environment.NewLine, "line 18", "line 19", "line 20"), tail);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SetupThatStopsAtOnceIsReportedInsteadOfClosingTheDashboard()
    {
        if (!OperatingSystem.IsWindows()) return;
        // where.exe rejects Setup's switches and exits at once, like a Setup that is blocked or damaged.
        string cmd = Path.Combine(Environment.SystemDirectory, "where.exe");
        var ex = Assert.Throws<InvalidOperationException>(() => UpdateHandoff.Start(cmd, Path.Combine(Path.GetTempPath(), "none.log"), silent: true));
        Assert.Contains("stopped right after starting", ex.Message);
    }
}
