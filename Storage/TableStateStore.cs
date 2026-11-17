using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>
/// The state store of the Azure run mode: the same exports, deliveries and document history as the SQLite ledger, in
/// Azure Table Storage (SQLite on a storage mount is ruled out by Microsoft). One instance per customer writes, so the
/// exports and the per-document state are mirrored in memory and the dashboard reads never touch the tables; a failed
/// write drops the mirror and the next call loads it again. Every member is best-effort, as <see cref="IStateStore"/> says.
/// </summary>
/// <remarks>
/// Tables ({prefix}Exports, Deliveries, Events, Documents, Meta). Exports: partition = local day of the export, row = id.
/// Deliveries: partition and row = hashes of the document key and the target key. Events: partition = hash of the
/// document id, row = id. Documents: one row per document with its last step, every input name and number it had (for
/// search) and its last id. Ids are microseconds since 1970 (UTC), unique and rising within the instance, so a row key
/// sorts by time and the cutoff of a prune is an id.
/// </remarks>
public sealed class TableStateStore : IStateStore
{
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss";
    private const string DocumentsPartition = "documents";
    private const string MetaPartition = "meta";
    private const string LifetimeRow = "lifetime_exports";
    private const int ListCap = 4000;

    private readonly ITableRows _rows;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly string _exports, _deliveries, _events, _documents, _meta;
    private readonly object _gate = new();

