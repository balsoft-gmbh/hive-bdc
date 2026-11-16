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

    public void Run(BDCOptions opts)
    {
        foreach (var t in opts.DocumentTypes.Where(t => t.Enabled))
            foreach (var route in t.Companies.Where(r => r.Source is { Kind: "blob" }))
            {
                try { Settle(opts, route); Stage(opts, route); }
                catch (Exception ex) { WarnOnce(route.Input, $"blob source for {route.Input}: {ex.Message}"); }
            }
    }

    private void Stage(BDCOptions opts, DocumentRoute route)
    {
        var source = route.Source!;
        var connection = opts.Connection(source.Connection);
        string container = source.Setting("Container");
        if (connection is null || container.Length == 0)
        { WarnOnce(route.Input, $"blob source for {route.Input}: set the connection and the container."); return; }
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
            string id = IdFor(name);
            string staged = id + StagedSuffix + ".json";
            bool known = File.Exists(Path.Combine(notes, id + ".json"));
            if (known && (File.Exists(Path.Combine(route.Input, staged)) || File.Exists(Path.Combine(route.Input, "processed", staged))
                          || File.Exists(Path.Combine(route.Input, "error", staged))))
                continue;

            byte[] json = store.Download(name);
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
        }
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

    /// <summary>
    /// The PDF of a JSON blob: the blob the JSON names in pdfLink, when it lies in this container, else a blob with the
    /// same name plus .pdf. A pdfLink that points anywhere else is refused, so a document cannot make the service fetch
    /// from an address it was not given.
    /// </summary>
    private static string? PdfFor(IBlobStore store, string name, byte[] json, HashSet<string> names, out string? refused)
    {
        refused = null;
        string? link = null;
        try
        {
            using var doc = JsonDocument.Parse(json, AxDocument.DocumentOptions);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("pdfLink", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    link = p.Value.GetString();
        }
        catch (JsonException) { }

        if (!string.IsNullOrWhiteSpace(link))
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) { refused = $"pdfLink '{link}' is not an address."; return null; }
            string root = store.ContainerUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
            string target = uri.GetLeftPart(UriPartial.Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            { refused = $"pdfLink points outside the container {store.ContainerUri.GetLeftPart(UriPartial.Path)}, so the PDF is not fetched."; return null; }
            return Uri.UnescapeDataString(target[root.Length..]);
        }
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