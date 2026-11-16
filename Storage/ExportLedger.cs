using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>One recorded export, as returned by <see cref="ExportLedger.Metrics"/>.</summary>
public sealed record ExportRecord(
    string DocumentId, string DocumentType, string Format, string OutputFile, string? TargetSite, long SizeBytes,
    string ExportedAt, string? DataAreaId, string Source = "json");

/// <summary>Exports per document type for one period, as returned by <see cref="ExportLedger.Metrics"/>.</summary>
public sealed record TypeCount(string DocumentType, int Count);

/// <summary>
/// Local metering for dispatched documents. Backed by an embedded SQLite database
/// (WAL) at %ProgramData%\HiveBDC\hive.db — no separate install, bundled into
/// the single-file exe. Purely additive: metering never affects conversion output,
/// so every write is best-effort and swallows its own errors.
/// </summary>
public sealed class ExportLedger : IStateStore
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
                -- One row per document and target: where it went and the id the target gave it.
                CREATE TABLE IF NOT EXISTS deliveries (
                    document_key TEXT NOT NULL,
                    target_key   TEXT NOT NULL,
                    kind         TEXT NOT NULL,
                    location     TEXT,
                    remote_id    TEXT,
                    delivered_at TEXT NOT NULL,
                    PRIMARY KEY (document_key, target_key)
                );
                -- The history of every document: one row per step and per target.
                CREATE TABLE IF NOT EXISTS events (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    at              TEXT NOT NULL,
                    document_id     TEXT NOT NULL,
                    input           TEXT NOT NULL,
                    step            TEXT NOT NULL,
                    state           TEXT NOT NULL,
                    message         TEXT NOT NULL,
                    document_number TEXT,
                    document_type   TEXT,
                    data_area_id    TEXT,
                    target          TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_events_document ON events(document_id);
                CREATE INDEX IF NOT EXISTS ix_events_number ON events(document_number);
                CREATE INDEX IF NOT EXISTS ix_events_at ON events(at);
                """;
            cmd.ExecuteNonQuery();

            // The 2.0 schema lives in a new %ProgramData%\HiveBDC folder, so no 1.x rows
            // are migrated; the guard stays for future columns.
            EnsureColumn(conn, "exports", "data_area_id", "TEXT");
            EnsureColumn(conn, "exports", "source", "TEXT");
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
        long sizeBytes, string? dataAreaId = null, string source = "json")
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
                    (dedupe_key, document_id, document_type, format, output_file, target_site, size_bytes, exported_at, data_area_id, source)
                VALUES ($key, $id, $type, $format, $out, $site, $size, $at, $area, $source);
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
            cmd.Parameters.AddWithValue("$source", source);
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

    /// <summary>Note that a document reached a target. Best-effort, like <see cref="Record"/>.</summary>
    public void RecordDelivery(string documentKey, string targetKey, string kind, string? location, string? remoteId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO deliveries (document_key, target_key, kind, location, remote_id, delivered_at)
                VALUES ($doc, $target, $kind, $location, $remote, $at)
                ON CONFLICT(document_key, target_key) DO UPDATE SET
                    kind = excluded.kind, location = excluded.location,
                    remote_id = COALESCE(excluded.remote_id, deliveries.remote_id), delivered_at = excluded.delivered_at;
                """;
            cmd.Parameters.AddWithValue("$doc", documentKey);
            cmd.Parameters.AddWithValue("$target", targetKey);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$location", (object?)location ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$remote", (object?)remoteId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(StampFormat));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Delivery ledger write failed for {Key} ({Kind}); delivery is unaffected.", documentKey, kind);
        }
    }

    public void RecordEvent(DocumentEvent entry)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO events (at, document_id, input, step, state, message, document_number, document_type, data_area_id, target)
                VALUES ($at, $id, $input, $step, $state, $message, $number, $type, $area, $target);
                """;
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(StampFormat));
            cmd.Parameters.AddWithValue("$id", entry.DocumentId);
            cmd.Parameters.AddWithValue("$input", entry.Input);
            cmd.Parameters.AddWithValue("$step", entry.Step);
            cmd.Parameters.AddWithValue("$state", entry.State);
            cmd.Parameters.AddWithValue("$message", EventText.Clean(entry.Message));
            cmd.Parameters.AddWithValue("$number", (object?)entry.DocumentNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", (object?)entry.DocumentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$area", (object?)entry.DataAreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$target", (object?)entry.Target ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Document history write failed for {Id} ({Step}); processing is unaffected.", entry.DocumentId, entry.Step);
        }
    }

    public IReadOnlyList<DocumentEvent> Attention(int limit)
    {
        var list = new List<DocumentEvent>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, e.at, e.document_id, e.input, e.step, e.state, e.message, e.document_number, e.document_type, e.data_area_id, e.target
                FROM events e JOIN (SELECT document_id, MAX(id) AS id FROM events GROUP BY document_id) last ON last.id = e.id
                WHERE e.state IN ('failed', 'waiting')
                ORDER BY e.id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 1000));
            using var r = cmd.ExecuteReader();
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (r.Read())
                list.Add(new DocumentEvent(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                    Text(7), Text(8), Text(9), Text(10), r.GetInt64(0), r.GetString(1)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Document history read failed.");
        }
        return list;
    }

    public IReadOnlyList<DocumentEvent> Events(string? search, int limit)
    {
        var list = new List<DocumentEvent>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            string columns = "id, at, document_id, input, step, state, message, document_number, document_type, data_area_id, target";
            if (string.IsNullOrWhiteSpace(search))
                cmd.CommandText = $"SELECT {columns} FROM (SELECT {columns} FROM events ORDER BY id DESC LIMIT $n) ORDER BY id";
            else
            {
                // A search names one document: its id from the file name or its ERP number. Every arrival of that
                // document is returned, oldest step first.
                cmd.CommandText = $"""
                    SELECT {columns} FROM events
                    WHERE document_id IN (SELECT document_id FROM events WHERE document_id = $s OR document_number = $s)
                    ORDER BY id LIMIT $n
                    """;
                cmd.Parameters.AddWithValue("$s", search.Trim());
            }
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 5000));
            using var r = cmd.ExecuteReader();
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (r.Read())
                list.Add(new DocumentEvent(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                    Text(7), Text(8), Text(9), Text(10), r.GetInt64(0), r.GetString(1)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Document history read failed.");
        }
        return list;
    }

    /// <summary>The id a target gave this document on an earlier delivery, or null.</summary>
    public string? DeliveryRemoteId(string documentKey, string targetKey)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT remote_id FROM deliveries WHERE document_key = $doc AND target_key = $target";
            cmd.Parameters.AddWithValue("$doc", documentKey);
            cmd.Parameters.AddWithValue("$target", targetKey);
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Delivery ledger read failed for {Key}.", documentKey);
            return null;
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
            using (var old = conn.CreateCommand())
            {
                old.CommandText = "DELETE FROM deliveries WHERE delivered_at < $cutoff; DELETE FROM events WHERE at < $cutoff;";
                old.Parameters.AddWithValue("$cutoff", DateTime.Now.AddDays(-Math.Abs(retentionDays)).ToString(StampFormat));
                old.ExecuteNonQuery();
            }
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
            SELECT document_id, document_type, format, output_file, target_site, size_bytes, exported_at, data_area_id, source
            FROM exports ORDER BY id DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", Math.Clamp(n, 1, 5000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ExportRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? "json" : r.GetString(8)));
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
