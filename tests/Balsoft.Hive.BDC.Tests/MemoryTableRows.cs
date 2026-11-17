using Balsoft.Hive.BDC.Storage;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// Table Storage in memory, with the key rules of the real service (no '/', '\', '#', '?' or control characters, at most
/// 1 KiB) and hooks for failures: <see cref="FailOn"/> makes an operation throw, <see cref="OtherWriterChangesLeft"/>
/// lets "another writer" change a row right before a conditional replace.
/// </summary>
internal sealed class MemoryTableRows : ITableRows
{
    private static readonly Comparer<(string Partition, string Row)> KeyOrder = Comparer<(string Partition, string Row)>.Create((a, b) =>
    {
        int c = string.CompareOrdinal(a.Partition, b.Partition);
        return c != 0 ? c : string.CompareOrdinal(a.Row, b.Row);
    });

    private readonly Dictionary<string, SortedDictionary<(string Partition, string Row), StateRow>> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _queries = new(StringComparer.Ordinal);
    private long _etag;

    /// <summary>Operation name ("Get", "Query", "Upsert", "TryAdd", "TryReplace", "Delete") and table: true throws.</summary>
    public Func<string, string, bool>? FailOn { get; set; }

    public int OtherWriterChangesLeft { get; set; }

    public int Count(string table) => Table(table).Count;

    public int QueriesOf(string table) => _queries.GetValueOrDefault(table);

    public StateRow? Stored(string table, string partition, string row) => Table(table).GetValueOrDefault((partition, row));

    public void Ensure(string table)
    {
        Check("Ensure", table);
        Table(table);
    }

    public StateRow? Get(string table, string partition, string row)
    {
        Check("Get", table);
        return Table(table).TryGetValue((partition, row), out var found) ? Copy(found, found.ETag) : null;
    }

    public IEnumerable<StateRow> Query(string table, RowQuery query)
    {
        Check("Query", table);
        _queries[table] = QueriesOf(table) + 1;
        return Table(table).Values
            .Where(r => query.Partition is null || r.Partition == query.Partition)
            .Where(r => query.PartitionFrom is null || string.CompareOrdinal(r.Partition, query.PartitionFrom) >= 0)
            .Where(r => query.PartitionBefore is null || string.CompareOrdinal(r.Partition, query.PartitionBefore) < 0)
            .Where(r => query.RowFrom is null || string.CompareOrdinal(r.Row, query.RowFrom) >= 0)
            .Where(r => query.RowBefore is null || string.CompareOrdinal(r.Row, query.RowBefore) < 0)
            .Select(r => Copy(r, r.ETag))
            .ToList();
    }

    public void Upsert(string table, StateRow row, bool merge = false)
    {
        Check("Upsert", table);
        CheckKeys(row);
        var rows = Table(table);
        var values = merge && rows.TryGetValue((row.Partition, row.Row), out var stored)
            ? new Dictionary<string, object?>(stored.Values)
            : new Dictionary<string, object?>();
        foreach (var (name, value) in row.Values)
            if (value is not null) values[name] = value;
        rows[(row.Partition, row.Row)] = new StateRow(row.Partition, row.Row, values, NextETag());
    }

    public bool TryAdd(string table, StateRow row)
    {
        Check("TryAdd", table);
        CheckKeys(row);
        var rows = Table(table);
        if (rows.ContainsKey((row.Partition, row.Row))) return false;
        rows[(row.Partition, row.Row)] = Copy(row, NextETag());
        return true;
    }

    public bool TryReplace(string table, StateRow row)
    {
        Check("TryReplace", table);
        var rows = Table(table);
        if (!rows.TryGetValue((row.Partition, row.Row), out var stored)) return false;
        if (OtherWriterChangesLeft > 0)
        {
            OtherWriterChangesLeft--;
            var bumped = new Dictionary<string, object?>(stored.Values) { ["Value"] = stored.Number("Value") + 1 };
            stored = rows[(row.Partition, row.Row)] = new StateRow(stored.Partition, stored.Row, bumped, NextETag());
        }
        if (stored.ETag != row.ETag) return false;
        rows[(row.Partition, row.Row)] = Copy(row, NextETag());
        return true;
    }

    public void Delete(string table, string partition, string row)
    {
        Check("Delete", table);
        Table(table).Remove((partition, row));
    }

    private SortedDictionary<(string Partition, string Row), StateRow> Table(string table)
    {
        if (!_tables.TryGetValue(table, out var rows))
            _tables[table] = rows = new SortedDictionary<(string Partition, string Row), StateRow>(KeyOrder);
        return rows;
    }

    private void Check(string operation, string table)
    {
        if (FailOn?.Invoke(operation, table) == true)
            throw new InvalidOperationException($"Table storage is not reachable ({operation} on {table}).");
    }

    private static void CheckKeys(StateRow row)
    {
        foreach (var key in new[] { row.Partition, row.Row })
            if (key.Length > 512 || key.Any(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c)))
                throw new ArgumentException($"'{key}' is not a valid Table Storage key.");
    }

    private string NextETag() => "W/\"" + Interlocked.Increment(ref _etag) + "\"";

    private static StateRow Copy(StateRow row, string? etag)
        => new(row.Partition, row.Row, row.Values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value), etag);
}