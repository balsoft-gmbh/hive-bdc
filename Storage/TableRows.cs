using System.Collections.Concurrent;
using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>One entity of a table: its keys, its values and the ETag it was read with. Null values are not stored.</summary>
public sealed class StateRow
{
    public StateRow(string partition, string row, IDictionary<string, object?>? values = null, string? etag = null)
    {
        Partition = partition;
        Row = row;
        Values = values is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : new Dictionary<string, object?>(values, StringComparer.Ordinal);
        ETag = etag;
    }

    public string Partition { get; }

    public string Row { get; }

    public Dictionary<string, object?> Values { get; }

    public string? ETag { get; }

    public string? Text(string name) => Values.TryGetValue(name, out var v) ? v?.ToString() : null;

    public long Number(string name)
        => Values.TryGetValue(name, out var v) && v is not null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
}

/// <summary>
/// Which rows a query returns: one partition or a range of partitions, and optionally a range of row keys. Keys compare
/// ordinally; a "from" bound is inclusive, a "before" bound exclusive. An empty query returns the whole table.
/// </summary>
public sealed record RowQuery(
    string? Partition = null, string? PartitionFrom = null, string? PartitionBefore = null, string? RowFrom = null, string? RowBefore = null);

/// <summary>The table operations the Azure state store needs, so it can be tested without Azure.</summary>
public interface ITableRows
{
    /// <summary>Creates the table when it does not exist.</summary>
    void Ensure(string table);

    StateRow? Get(string table, string partition, string row);

    /// <summary>The rows the query selects, in partition key then row key order.</summary>
    IEnumerable<StateRow> Query(string table, RowQuery query);

    /// <summary>Writes the row; with <paramref name="merge"/> the stored values the row does not name are kept.</summary>
    void Upsert(string table, StateRow row, bool merge = false);

    /// <summary>Adds the row; false when a row with the same keys exists.</summary>
    bool TryAdd(string table, StateRow row);

    /// <summary>Replaces the row only when it still has the ETag it was read with; false when it changed since.</summary>
    bool TryReplace(string table, StateRow row);

    /// <summary>Deletes the row; a missing row is not an error.</summary>
    void Delete(string table, string partition, string row);
}

/// <summary>
/// Azure Table Storage. Signs in with a storage connection string, or with the identity of the host (managed identity in
/// Azure, the Azure CLI sign-in on a developer machine) at the table endpoint https://{account}.table.core.windows.net.
/// </summary>
public sealed class AzureTableRows : ITableRows
{
    private static readonly HashSet<string> SystemProperties = new(StringComparer.Ordinal)
    {
        "PartitionKey", "RowKey", "Timestamp", "odata.etag",
    };

    private readonly TableServiceClient _service;
    private readonly ConcurrentDictionary<string, TableClient> _tables = new(StringComparer.Ordinal);

    public AzureTableRows(TableServiceClient service) => _service = service;

    public static AzureTableRows For(string? connectionString, string? tableEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            return new AzureTableRows(new TableServiceClient(connectionString));
        if (!Uri.TryCreate(tableEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Give a storage connection string or the table endpoint (https://<account>.table.core.windows.net).");
        return new AzureTableRows(new TableServiceClient(endpoint, new DefaultAzureCredential()));
    }

    public void Ensure(string table) => Table(table);

    public StateRow? Get(string table, string partition, string row)
    {
        var found = Table(table).GetEntityIfExists<TableEntity>(partition, row);
        return found.HasValue && found.Value is not null ? ToRow(found.Value) : null;
    }

    public IEnumerable<StateRow> Query(string table, RowQuery query)
    {
        var parts = new List<string>();
        if (query.Partition is not null) parts.Add(TableClient.CreateQueryFilter($"PartitionKey eq {query.Partition}"));
        if (query.PartitionFrom is not null) parts.Add(TableClient.CreateQueryFilter($"PartitionKey ge {query.PartitionFrom}"));
        if (query.PartitionBefore is not null) parts.Add(TableClient.CreateQueryFilter($"PartitionKey lt {query.PartitionBefore}"));
        if (query.RowFrom is not null) parts.Add(TableClient.CreateQueryFilter($"RowKey ge {query.RowFrom}"));
        if (query.RowBefore is not null) parts.Add(TableClient.CreateQueryFilter($"RowKey lt {query.RowBefore}"));
        string? filter = parts.Count == 0 ? null : string.Join(" and ", parts);
        foreach (var entity in Table(table).Query<TableEntity>(filter))
            yield return ToRow(entity);
    }

    public void Upsert(string table, StateRow row, bool merge = false)
        => Table(table).UpsertEntity(ToEntity(row), merge ? TableUpdateMode.Merge : TableUpdateMode.Replace);

    public bool TryAdd(string table, StateRow row)
    {
        try
        {
            Table(table).AddEntity(ToEntity(row));
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public bool TryReplace(string table, StateRow row)
    {
        try
        {
            Table(table).UpdateEntity(ToEntity(row), new ETag(row.ETag ?? throw new ArgumentException("The row has no ETag.")), TableUpdateMode.Replace);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 412 or 404)
        {
            return false;
        }
    }

    public void Delete(string table, string partition, string row)
    {
        try
        {
            Table(table).DeleteEntity(partition, row);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private TableClient Table(string name) => _tables.GetOrAdd(name, n =>
    {
        var client = _service.GetTableClient(n);
        client.CreateIfNotExists();
        return client;
    });

    private static TableEntity ToEntity(StateRow row)
    {
        var entity = new TableEntity(row.Partition, row.Row);
        foreach (var (name, value) in row.Values)
            if (value is not null) entity[name] = value;
        return entity;
    }

    private static StateRow ToRow(TableEntity entity)
        => new(entity.PartitionKey, entity.RowKey,
            entity.Where(p => !SystemProperties.Contains(p.Key)).ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
            entity.ETag.ToString());
}