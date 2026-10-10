using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>One recorded export, as returned by <see cref="ExportLedger.Metrics"/>.</summary>
public sealed record ExportRecord(
    string DocumentId, string DocumentType, string Format, string OutputFile, string? TargetSite, long SizeBytes,
    string ExportedAt, string? DataAreaId);

/// <summary>Exports per document type for one period, as returned by <see cref="ExportLedger.Metrics"/>.</summary>
public sealed record TypeCount(string DocumentType, int Count);

/// <summary>
/// Local metering for dispatched documents. Backed by an embedded SQLite database
/// (WAL) at %ProgramData%\HiveBDC\hive.db — no separate install, bundled into
/// the single-file exe. Purely additive: metering never affects conversion output,
/// so every write is best-effort and swallows its own errors.
/// </summary>
public sealed class ExportLedger
{
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string _connectionString;
    private readonly ILogger _log;

    private ExportLedger(string dbPath, ILogger log)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _log = log;
    }

    /// <summary>Default database path: %ProgramData%\HiveBDC\hive.db.</summary>
    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HiveBDC", "hive.db");

    /// <summary>Open (creating the file + schema if absent). Never throws — a broken ledger just no-ops.</summary>
    public static ExportLedger CreateIfMissing(ILogger log, string? dbPath = null)
    {
        string path = string.IsNullOrWhiteSpace(dbPath) ? DefaultDbPath : dbPath!;
        var ledger = new ExportLedger(path, log);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var conn = ledger.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA auto_vacuum=INCREMENTAL;
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS exports (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    dedupe_key    TEXT NOT NULL UNIQUE,
                    document_id   TEXT NOT NULL,
                    document_type TEXT NOT NULL,
                    format        TEXT NOT NULL,
                    output_file   TEXT NOT NULL,
                    target_site   TEXT,
                    size_bytes    INTEGER NOT NULL,
                    exported_at   TEXT NOT NULL,
                    data_area_id  TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_exports_exported_at ON exports(exported_at);
                CREATE INDEX IF NOT EXISTS ix_exports_type ON exports(document_type, exported_at);
                -- Lifetime counters survive detail-row pruning (see Prune), so the usage
                -- 'total exported' stays accurate even after old rows are dropped.
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value INTEGER NOT NULL);
                """;
            cmd.ExecuteNonQuery();

            // The 2.0 schema lives in a new %ProgramData%\HiveBDC folder, so no 1.x rows
            // are migrated; the guard stays for future columns.
            EnsureColumn(conn, "exports", "data_area_id", "TEXT");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Export ledger could not be initialized at {Path}; metering is disabled this run.", path);
        }
        return ledger;
    }

    /// <summary>
    /// Record one successful export. One row per (dataAreaId, documentType, documentId, format,
    /// output file name): the same id in two AX companies (or two document types) is counted
    /// once each, and the lifetime counter counts each distinct export once. A document that is
    /// processed AGAIN (dropped back in, re-exported from AX) refreshes its row's time, file,
    /// size and target, so it comes back to the top of the dashboard's recent list instead of
    /// staying hidden under its first export. Best-effort: a failure here is logged but never
    /// propagated to the pipeline.
    /// </summary>
    public void Record(string documentId, string documentType, string format, string outputFile, string? targetSite,
        long sizeBytes, string? dataAreaId = null)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            string key = $"{dataAreaId ?? ""}|{documentType}|{documentId}|{format}|{Path.GetFileName(outputFile)}";

            bool isNew;
            using (var exists = conn.CreateCommand())
            {
                exists.Transaction = tx;
                exists.CommandText = "SELECT 1 FROM exports WHERE dedupe_key = $key";
                exists.Parameters.AddWithValue("$key", key);
                isNew = exists.ExecuteScalar() is null;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // A refreshed row is deleted and inserted again rather than updated in place, so it
            // gets a new id and the newest-first ordering (by id) stays a plain index walk.
            cmd.CommandText = """
                DELETE FROM exports WHERE dedupe_key = $key;
                INSERT INTO exports
                    (dedupe_key, document_id, document_type, format, output_file, target_site, size_bytes, exported_at, data_area_id)
                VALUES ($key, $id, $type, $format, $out, $site, $size, $at, $area);
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$id", documentId);
            cmd.Parameters.AddWithValue("$type", documentType);
            cmd.Parameters.AddWithValue("$format", format);
            cmd.Parameters.AddWithValue("$out", outputFile);
            cmd.Parameters.AddWithValue("$site", (object?)targetSite ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(StampFormat));
            cmd.Parameters.AddWithValue("$area", (object?)dataAreaId ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            if (isNew)
            {
                using var inc = conn.CreateCommand();
                inc.Transaction = tx;
                inc.CommandText =
                    "INSERT INTO meta(key,value) VALUES('lifetime_exports',1) " +
                    "ON CONFLICT(key) DO UPDATE SET value=value+1";
                inc.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Export ledger write failed for {Id} ({Type}); delivery is unaffected.", documentId, documentType);
        }
    }

    /// <summary>Everything the dashboard's metrics card needs, read on ONE connection.</summary>
    public sealed record MetricsSnapshot(int Today, int Month, long Total, IReadOnlyList<TypeCount> MonthByType, IReadOnlyList<ExportRecord> Recent);

    /// <summary>
    /// The dashboard polls /metrics every few seconds; five separate connections per poll
    /// (today, month, total, by type, recent) was needless. One connection, five statements.
    /// </summary>
    public MetricsSnapshot Metrics(int recent)
    {
        try
        {
            using var conn = Open();
            long Scalar(string sql, string? start = null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (start is not null) cmd.Parameters.AddWithValue("$start", start);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            int today = (int)Scalar("SELECT COUNT(*) FROM exports WHERE exported_at >= $start", DateTime.Today.ToString(StampFormat));
            int month = (int)Scalar("SELECT COUNT(*) FROM exports WHERE exported_at >= $start", StartOfMonth.ToString(StampFormat));
            long total = Math.Max(Scalar("SELECT COALESCE((SELECT value FROM meta WHERE key='lifetime_exports'), 0)"),
                                  Scalar("SELECT COUNT(*) FROM exports"));
            return new MetricsSnapshot(today, month, total, ReadByType(conn, StartOfMonth), ReadRecent(conn, recent));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Export ledger metrics read failed.");
            return new MetricsSnapshot(0, 0, 0, Array.Empty<TypeCount>(), Array.Empty<ExportRecord>());
        }
    }

    public static DateTime StartOfMonth => new(DateTime.Now.Year, DateTime.Now.Month, 1);

    /// <summary>Exports per document type since <paramref name="start"/>, most active first.</summary>
    private static List<TypeCount> ReadByType(SqliteConnection conn, DateTime start)
    {
        var list = new List<TypeCount>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT document_type, COUNT(*) FROM exports
            WHERE exported_at >= $start GROUP BY document_type ORDER BY COUNT(*) DESC, document_type
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString(StampFormat));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new TypeCount(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>Default retention for the detailed export rows (days).</summary>
    public const int DefaultRetentionDays = 60;

    private DateTime _lastPruneUtc = DateTime.MinValue;

    /// <summary>
    /// Delete detailed export rows older than the retention window and reclaim the freed
    /// pages, so hive.db stays bounded (~retention worth of rows). Best-effort. The
    /// lifetime total counter is untouched, so usage totals are preserved.
    /// </summary>
    public int Prune(int retentionDays = DefaultRetentionDays)
    {
        try
        {
            using var conn = Open();
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM exports WHERE exported_at < $cutoff";
            del.Parameters.AddWithValue("$cutoff",
                DateTime.Now.AddDays(-Math.Abs(retentionDays)).ToString(StampFormat));
            int deleted = del.ExecuteNonQuery();
            if (deleted > 0)
            {
                using var vac = conn.CreateCommand();
                vac.CommandText = "PRAGMA incremental_vacuum;";
                vac.ExecuteNonQuery();
                _log.LogInformation("Export ledger pruned {Deleted} row(s) older than {Days} days.",
                    deleted, retentionDays);
            }
            return deleted;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Export ledger prune failed; conversion is unaffected.");
            return 0;
        }
    }

    /// <summary>Prune at most once per 24h — cheap to call every polling cycle.</summary>
    public void PruneIfDue(int retentionDays = DefaultRetentionDays)
    {
        if (DateTime.UtcNow - _lastPruneUtc < TimeSpan.FromHours(24)) return;
        _lastPruneUtc = DateTime.UtcNow;
        Prune(retentionDays);
    }

    /// <summary>The most recent <paramref name="n"/> exports, newest first.</summary>
    private static List<ExportRecord> ReadRecent(SqliteConnection conn, int n)
    {
        var list = new List<ExportRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT document_id, document_type, format, output_file, target_site, size_bytes, exported_at, data_area_id
            FROM exports ORDER BY id DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", Math.Clamp(n, 1, 5000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ExportRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Add <paramref name="column"/> to <paramref name="table"/> if it is not already there.
    /// Idempotent: PRAGMA table_info is consulted first so ALTER TABLE runs at most once.
    /// Best-effort — a failure just leaves the schema as-is (Record then no-ops on that column).
    /// </summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string sqlType)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var r = check.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;   // already present
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType}";
        alter.ExecuteNonQuery();
    }
}