    private Dictionary<string, StateRow>? _exportsByKey;
    private Dictionary<string, StateRow>? _documentsById;
    private long _lifetime;
    private long _lastId;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public TableStateStore(ITableRows rows, ILogger log, string tablePrefix = "HiveBDC", TimeProvider? clock = null)
    {
        if (tablePrefix.Length is 0 or > 50 || !char.IsAsciiLetter(tablePrefix[0]) || !tablePrefix.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("The table prefix must be 1 to 50 letters or digits and start with a letter.", nameof(tablePrefix));
        _rows = rows;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _exports = tablePrefix + "Exports";
        _deliveries = tablePrefix + "Deliveries";
        _events = tablePrefix + "Events";
        _documents = tablePrefix + "Documents";
        _meta = tablePrefix + "Meta";
    }

    public void Record(string documentId, string documentType, string format, string outputFile, string? targetSite,
        long sizeBytes, string? dataAreaId = null, string source = "json")
    {
        lock (_gate)
        {
            try
            {
                var exports = Exports();
                string key = $"{dataAreaId ?? ""}|{documentType}|{documentId}|{format}|{Path.GetFileName(outputFile)}";
                long id = NextId();
                var at = _clock.GetLocalNow();
                var row = new StateRow(at.ToString("yyyyMMdd"), RowKey(id), new Dictionary<string, object?>
                {
                    ["DedupeKey"] = key,
                    ["DocumentId"] = documentId,
                    ["DocumentType"] = documentType,
                    ["Format"] = format,
                    ["OutputFile"] = outputFile,
                    ["TargetSite"] = targetSite,
                    ["SizeBytes"] = sizeBytes,
                    ["ExportedAt"] = at.ToString(StampFormat),
                    ["DataAreaId"] = dataAreaId,
                    ["Source"] = source,
                });
                // The new row is written before the old one goes, so a failure in between leaves a duplicate that the
                // next load removes, never a lost export.
                _rows.Upsert(_exports, row);
                bool isNew = !exports.TryGetValue(key, out var old);
                exports[key] = row;
                if (old is not null) _rows.Delete(_exports, old.Partition, old.Row);
                if (isNew) CountLifetimeExport();
            }
            catch (Exception ex)
            {
                Forget();
                _log.LogWarning(ex, "Export ledger write failed for {Id} ({Type}); delivery is unaffected.", documentId, documentType);
            }
        }
    }

    public void RecordDelivery(string documentKey, string targetKey, string kind, string? location, string? remoteId)
    {
        try
        {
            string partition = Hash(documentKey), row = Hash(targetKey);
            // An earlier remote id stays when this delivery reports none, as in the SQLite ledger.
            remoteId ??= _rows.Get(_deliveries, partition, row)?.Text("RemoteId");
            _rows.Upsert(_deliveries, new StateRow(partition, row, new Dictionary<string, object?>
            {
                ["DocumentKey"] = documentKey,
                ["TargetKey"] = targetKey,
                ["Kind"] = kind,
                ["Location"] = location,
                ["RemoteId"] = remoteId,
                ["DeliveredAt"] = Stamp(),
            }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Delivery ledger write failed for {Key} ({Kind}); delivery is unaffected.", documentKey, kind);
        }
    }

    public string? DeliveryRemoteId(string documentKey, string targetKey)
    {
        try
        {
            return _rows.Get(_deliveries, Hash(documentKey), Hash(targetKey))?.Text("RemoteId");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Delivery ledger read failed for {Key}.", documentKey);
            return null;
        }
    }

    public void RecordEvent(DocumentEvent entry)
    {
        lock (_gate)
        {
            try
            {
                var documents = Documents();
                long id = NextId();
                string at = Stamp();
                var values = new Dictionary<string, object?>
                {
                    ["At"] = at,
                    ["DocumentId"] = entry.DocumentId,
                    ["Input"] = entry.Input,
                    ["Step"] = entry.Step,
                    ["State"] = entry.State,
                    ["Message"] = EventText.Clean(entry.Message),
                    ["DocumentNumber"] = entry.DocumentNumber,
                    ["DocumentType"] = entry.DocumentType,
                    ["DataAreaId"] = entry.DataAreaId,
                    ["Target"] = entry.Target,
                };
                _rows.Upsert(_events, new StateRow(Hash(entry.DocumentId), RowKey(id), values));

                documents.TryGetValue(entry.DocumentId, out var before);
                var state = new Dictionary<string, object?>(values)
                {
                    ["LastId"] = id,
                    ["Inputs"] = Append(before?.Text("Inputs"), entry.Input),
                    ["Numbers"] = Append(before?.Text("Numbers"), entry.DocumentNumber),
                };
                var document = new StateRow(DocumentsPartition, Hash(entry.DocumentId), state);
                _rows.Upsert(_documents, document);
                documents[entry.DocumentId] = document;
            }
            catch (Exception ex)
            {
                Forget();
                _log.LogWarning(ex, "Document history write failed for {Id} ({Step}); processing is unaffected.", entry.DocumentId, entry.Step);
            }
        }
    }

    public IReadOnlyList<DocumentEvent> Events(string? search, int limit)
    {
        int n = Math.Clamp(limit, 1, 5000);
        lock (_gate)
        {
            try
            {
                var newestFirst = Documents().Values.OrderByDescending(d => d.Number("LastId"));
                if (string.IsNullOrWhiteSpace(search))
                {
                    // The newest n steps of all documents: a document whose last step is older than the n-th newest step
                    // found so far cannot add one, and neither can any document after it.
                    var found = new List<DocumentEvent>();
                    foreach (var document in newestFirst)
                    {
                        if (found.Count >= n && document.Number("LastId") < found.OrderByDescending(e => e.Id).ElementAt(n - 1).Id)
                            break;
                        found.AddRange(History(document));
                    }
                    return found.OrderByDescending(e => e.Id).Take(n).OrderBy(e => e.Id).ToList();
                }

                // Part of a document number, a document id or an input file name, in any case; every step of the twenty
                // newest matching documents, oldest first.
                string term = search.Trim();
                bool Has(string? text) => text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
                return newestFirst
                    .Where(d => Has(d.Text("DocumentId")) || Has(d.Text("Numbers")) || Has(d.Text("Inputs")))
                    .Take(20)
                    .SelectMany(History)
                    .OrderBy(e => e.Id)
                    .Take(n)
                    .ToList();
            }
            catch (Exception ex)
            {
                Forget();
                _log.LogWarning(ex, "Document history read failed.");
                return Array.Empty<DocumentEvent>();
            }
        }
    }

    public IReadOnlyList<DocumentEvent> Attention(int limit)
    {
        lock (_gate)
        {
            try
            {
                return Documents().Values
                    .Where(d => d.Text("State") is EventStates.Failed or EventStates.Waiting)
                    .OrderByDescending(d => d.Number("LastId"))
                    .Take(Math.Clamp(limit, 1, 1000))
                    .Select(d => ToEvent(d, d.Number("LastId")))
                    .ToList();
            }
            catch (Exception ex)
            {
                Forget();
                _log.LogWarning(ex, "Document history read failed.");
                return Array.Empty<DocumentEvent>();
            }
        }
    }

    public ExportLedger.MetricsSnapshot Metrics(int recent)
    {
        lock (_gate)
        {
            try
            {
                var rows = Exports().Values;
                var now = _clock.GetLocalNow().DateTime;
                string today = now.Date.ToString(StampFormat);
                string month = new DateTime(now.Year, now.Month, 1).ToString(StampFormat);
                bool Since(StateRow r, string start) => string.CompareOrdinal(r.Text("ExportedAt"), start) >= 0;

                var byType = rows.Where(r => Since(r, month))
                    .GroupBy(r => r.Text("DocumentType") ?? "")
                    .Select(g => new TypeCount(g.Key, g.Count()))
                    .OrderByDescending(t => t.Count).ThenBy(t => t.DocumentType, StringComparer.Ordinal)
                    .ToList();
                var newest = rows.OrderByDescending(r => long.Parse(r.Row)).Take(Math.Clamp(recent, 1, 5000))
                    .Select(r => new ExportRecord(
                        r.Text("DocumentId") ?? "", r.Text("DocumentType") ?? "", r.Text("Format") ?? "", r.Text("OutputFile") ?? "",
                        r.Text("TargetSite"), r.Number("SizeBytes"), r.Text("ExportedAt") ?? "", r.Text("DataAreaId"),
                        r.Text("Source") ?? "json"))
                    .ToList();
                return new ExportLedger.MetricsSnapshot(
                    rows.Count(r => Since(r, today)), rows.Count(r => Since(r, month)), Math.Max(_lifetime, rows.Count), byType, newest);
            }
            catch (Exception ex)
            {
                Forget();
                _log.LogWarning(ex, "Export ledger metrics read failed.");
                return new ExportLedger.MetricsSnapshot(0, 0, 0, Array.Empty<TypeCount>(), Array.Empty<ExportRecord>());
            }
        }
    }

    public int Prune(int retentionDays = ExportLedger.DefaultRetentionDays)
    {
        lock (_gate)
        {
            try
            {
                var days = TimeSpan.FromDays(Math.Abs(retentionDays));
                string cutoff = (_clock.GetLocalNow() - days).ToString(StampFormat);
                long cutoffId = Micros(_clock.GetUtcNow() - days);

                var exports = Exports();
                var oldExports = exports.Where(e => string.CompareOrdinal(e.Value.Text("ExportedAt"), cutoff) < 0).ToList();
                foreach (var (key, row) in oldExports)
                {
                    _rows.Delete(_exports, row.Partition, row.Row);
                    exports.Remove(key);
                }
                foreach (var row in _rows.Query(_deliveries, new RowQuery())
                             .Where(r => string.CompareOrdinal(r.Text("DeliveredAt"), cutoff) < 0).ToList())
                    _rows.Delete(_deliveries, row.Partition, row.Row);
                foreach (var row in _rows.Query(_events, new RowQuery(RowBefore: RowKey(cutoffId))).ToList())
                    _rows.Delete(_events, row.Partition, row.Row);
                var documents = Documents();
                foreach (var (id, row) in documents.Where(d => d.Value.Number("LastId") < cutoffId).ToList())
                {
                    _rows.Delete(_documents, row.Partition, row.Row);
                    documents.Remove(id);
                }

                if (oldExports.Count > 0)
                    _log.LogInformation("Export ledger pruned {Deleted} row(s) older than {Days} days.", oldExports.Count, retentionDays);
                return oldExports.Count;
            }
            catch (Exception ex)
            {
                Forget();
                _log.LogWarning(ex, "Export ledger prune failed; conversion is unaffected.");
                return 0;
            }
        }
    }

    public void PruneIfDue(int retentionDays = ExportLedger.DefaultRetentionDays)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (now - _lastPruneUtc < TimeSpan.FromHours(24)) return;
        _lastPruneUtc = now;
        Prune(retentionDays);
    }

    private Dictionary<string, StateRow> Exports()
    {
        Load();
        return _exportsByKey!;
    }

    private Dictionary<string, StateRow> Documents()
    {
        Load();
        return _documentsById!;
    }

    /// <summary>Reads the exports, the document states and the lifetime counter once; a failure leaves nothing loaded.</summary>
    private void Load()
    {
        if (_exportsByKey is not null && _documentsById is not null) return;
        foreach (var table in new[] { _exports, _deliveries, _events, _documents, _meta })
            _rows.Ensure(table);

        long lastId = 0;
        var exports = new Dictionary<string, StateRow>(StringComparer.Ordinal);
        foreach (var row in _rows.Query(_exports, new RowQuery()))
        {
            long id = long.Parse(row.Row);
            lastId = Math.Max(lastId, id);
            string key = row.Text("DedupeKey") ?? row.Row;
            if (exports.TryGetValue(key, out var other))
            {
                var (keep, drop) = long.Parse(other.Row) > id ? (other, row) : (row, other);
                _rows.Delete(_exports, drop.Partition, drop.Row);
                exports[key] = keep;
            }
            else exports[key] = row;
        }
        var documents = new Dictionary<string, StateRow>(StringComparer.Ordinal);
        foreach (var row in _rows.Query(_documents, new RowQuery(Partition: DocumentsPartition)))
        {
            lastId = Math.Max(lastId, row.Number("LastId"));
            documents[row.Text("DocumentId") ?? row.Row] = row;
        }
        _lifetime = _rows.Get(_meta, MetaPartition, LifetimeRow)?.Number("Value") ?? 0;
        _lastId = Math.Max(_lastId, lastId);
        _exportsByKey = exports;
        _documentsById = documents;
    }

    private void Forget()
    {
        _exportsByKey = null;
        _documentsById = null;
    }

    /// <summary>Adds one to the lifetime counter; another writer in between makes it read and try again.</summary>
    private void CountLifetimeExport()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var current = _rows.Get(_meta, MetaPartition, LifetimeRow);
            bool written = current is null
                ? _rows.TryAdd(_meta, new StateRow(MetaPartition, LifetimeRow, new Dictionary<string, object?> { ["Value"] = 1L }))
                : _rows.TryReplace(_meta, new StateRow(MetaPartition, LifetimeRow,
                    new Dictionary<string, object?> { ["Value"] = current.Number("Value") + 1 }, current.ETag));
            if (!written) continue;
            _lifetime = (current?.Number("Value") ?? 0) + 1;
            return;
        }
        _log.LogWarning("The lifetime export counter changed under every try; this export is not counted in the total.");
    }

