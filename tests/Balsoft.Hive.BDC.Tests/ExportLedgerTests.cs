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

    [Fact]
    public void LedgerFromBeforeWordInputGainsTheSourceColumnAndOldRowsReadAsJson()
    {
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE exports (id INTEGER PRIMARY KEY AUTOINCREMENT, dedupe_key TEXT NOT NULL UNIQUE, document_id TEXT NOT NULL,
                    document_type TEXT NOT NULL, format TEXT NOT NULL, output_file TEXT NOT NULL, target_site TEXT,
                    size_bytes INTEGER NOT NULL, exported_at TEXT NOT NULL, data_area_id TEXT);
                INSERT INTO exports (dedupe_key, document_id, document_type, format, output_file, size_bytes, exported_at)
                VALUES ('k', '2026000001', 'SalesInvoice', 'xrechnung', 'a.xml', 1, '2026-11-01 10:00:00');
                """;
            cmd.ExecuteNonQuery();
        }
        var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, _db);
        ledger.Record("RE-1", "SalesInvoice", "xrechnung", "b.xml", null, 2, "1000", "word");
        var recent = ledger.Metrics(10).Recent;
        Assert.Equal("word", recent[0].Source);
        Assert.Equal("json", recent[1].Source);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
