using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Runs the flows that start outside the ERP (<see cref="BDCOptions.Flows"/>): finds the DocuWare documents marked
/// ready, writes for each a CSV (and its file) into the folder the ERP reads, then marks the document done in DocuWare.
/// Every record gets a history like an exported document. A record whose files were written but whose done mark
/// failed is not written again: the next run only repeats the mark.
/// </summary>
public sealed class ImportProcessor
{
    private const int RecordsPerRun = 50;
    private static readonly TimeSpan FailedPause = TimeSpan.FromHours(1);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly ILogger _log;
    private readonly IStateStore? _state;
    private readonly Func<ConnectionOptions, DocuWareClient> _clients;
    private readonly Dictionary<string, DateTime> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public ImportProcessor(ILogger log, IStateStore? state, Func<ConnectionOptions, DocuWareClient>? clients = null)
    {
        _log = log;
        _state = state;
        _clients = clients ?? DocuWareClients.For;
    }

    public DocumentProcessor.ScanResult RunOnce(BDCOptions opts, LicenseResult license)
    {
        var result = new DocumentProcessor.ScanResult();
        foreach (var flow in opts.Flows.Where(f => f.Enabled))
        {
            try { result += RunFlow(opts, flow, license); }
            catch (Exception ex)
            {
                WarnOnce(flow.Id, $"flow {flow.DisplayName}: {ex.Message}");
                result += new DocumentProcessor.ScanResult(0, 0, 1);
            }
        }
        return result;
    }

    private DocumentProcessor.ScanResult RunFlow(BDCOptions opts, FlowOptions flow, LicenseResult license)
    {
        if (flow.Source.Kind != ConnectionKinds.DocuWare)
        { WarnOnce(flow.Id, $"flow {flow.DisplayName}: source kind '{flow.Source.Kind}' is not known to this version of Hive BDC."); return new(0, 0, 0); }
        if (!license.HasFeature(LicenseFeatures.SharePoint))
        { WarnOnce(flow.Id, $"flow {flow.DisplayName}: the installed licence has no 'sharepoint' feature, which DocuWare import needs."); return new(0, 0, 0); }
        var connection = opts.Connection(flow.Source.Connection);
        var problems = connection?.Problems() ?? new[] { $"The connection '{flow.Source.Connection}' is not set up (Settings > Connections)." };
        string cabinet = flow.Source.Setting("FileCabinet"), readyField = flow.Source.Setting("ReadyField"),
               readyValue = flow.Source.Setting("ReadyValue"), doneValue = flow.Source.Setting("DoneValue");
        string folder = flow.Targets.FirstOrDefault(t => t.Enabled && t.Kind == "folder")?.Setting("Folder") ?? "";
        if (cabinet.Length == 0 || readyField.Length == 0 || readyValue.Length == 0 || doneValue.Length == 0)
            problems = problems.Append("Set the file cabinet, the field and value that mark a document ready, and the value written once it is imported.").ToList();
        if (folder.Length == 0) problems = problems.Append("Set the folder the ERP reads.").ToList();
        if (flow.Produce.Columns.Count == 0) problems = problems.Append("Add at least one CSV column.").ToList();
        if (problems.Count > 0) { WarnOnce(flow.Id, $"flow {flow.DisplayName}: " + string.Join(" ", problems)); return new(0, 0, 0); }
        _warned.RemoveWhere(w => w.StartsWith(flow.Id + "|", StringComparison.Ordinal));

        var client = _clients(connection!);
        var ids = client.QueryIdsAsync(cabinet, readyField, readyValue, RecordsPerRun).GetAwaiter().GetResult();
        var result = new DocumentProcessor.ScanResult();
        foreach (var id in ids)
        {
            string key = $"{flow.Id}|{cabinet}|{id}";
            if (_failedUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until) continue;
            result += ImportOne(flow, client, cabinet, id, readyField, doneValue, folder, key);
        }
        return result;
    }

    private DocumentProcessor.ScanResult ImportOne(FlowOptions flow, DocuWareClient client, string cabinet, string id,
        string readyField, string doneValue, string folder, string key)
    {
        string input = $"DocuWare document {id}";
        bool written = _state?.DeliveryRemoteId(key, "folder") is not null;
        try
        {
            if (!written)
            {
                Note(id, input, EventSteps.Received, EventStates.Ok, $"Found document {id} in DocuWare, ready for flow \"{flow.DisplayName}\".", flow);
                var record = client.RecordAsync(cabinet, id).GetAwaiter().GetResult();
                var culture = CultureFor(flow.Produce.Setting("Culture", "de-DE"));
                string delimiter = flow.Produce.Setting("Delimiter", ";");
                string rowsFrom = flow.Produce.Setting("RowsFrom");
                var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string? filePath = null;
                if (string.Equals(flow.Targets.First(t => t.Kind == "folder").Setting("SaveFile"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    var (content, name, _) = client.DownloadAsync(cabinet, id).GetAwaiter().GetResult();
                    string ext = Path.GetExtension(name);
                    filePath = Path.Combine(folder, CsvFile.SafeFileName($"DW{id}{(ext.Length > 0 ? ext : ".pdf")}"));
                    CsvFile.WriteAtomic(filePath, content);
                }
                extra["FilePath"] = filePath ?? "";

                var problems = new List<string>();
                string text = CsvFile.Build(record, flow.Produce.Columns, rowsFrom.Length > 0 ? rowsFrom : null, delimiter, culture, extra, problems);
                if (problems.Count > 0)
                {
                    if (filePath is not null) TryDelete(filePath);
                    throw new InvalidOperationException(string.Join(" ", problems.Distinct()) + " Correct the columns of the flow in Settings > Imports.");
                }
                string fileName = CsvFile.SafeFileName(CsvFile.Resolve(flow.Produce.Setting("FileName", "{DWDOCID}.csv"), record, null,
                    new Dictionary<string, object?>(), culture, extra, "file name", null));
                if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) fileName += ".csv";
                string csvPath = Path.Combine(folder, fileName);
                CsvFile.WriteAtomic(csvPath, Utf8WithBom.GetPreamble().Concat(Utf8WithBom.GetBytes(text)).ToArray());
                int rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
                _state?.RecordDelivery(key, "folder", "folder", csvPath, id);
                Note(id, input, EventSteps.Delivered, EventStates.Ok,
                    $"Wrote {fileName} with {rows} row(s) to {folder}" + (filePath is null ? "." : $" and the file as {Path.GetFileName(filePath)}."), flow, "folder");
            }

            client.SetFieldAsync(cabinet, id, readyField, doneValue).GetAwaiter().GetResult();
            Note(id, input, EventSteps.Done, EventStates.Ok, $"Marked {readyField} = {doneValue} in DocuWare, so it is not imported again.", flow);
            _failedUntil.Remove(key);
            return new(1, 0, 0);
        }
        catch (DocuWareException ex) when (ex.RetryLater)
        {
            Note(id, input, EventSteps.Waiting, EventStates.Waiting, $"DocuWare: {ex.Message} Hive BDC tries again on the next scan.", flow);
            return new(0, 0, 1);
        }
        catch (Exception ex)
        {
            _failedUntil[key] = DateTime.UtcNow.Add(FailedPause);
            _log.LogError("[import] {Flow}: document {Id} failed - {Message}", flow.DisplayName, id, ex.Message);
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