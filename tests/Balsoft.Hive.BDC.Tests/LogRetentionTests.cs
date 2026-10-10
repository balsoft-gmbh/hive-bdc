using Balsoft.Hive.BDC.Hosting;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class LogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hivebdc-logs-" + Guid.NewGuid());

    private string Log(string name, int ageDays)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void LogsOlderThanThirtyDaysAreDeletedAndNewerOnesKept()
    {
        string old = Log("hive-bdc-20260801.log", 40);
        string oldSetup = Log("setup-v2.2.0-20260825-1010.log", 31);
        string recent = Log("hive-bdc-20260920.log", 29);
        string today = Log("hive-bdc-20261007.log", 0);
        string notALog = Log("hive.db", 90);

        var (deleted, problems) = LogSetup.DeleteOldLogs(_dir);

        Assert.Equal(2, deleted);
        Assert.Empty(problems);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldSetup));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(today));
        Assert.True(File.Exists(notALog));
    }

    [Fact]
    public void MissingFolderIsNotAnError()
    {
        var (deleted, problems) = LogSetup.DeleteOldLogs(Path.Combine(_dir, "missing"));
        Assert.Equal(0, deleted);
        Assert.Empty(problems);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
}
