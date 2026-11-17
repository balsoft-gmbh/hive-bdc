using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Runs the inbound flows (<see cref="BDCOptions.Flows"/>): finds the records a document system marks ready, writes for
/// each a CSV (and its file) into the folder the ERP reads, then marks the record done in that system. The system is a
/// source connector by kind (<see cref="IRecordSource"/>). Every record gets a history like an outbound document. A
/// record whose files were written but whose done mark failed is not written again: the next run only repeats the mark.
/// </summary>
public sealed class ImportProcessor
{
    private const int RecordsPerRun = 50;
    private static readonly TimeSpan FailedPause = TimeSpan.FromHours(1);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

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
        string folder = flow.Targets.FirstOrDefault(t => t.Enabled && t.Kind == "folder")?.Setting("Folder") ?? "";
        if (folder.Length == 0) problems.Add("Set the folder the ERP reads.");
        if (flow.Produce.Columns.Count == 0) problems.Add("Add at least one CSV column.");
        if (problems.Count > 0) { WarnOnce(flow.Id, $"flow {flow.DisplayName}: " + string.Join(" ", problems)); return new(0, 0, 0); }
        _warned.RemoveWhere(w => w.StartsWith(flow.Id + "|", StringComparison.Ordinal));

        var ids = source.ReadyAsync(flow.Source, RecordsPerRun).GetAwaiter().GetResult();
        var result = new DocumentProcessor.ScanResult();
        foreach (var id in ids)
        {
            string key = $"{flow.Id}|{source.RecordKey(flow.Source, id)}";
            if (_failedUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until) continue;
            result += ImportOne(flow, source, id, folder, key);
        }
        return result;
    }

    private DocumentProcessor.ScanResult ImportOne(FlowOptions flow, IRecordSource source, string id, string folder, string key)
    {
        var d = source.Descriptor;
        string input = $"{d.Name} {d.RecordNoun} {id}";
        bool written = _state?.DeliveryRemoteId(key, "folder") is not null;
        try
        {
            if (!written)
            {
                Note(id, input, EventSteps.Received, EventStates.Ok, $"Found {d.RecordNoun} {id} in {d.Name}, ready for flow \"{flow.DisplayName}\".", flow);
                var record = source.ReadAsync(flow.Source, id).GetAwaiter().GetResult();
                var culture = CultureFor(flow.Produce.Setting("Culture", "de-DE"));
                string delimiter = flow.Produce.Setting("Delimiter", ";");
                string rowsFrom = flow.Produce.Setting("RowsFrom");
                var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DataAreaId"] = flow.DataAreaId };

                string? filePath = null;
                if (string.Equals(flow.Targets.First(t => t.Kind == "folder").Setting("SaveFile"), "true", StringComparison.OrdinalIgnoreCase)
                    && source.FileAsync(flow.Source, id).GetAwaiter().GetResult() is { } file)
                {
                    string ext = Path.GetExtension(file.Name);
                    filePath = Path.Combine(folder, CsvFile.SafeFileName($"{d.FilePrefix}{id}{(ext.Length > 0 ? ext : ".pdf")}"));
                    CsvFile.WriteAtomic(filePath, file.ReadAllBytes());
                }
                extra["FilePath"] = filePath ?? "";

                var problems = new List<string>();
                string text = CsvFile.Build(record, flow.Produce.Columns, rowsFrom.Length > 0 ? rowsFrom : null, delimiter, culture, extra, problems);
                if (problems.Count > 0)
                {
                    if (filePath is not null) TryDelete(filePath);
                    throw new InvalidOperationException(string.Join(" ", problems.Distinct()) + " Correct the columns of the flow in Settings > Inbound.");
                }
                string fileName = CsvFile.SafeFileName(CsvFile.Resolve(flow.Produce.Setting("FileName", d.DefaultFileName), record, null,
                    new Dictionary<string, object?>(), culture, extra, "file name", null));
                if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) fileName += ".csv";
                string csvPath = Path.Combine(folder, fileName);
                CsvFile.WriteAtomic(csvPath, Utf8WithBom.GetPreamble().Concat(Utf8WithBom.GetBytes(text)).ToArray());
                int rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
                _state?.RecordDelivery(key, "folder", "folder", csvPath, id);
                Note(id, input, EventSteps.Delivered, EventStates.Ok,
                    $"Wrote {fileName} with {rows} row(s) to {folder}" + (filePath is null ? "." : $" and the file as {Path.GetFileName(filePath)}."), flow, "folder");
            }

            string done = source.MarkDoneAsync(flow.Source, id).GetAwaiter().GetResult();
            Note(id, input, EventSteps.Done, EventStates.Ok, done, flow);
            _failedUntil.Remove(key);
            return new(1, 0, 0);
        }
        catch (ConnectorException ex) when (ex.RetryLater)
        {
            Note(id, input, EventSteps.Waiting, EventStates.Waiting, $"{d.Name}: {ex.Message} Hive BDC tries again on the next scan.", flow);
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}