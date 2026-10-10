using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class ExportLedgerTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "hivebdc-ledger-" + Guid.NewGuid() + ".db");

    [Fact]
    public void ReExportComesBackToTheTopWithoutCountingTwice()
    {
        var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, _db);
        ledger.Record("2026004721", "SalesInvoice", "zugferd", @"C:\out\2026004721.pdf", null, 10, "001");
        ledger.Record("2026004722", "SalesInvoice", "zugferd", @"C:\out\2026004722.pdf", null, 10, "001");
        ledger.Record("2026004721", "SalesInvoice", "zugferd", @"C:\out\2026004721.pdf", "https://x.sharepoint.com/sites/a", 12, "001");

        var m = ledger.Metrics(10);
        Assert.Equal(2, m.Recent.Count);
        Assert.Equal("2026004721", m.Recent[0].DocumentId);
        Assert.Equal("https://x.sharepoint.com/sites/a", m.Recent[0].TargetSite);
        Assert.Equal(12, m.Recent[0].SizeBytes);
        Assert.Equal(2, m.Total);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