    private IEnumerable<DocumentEvent> History(StateRow document)
        => _rows.Query(_events, new RowQuery(Partition: document.Row)).Select(e => ToEvent(e, long.Parse(e.Row)));

    private static DocumentEvent ToEvent(StateRow r, long id)
        => new(r.Text("DocumentId") ?? "", r.Text("Input") ?? "", r.Text("Step") ?? "", r.Text("State") ?? "", r.Text("Message") ?? "",
            r.Text("DocumentNumber"), r.Text("DocumentType"), r.Text("DataAreaId"), r.Text("Target"), id, r.Text("At"));

    private long NextId()
    {
        _lastId = Math.Max(_lastId + 1, Micros(_clock.GetUtcNow()));
        return _lastId;
    }

    private string Stamp() => _clock.GetLocalNow().ToString(StampFormat);

    private static long Micros(DateTimeOffset at) => (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    private static string RowKey(long id) => id.ToString("D19");

    /// <summary>A key that is valid in Table Storage whatever the text holds (no '/', '\', '#', '?', no length limit).</summary>
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The distinct values a document had, one per line, the oldest dropped first when the list gets long.</summary>
    private static string? Append(string? list, string? value)
    {
        if (string.IsNullOrEmpty(value)) return list;
        var values = (list ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (values.Contains(value, StringComparer.Ordinal)) return list;
        values.Add(value.Replace('\n', ' '));
        string joined = string.Join('\n', values);
        while (joined.Length > ListCap && values.Count > 1)
        {
            values.RemoveAt(0);
            joined = string.Join('\n', values);
        }
        return joined;
    }
}