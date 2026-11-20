using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Runs the inbound flows (<see cref="BDCOptions.Flows"/>): finds the records a document system marks ready, delivers
/// for each a CSV (and its file) where the ERP reads (a folder, Blob Storage or Service Bus), then marks the record
/// done in that system. Source and destination are connectors by kind (<see cref="IRecordSource"/>,
/// <see cref="IInboundTarget"/>). Every record gets a history like an outbound document. A record whose CSV was
/// delivered but whose done mark failed is not delivered again: the next run only repeats the mark.
/// </summary>
public sealed class ImportProcessor
{
    private const int RecordsPerRun = 50;
    private static readonly TimeSpan FailedPause = TimeSpan.FromHours(1);

    private readonly ILogger _log;
    private readonly IStateStore? _state;
    private readonly ConnectorRegistry _connectors;
    private readonly Dictionary<string, DateTime> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public ImportProcessor(ILogger log, IStateStore? state, ConnectorRegistry? connectors = null)
    {
        _log = log;
        _state = state;
        _connectors = connectors ?? ConnectorRegistry.BuiltIn();
    }

    /// <summary>DocuWare with its own client factory (tests use a fake DocuWare behind it).</summary>
    public ImportProcessor(ILogger log, IStateStore? state, Func<ConnectionOptions, DocuWareClient> clients)
        : this(log, state, WithDocuWare(clients)) { }

    private static ConnectorRegistry WithDocuWare(Func<ConnectionOptions, DocuWareClient> clients)
    {
        var registry = ConnectorRegistry.BuiltIn();
        registry.RegisterSource(ConnectionKinds.DocuWare, runtime => new DocuWareRecordSource(runtime, clients));
        return registry;
    }

    public DocumentProcessor.ScanResult RunOnce(BDCOptions opts, LicenseResult license)
    {
        var result = new DocumentProcessor.ScanResult();
        var runtime = new SourceRuntime(opts, _log);
        foreach (var flow in opts.Flows.Where(f => f.Enabled))
        {
            try { result += RunFlow(runtime, flow, license); }
            catch (Exception ex)
            {
                WarnOnce(flow.Id, $"flow {flow.DisplayName}: {ex.Message}");
                result += new DocumentProcessor.ScanResult(0, 0, 1);
            }
        }
        return result;
    }

    private DocumentProcessor.ScanResult RunFlow(SourceRuntime runtime, FlowOptions flow, LicenseResult license)
    {
        var source = _connectors.Source(flow.Source.Kind, runtime);
        if (source is null)
        { WarnOnce(flow.Id, $"flow {flow.DisplayName}: source kind '{flow.Source.Kind}' is not known to this version of Hive BDC."); return new(0, 0, 0); }
        var d = source.Descriptor;
        if (!license.HasFeature(d.RequiredFeature))
        { WarnOnce(flow.Id, $"flow {flow.DisplayName}: the installed licence has no '{d.RequiredFeature}' feature, which {d.Name} import needs."); return new(0, 0, 0); }
        var problems = source.Problems(flow.Source).ToList();
        var target = flow.Targets.FirstOrDefault(t => t.Enabled);
        var destination = target is null ? null : _connectors.Inbound(target.Kind, runtime.Options);
        if (target is null)
            problems.Add(SettingsPaths.Pick("Set the folder the ERP reads.", "Choose where your ERP reads the CSV: Blob Storage or Service Bus."));
        else if (destination is null)
            problems.Add($"It goes to '{target.Kind}', which this version of Hive BDC does not know.");
        else
            problems.AddRange(destination.Problems(target));
        if (flow.Produce.Columns.Count == 0) problems.Add("Add at least one CSV column.");
        if (problems.Count > 0) { WarnOnce(flow.Id, $"flow {flow.DisplayName}: " + string.Join(" ", problems)); return new(0, 0, 0); }
        _warned.RemoveWhere(w => w.StartsWith(flow.Id + "|", StringComparison.Ordinal));

        var ids = source.ReadyAsync(flow.Source, RecordsPerRun).GetAwaiter().GetResult();
        var result = new DocumentProcessor.ScanResult();
        foreach (var id in ids)
        {
            string key = $"{flow.Id}|{source.RecordKey(flow.Source, id)}";
            if (_failedUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until) continue;
            result += ImportOne(flow, source, id, target!, destination!, key);
        }
        return result;
    }

    /// <summary>Whether the record's CSV already went out, to the destination of any kind (a flow changed from a folder to Blob Storage after the CSV went out is not delivered twice).</summary>
    private bool Delivered(string key, string kind)
        => _state is not null && (_state.DeliveryRemoteId(key, kind) is not null
                                  || _connectors.InboundKindNames.Any(k => k != kind && _state.DeliveryRemoteId(key, k) is not null));

