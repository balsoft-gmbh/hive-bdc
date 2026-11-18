using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Company entries whose documents arrive in Azure Blob Storage (the route's Source, Kind "blob"). Before each scan,
/// every new JSON blob under the source prefix, with its PDF, is downloaded into the entry's input folder as
/// {id}_blob.json and {id}_blob.pdf, where the normal pipeline processes it. A note per pair in the folder's
/// .hive-sources records where it came from; once the pipeline has moved the pair to processed or error, the blobs
/// are moved to the processed or error prefix, as the F&amp;O receive classes do. Restarts lose nothing: the notes
/// are files.
/// </summary>
public sealed partial class BlobStager
{
    private const string NotesFolder = ".hive-sources";
    private const string StagedSuffix = "_blob";

    private readonly ILogger _log;
    private readonly IStateStore? _state;
    private readonly Func<ConnectionOptions, string, IBlobStore> _stores;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public BlobStager(ILogger log, IStateStore? state, Func<ConnectionOptions, string, IBlobStore>? stores = null)
    {
        _log = log;
        _state = state;
        _stores = stores ?? ((c, container) => AzureBlobStore.For(c, container));
    }

    private sealed record Note(string Connection, string Container, string Json, string? Pdf, string ProcessedPrefix, string ErrorPrefix);

    [GeneratedRegex(@"[^A-Za-z0-9\-]")]
    private static partial Regex UnsafeIdChars();

    /// <summary>The staged JSON of an export id, as the pipeline sees it in the input folder.</summary>
    public static string StagedJson(string id) => id + StagedSuffix + ".json";

    /// <summary>
    /// Stages only the first waiting export of one document type and company (a test run, failure cases 56 and 59) and
    /// returns its id, or null with the reason when none can be staged. Exports of other types or companies in the same
    /// container stay untouched.
    /// </summary>
    public string? StageOne(BDCOptions opts, DocumentRoute route, string documentType, string company, out string? problem)
    {
        problem = null;
        var source = route.Source;
        if (source is not { Kind: "blob" } || opts.Connection(source.Connection) is null || source.Setting("Container").Length == 0)
        {
            problem = "The Blob source of this company has no connection or container yet.";
            return null;
        }
        Settle(opts, route);
        return Stage(opts, route, limit: 1, accept: json => ExportIsFor(json, documentType, company)).FirstOrDefault();
    }

    /// <summary>
    /// Whether an export names this document type and company. A field the export does not carry does not exclude it;
    /// anything that is not a JSON object is not an export.
    /// </summary>
    public static bool ExportIsFor(byte[] json, string documentType, string company)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                string value = property.Value.GetString() ?? "";
                if (string.Equals(property.Name, "documentType", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length > 0 && !string.Equals(value, documentType, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if (string.Equals(property.Name, "dataAreaId", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length > 0 && !string.Equals(value, company, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Moves the blobs of the pairs the pipeline finished with, for one company, without staging new ones.</summary>
    public void SettleRoute(BDCOptions opts, DocumentRoute route) => Settle(opts, route);

    /// <summary>
    /// Forgets a staged export without touching its blobs: the local copies and the note go, the export stays where it
    /// is in the container and is taken again by the next run.
    /// </summary>
    public void Abandon(DocumentRoute route, string id)
    {
        foreach (var folder in new[] { route.Input, Path.Combine(route.Input, "error"), Path.Combine(route.Input, "processed") })
            foreach (var name in new[] { id + StagedSuffix + ".json", id + StagedSuffix + ".pdf" })
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) File.Delete(path);
            }
        string note = Path.Combine(route.Input, NotesFolder, id + ".json");
        if (File.Exists(note)) File.Delete(note);
        // The pipeline's last line speaks of the error folder; for a test run the export never left the container.
        _state?.RecordEvent(new DocumentEvent(id, StagedJson(id), EventSteps.Waiting, EventStates.Waiting,
            "Test run: nothing was moved, the export stays where it was in the container. The next test run or Start takes it again."));
        _log.LogInformation("[blob] test run of {Id} not settled; the export stays in the container.", id);
    }

    public void Run(BDCOptions opts)
    {
        foreach (var t in opts.DocumentTypes.Where(t => t.Enabled))
            foreach (var route in t.Companies.Where(r => r.IsOn && r.Source is { Kind: "blob" }))
            {
                try { Settle(opts, route); Stage(opts, route); }
                catch (Exception ex) { WarnOnce(route.Input, $"blob source for {route.Input}: {ex.Message}"); }
            }
    }

    private List<string> Stage(BDCOptions opts, DocumentRoute route, int limit = int.MaxValue, Func<byte[], bool>? accept = null)
    {
        var stagedIds = new List<string>();
        var source = route.Source!;
        var connection = opts.Connection(source.Connection);
        string container = source.Setting("Container");
        if (connection is null || container.Length == 0)
        { WarnOnce(route.Input, $"blob source for {route.Input}: set the connection and the container."); return stagedIds; }
        string prefix = source.Setting("Prefix");
        string processed = PrefixOr(source.Setting("ProcessedPrefix"), "processed/" + prefix);
        string error = PrefixOr(source.Setting("ErrorPrefix"), "error/" + prefix);
        var store = _stores(connection, container);
        string notes = Path.Combine(route.Input, NotesFolder);

        var names = store.List(prefix)
            .Where(n => !n.StartsWith(processed, StringComparison.Ordinal) && !n.StartsWith(error, StringComparison.Ordinal))
            .ToList();
        var all = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var name in names.Where(n => !n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            if (stagedIds.Count >= limit) break;
            string id = IdFor(name);
            string staged = id + StagedSuffix + ".json";
            bool known = File.Exists(Path.Combine(notes, id + ".json"));
            if (known && (File.Exists(Path.Combine(route.Input, staged)) || File.Exists(Path.Combine(route.Input, "processed", staged))
                          || File.Exists(Path.Combine(route.Input, "error", staged))))
                continue;

            byte[] json = store.Download(name);
            if (accept is not null && !accept(json)) continue;
            string? pdf = PdfFor(store, name, json, all, out var refused);
            if (refused is not null)
            {
                _state?.RecordEvent(new DocumentEvent(id, name, EventSteps.Waiting, EventStates.Waiting, refused));
                WarnOnce(route.Input + name, $"blob {name}: {refused}");
                continue;
            }

            Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, id + ".json"),
                JsonSerializer.Serialize(new Note(source.Connection, container, name, pdf, processed, error)));
            if (pdf is not null) CsvFile.WriteAtomic(Path.Combine(route.Input, id + StagedSuffix + ".pdf"), store.Download(pdf));
            CsvFile.WriteAtomic(Path.Combine(route.Input, staged), json);
            _log.LogInformation("[blob] {Container}/{Name} staged as {Id}{Suffix}.json{Pdf}", container, name, id, StagedSuffix,
                pdf is null ? "" : " with " + pdf);
            stagedIds.Add(id);
        }
        return stagedIds;
    }

    /// <summary>Moves the blobs of every pair the pipeline has finished with, and removes its note.</summary>
    private void Settle(BDCOptions opts, DocumentRoute route)
    {
        string notes = Path.Combine(route.Input, NotesFolder);
        if (!Directory.Exists(notes)) return;
        foreach (var file in Directory.EnumerateFiles(notes, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            string staged = id + StagedSuffix + ".json";
            bool done = File.Exists(Path.Combine(route.Input, "processed", staged));
            bool failed = File.Exists(Path.Combine(route.Input, "error", staged));
            if (!done && !failed) continue;

            var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(file))!;
            var connection = opts.Connection(note.Connection);
            if (connection is null) { WarnOnce(file, $"blob {note.Json}: connection '{note.Connection}' is gone; the blob stays where it is."); continue; }
            var store = _stores(connection, note.Container);
            string target = done ? note.ProcessedPrefix : note.ErrorPrefix;
            foreach (var name in new[] { note.Json, note.Pdf }.OfType<string>())
                if (store.Exists(name)) store.Move(name, target + LeafAfter(name));
            File.Delete(file);
            _log.LogInformation("[blob] {Container}/{Name} moved to {Target}", note.Container, note.Json, target);
        }
    }

    /// <summary>The PDF of a JSON blob: the blob the JSON names in pdfLink (<see cref="PdfLink"/>), else a blob with the same name plus .pdf.</summary>
    private static string? PdfFor(IBlobStore store, string name, byte[] json, HashSet<string> names, out string? refused)
    {
        string? linked = PdfLink.BlobName(store, json, out refused);
        if (linked is not null || refused is not null) return linked;
        string sibling = name + ".pdf";
        return names.Contains(sibling) ? sibling : null;
    }

    private static string IdFor(string name)
    {
        string leaf = name[(name.LastIndexOf('/') + 1)..];
        if (leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^5];
        string id = UnsafeIdChars().Replace(leaf, "-").Trim('-');
        return id.Length == 0 ? "blob" : id;
    }

    private static string LeafAfter(string name) => name[(name.LastIndexOf('/') + 1)..];

    private static string PrefixOr(string value, string fallback)
    {
        string p = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return p.Length == 0 || p.EndsWith('/') ? p : p + "/";
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key + "|" + message)) _log.LogWarning("[blob] {Message}", message);
    }
}

/// <summary>
/// The pdfLink an F&amp;O export puts into its JSON (BDCDocumentPdf.addPdfLink): the blob it names, but only when it lies in
/// the container the service was given, so a document cannot make the service fetch from any other address.
/// </summary>
public static class PdfLink
{
    /// <summary>The blob name inside <paramref name="store"/>'s container; null with no link; null and a reason when refused.</summary>
    public static string? BlobName(IBlobStore store, byte[] json, out string? refused)
    {
        refused = null;
        string? link = null;
        try
        {
            using var doc = JsonDocument.Parse(json, AxDocument.DocumentOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals("pdfLink", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        link = p.Value.GetString();
        }
        catch (JsonException) { }

        if (string.IsNullOrWhiteSpace(link)) return null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) { refused = $"pdfLink '{link}' is not an address."; return null; }
        string root = store.ContainerUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        string target = uri.GetLeftPart(UriPartial.Path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        { refused = $"pdfLink points outside the container {store.ContainerUri.GetLeftPart(UriPartial.Path)}, so the PDF is not fetched."; return null; }
        return Uri.UnescapeDataString(target[root.Length..]);
    }

    /// <summary>Whether the JSON names a pdfLink at all.</summary>
    public static bool Present(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, AxDocument.DocumentOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject()
                .Any(p => p.Name.Equals("pdfLink", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                          && !string.IsNullOrWhiteSpace(p.Value.GetString()));
        }
        catch (JsonException) { return false; }
    }
}