    private DocumentProcessor.ScanResult ImportOne(FlowOptions flow, IRecordSource source, string id, TargetOptions target,
        IInboundTarget destination, string key)
    {
        var d = source.Descriptor;
        string input = $"{d.Name} {d.RecordNoun} {id}";
        string kind = destination.Descriptor.Kind;
        bool written = Delivered(key, kind);
        bool atDestination = false;
        try
        {
            if (!written)
            {
                Note(id, input, EventSteps.Received, EventStates.Ok, $"Found {d.RecordNoun} {id} in {d.Name}, ready for flow \"{flow.DisplayName}\".", flow);
                var record = source.ReadAsync(flow.Source, id).GetAwaiter().GetResult();

                // The file is read and its place known before the CSV is built, so a CSV that cannot be built leaves nothing behind.
                DocumentFile? file = InboundSettings.SavesFile(target) ? source.FileAsync(flow.Source, id).GetAwaiter().GetResult() : null;
                string? fileName = file is null ? null : FileNameFor(d, id, file.Name);
                var csv = MakeCsv(d, record, flow.Produce, flow.DataAreaId, fileName is null ? "" : destination.FileLocation(target, fileName));
                if (csv.Problems.Count > 0)
                    throw new InvalidOperationException(string.Join(" ", csv.Problems) + $" Correct the columns of the flow in {SettingsPaths.Pick("Settings > Inbound", "Inbound")}.");
                string csvName = csv.FileName;
                string text = csv.Text;

                var delivery = new InboundDelivery(flow, target, id, _log);
                atDestination = true;
                string? savedAs = file is null ? null : destination.SaveFile(delivery, fileName!, file.ReadAllBytes(), file.ContentType);
                var delivered = destination.DeliverCsv(delivery, csvName, text);
                atDestination = false;
                int rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
                _state?.RecordDelivery(key, kind, kind, delivered.Location, delivered.RemoteId ?? id);
                Note(id, input, EventSteps.Delivered, EventStates.Ok,
                    $"Wrote {csvName} with {rows} row(s) {delivered.Summary}" + (savedAs is null ? "." : $" and the file as {savedAs}."), flow, kind);
            }

            string done = source.MarkDoneAsync(flow.Source, id).GetAwaiter().GetResult();
            Note(id, input, EventSteps.Done, EventStates.Ok, done, flow);
            _failedUntil.Remove(key);
            return new(1, 0, 0);
        }
        catch (ConnectorException ex) when (ex.RetryLater)
        {
            Note(id, input, EventSteps.Waiting, EventStates.Waiting, (atDestination ? ex.Message : $"{d.Name}: {ex.Message}") + " Hive BDC tries again on the next scan.", flow);
            return new(0, 0, 1);
        }
        catch (Exception ex)
        {
            _failedUntil[key] = DateTime.UtcNow.Add(FailedPause);
            _log.LogError("[import] {Flow}: {Noun} {Id} failed - {Message}", flow.DisplayName, d.RecordNoun, id, ex.Message);
            Note(id, input, EventSteps.Failed, EventStates.Failed,
                ex.Message + (written ? "" : " Nothing was written for it.") + " Hive BDC tries again in an hour, or after the flow's settings are saved.", flow);
            return new(0, 1, 0);
        }
    }

    /// <summary>The CSV a record makes and its file name; Problems names every column that cannot be filled.</summary>
    public sealed record CsvResult(string Text, string FileName, IReadOnlyList<string> Problems);

    /// <summary>The name the record's file gets where the ERP reads it, e.g. DW17.pdf.</summary>
    public static string FileNameFor(SourceDescriptor d, string id, string sourceName)
    {
        string ext = Path.GetExtension(sourceName);
        return CsvFile.SafeFileName($"{d.FilePrefix}{id}{(ext.Length > 0 ? ext : ".pdf")}");
    }

    /// <summary>Builds the CSV of one record as a run does: columns, separator, number and date format, rows from a table.</summary>
    public static CsvResult MakeCsv(SourceDescriptor d, SourceRecord record, ProduceOptions produce, string dataAreaId, string filePath)
    {
        var culture = CultureFor(produce.Setting("Culture", "de-DE"));
        string delimiter = produce.Setting("Delimiter", ";");
        string rowsFrom = produce.Setting("RowsFrom");
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DataAreaId"] = dataAreaId, ["FilePath"] = filePath };
        var problems = new List<string>();
        string text = CsvFile.Build(record, produce.Columns, rowsFrom.Length > 0 ? rowsFrom : null, delimiter, culture, extra, problems);
        string csvName = "";
        if (problems.Count == 0)
        {
            csvName = CsvFile.SafeFileName(CsvFile.Resolve(produce.Setting("FileName", d.DefaultFileName), record, null,
                new Dictionary<string, object?>(), culture, extra, "file name", null));
            if (!csvName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) csvName += ".csv";
        }
        return new CsvResult(text, csvName, problems.Distinct().ToList());
    }

    /// <summary>Saving the settings clears the pause after a failure, so a corrected flow runs at once.</summary>
    public void SettingsChanged() => _failedUntil.Clear();

    private void Note(string id, string input, string step, string state, string message, FlowOptions flow, string? target = null)
    {
        _log.LogInformation("[import] {Flow}: {Id} {Step} - {Message}", flow.DisplayName, id, step, message);
        _state?.RecordEvent(new DocumentEvent(id, input, step, state, message, id, flow.DisplayName, null, target));
    }

    private void WarnOnce(string flowId, string message)
    {
        if (_warned.Add(flowId + "|" + message)) _log.LogWarning("[import] {Message}", message);
    }

    /// <summary>
    /// Number and date formats for the CSV. Built by hand because the service runs with invariant globalization:
    /// "de-DE" gives 1234,5 and 31.12.2026, "en-US" 1234.5 and 12/31/2026, anything else 1234.5 and 2026-12-31.
    /// </summary>
    public static CultureInfo CultureFor(string name)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        string n = (name ?? "").Trim().ToLowerInvariant();
        if (n.StartsWith("de"))
        {
            culture.NumberFormat.NumberDecimalSeparator = ",";
            culture.NumberFormat.NumberGroupSeparator = ".";
            culture.DateTimeFormat.ShortDatePattern = "dd.MM.yyyy";
        }
        else if (n == "en-us")
            culture.DateTimeFormat.ShortDatePattern = "MM/dd/yyyy";
        else
            culture.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
        return culture;
    }

}