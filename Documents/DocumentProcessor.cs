using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Core folder-scan logic, shared by the console (--once) and the Windows service Worker.
/// One scan = every folder any document type's company entry points at, each scanned once.
/// For each {documentId}_{seq}.json (+ sibling .pdf) in a folder:
///   1. parse the envelope, resolve its documentType (registering an unknown one) and the
///      company entry that applies (by the JSON's dataAreaId, else the folder's),
///   2. check the type is enabled and the license carries the handler's feature,
///   3. let the type's handler produce the output file,
///   4. deliver: SharePoint upload, then mail, as the type and the entry allow,
///   5. meter it in the ledger and move the inputs to processed\.
/// Any failure in 3 or 4 throws, which routes the pair to error\ for a later retry;
/// Missing license features, unresolved routes, incomplete input, and unavailable required
/// delivery clients HOLD the pair in the input folder for a later scan.
/// </summary>
public sealed partial class DocumentProcessor
{
    private readonly ILogger _log;
    private readonly DocumentTypeRegistry _registry;
    private readonly IStateStore? _ledger;
    private readonly ConnectorRegistry _connectors;

    /// <summary>Type ids already warned about for a missing license feature, so a held folder does not repeat the warning every poll.</summary>
    private readonly HashSet<string> _featureWarned = new(StringComparer.OrdinalIgnoreCase);
    // Pairs currently held (json path -> reason), so a hold is warned once, not every poll.
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pendingLoggedUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PendingReminder = TimeSpan.FromMinutes(15);

    /// <summary>What the history already knows about an input that is being worked on (key: input path).</summary>
    private sealed class HistoryState
    {
        public string? LastWaiting;
        public readonly HashSet<string> ReadLines = new(StringComparer.Ordinal);
        public string? DocumentNumber;
        public string? DocumentType;
        public string? DataAreaId;
        public bool TargetFailed;
        public readonly HashSet<string> DeliveredTargets = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, HistoryState> _history = new(StringComparer.OrdinalIgnoreCase);

    public DocumentProcessor(ILogger log, DocumentTypeRegistry registry, IStateStore? ledger, ConnectorRegistry? connectors = null)
    {
        _log = log;
        _registry = registry;
        _ledger = ledger;
        _connectors = connectors ?? ConnectorRegistry.BuiltIn();
    }

    /// <summary>Outcome of one scan: converted+delivered, routed to error\, and held for a missing license feature.</summary>
    public readonly record struct ScanResult(int Processed, int Errors, int Held)
    {
        public static ScanResult operator +(ScanResult a, ScanResult b)
            => new(a.Processed + b.Processed, a.Errors + b.Errors, a.Held + b.Held);
    }

    /// <summary>Scan every configured folder once. The shared <paramref name="uploader"/> is built from the shared auth.</summary>
    public ScanResult ScanOnce(BDCOptions opts, LicenseResult license, SharePointUploader? uploader, MailSender? mailer)
    {
        var result = new ScanResult();
        foreach (var folder in opts.ScanFolders())
        {
            try { result += ScanFolder(opts, folder, license, uploader, mailer); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Cannot scan {Folder}; continuing with other folders.", folder.Path);
                result += new ScanResult(0, 1, 0);
            }
        }
        return result;
    }

    private ScanResult ScanFolder(BDCOptions opts, ScanFolder folder, LicenseResult license,
        SharePointUploader? uploader, MailSender? mailer)
    {
        var result = new ScanResult();
        string tag = FolderTag(folder);

        try { Directory.CreateDirectory(folder.Path); }
        catch (Exception ex)
        {
            _log.LogError("scan [{Tag}] {Input}: folder is not usable — {Message}", tag, folder.Path, ex.Message);
            return new ScanResult(0, 1, 0);
        }

        if (folder.AcceptsWord)
            result += ScanWordDocuments(opts, folder, license, uploader, mailer);

        var jsonFiles = Directory.EnumerateFiles(folder.Path, "*.json", SearchOption.TopDirectoryOnly)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

        // Make each scan observable: when there is work, say how much and from where; when
        // idle, keep it at Debug so a healthy service does not spam the file every poll.
        if (jsonFiles.Count > 0)
            _log.LogInformation("scan [{Tag}] {Input}: {Count} document JSON file(s) to process.", tag, folder.Path, jsonFiles.Count);
        else
            _log.LogDebug("scan [{Tag}] {Input}: no JSON files.", tag, folder.Path);

        foreach (var jsonPath in jsonFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(jsonPath);
            var pdfPath = Path.Combine(folder.Path, baseName + ".pdf");
            var documentId = baseName.Split('_', 2)[0];

            if (_pending.ContainsKey(jsonPath))
                _log.LogDebug("[{Tag}] re-checking held {File}", tag, Path.GetFileName(jsonPath));
            else
                _log.LogInformation("[{Tag}] processing {File}", tag, Path.GetFileName(jsonPath));
            NoteReceived(jsonPath, documentId, folder.Path);

            try
            {
                result += ProcessPair(opts, folder, license, jsonPath, pdfPath, documentId, uploader, mailer);
                _pending.Remove(jsonPath);
                _pendingLoggedUtc.Remove(jsonPath);
            }
            catch (PendingDocumentException ex)
            {
                result += new ScanResult(0, 0, 1);
                // First time (or a new reason): a warning. Then a reminder every 15 minutes, so a
                // document stuck for hours is still named in the log, without a line every poll.
                bool known = _pending.TryGetValue(jsonPath, out var reason) && reason == ex.Message;
                if (!known)
                {
                    _log.LogWarning("{Base}: pending: {Reason}", baseName, ex.Message);
                    _pendingLoggedUtc[jsonPath] = DateTime.UtcNow;
                }
                else if (!_pendingLoggedUtc.TryGetValue(jsonPath, out var last) || DateTime.UtcNow - last >= PendingReminder)
                {
                    _log.LogInformation("{Base}: still pending: {Reason}", baseName, ex.Message);
                    _pendingLoggedUtc[jsonPath] = DateTime.UtcNow;
                }
                else
                    _log.LogDebug("{Base}: still pending: {Reason}", baseName, ex.Message);
                _pending[jsonPath] = ex.Message;
                Note(jsonPath, documentId, EventSteps.Waiting, EventStates.Waiting, ex.Message);
            }
            catch (Exception ex)
            {
                _pending.Remove(jsonPath);
                _pendingLoggedUtc.Remove(jsonPath);
                result += new ScanResult(0, 1, 0);
                _log.LogError(ex, "{Base}: failed - {Message}", baseName, ex.Message);
                NoteFailed(jsonPath, documentId, ex.Message);
                try
                {
                    MoveToFolder(jsonPath, folder.ErrorDir);
                    if (File.Exists(pdfPath)) MoveToFolder(pdfPath, folder.ErrorDir);
                }
                catch (Exception archiveError)
                {
                    _log.LogError(archiveError, "{Base}: could not quarantine inputs; continuing with other documents.", baseName);
                }
            }
        }

        return result;
    }

    private ScanResult ProcessPair(BDCOptions opts, ScanFolder folder, LicenseResult license,
        string jsonPath, string pdfPath, string documentId, SharePointUploader? uploader, MailSender? mailer, string source = "json",
        string? inputPath = null)
    {
        string input = inputPath ?? jsonPath;
        // One parse per pair: the DTO and the SharePoint column templates both read this document.
        using var json = ReadDocument(jsonPath);
        var data = AxDocument.FromElement(json.RootElement);
        string tag = FolderTag(folder);

        void Warn(string m) => _log.LogWarning("{Id}: {Message}", documentId, m);

        // ── 1. which document type, and which company entry? ──────────────
        // The JSON's documentType wins. Without one, a folder used by exactly one type
        // implies that type; otherwise it is a pre-2.0 export, i.e. a sales invoice.
        string? typeId = data.DocumentType;
        if (string.IsNullOrWhiteSpace(typeId))
        {
            typeId = folder.ImpliedTypeId ?? DocumentTypeCatalog.DefaultId;
            _log.LogInformation("[{Tag}] {Id}: JSON carries no documentType, treated as {Type} ({Why}).",
                tag, documentId, typeId, folder.ImpliedTypeId is null ? "pre-2.0 export" : "implied by the folder");
        }
        else if (folder.ImpliedTypeId is not null && !DocumentTypeCatalog.SameId(folder.ImpliedTypeId, typeId))
        {
            _log.LogWarning("[{Tag}] {Id}: JSON says documentType={Json} but it was dropped into the {Folder} folder; the JSON wins.",
                tag, documentId, typeId, folder.ImpliedTypeId);
        }

        // The company: the JSON's dataAreaId, else the one company the folder serves. Most
        // exports carry no dataAreaId, and the folder is then the only reliable hint: a pair
        // from company B's folder must not borrow company A's entry just because its
        // document type happens to be routed for A only.
        string? company = !string.IsNullOrWhiteSpace(data.DataAreaId) ? data.DataAreaId.Trim() : FolderCompany(folder);

        // The folder's entry for that company seeds a newly discovered type.
        var seed = PickRoute(folder.Routes.Select(r => r.Route), company);
        var type = _registry.Resolve(opts, typeId, data.Party, seed, out _);
        if (!type.Discovered && !opts.DocumentTypes.Contains(type))
            throw new InvalidOperationException(
                $"Document type '{type.Id}' was removed in Settings > Document types. Add it again (Add type, ID {type.Id}), then drop the pair back into the input folder.");
        var route = PickRoute(folder.Routes.Where(r => r.Type.Matches(type.Id)).Select(r => r.Route), company)
                    ?? PickRoute(type.Companies, company)
                    ?? (type.Discovered && seed is not null ? _registry.AddCompany(type, seed) : null)
                    ?? throw new PendingDocumentException(
                        $"Document type '{type.Id}' has no route for company '{company ?? "(not in the JSON, folder serves several)"}' " +
                        $"(Settings > Document types > {type.EffectiveDisplayName} > Companies). Add that company; the pair remains pending.");
        var documentNumber = data.ResolveNumber(documentId);
        string area = company ?? route.DataAreaId;

        _log.LogInformation("[{Tag}] {Id}: parsed — documentType={Type} ({Handler}) company={Area} number={No}{EInv}",
            tag, documentId, type.Id, type.Handler, area, documentNumber,
            type.Handler == DocumentHandlerKind.EInvoice
                ? " eInvoiceType=" + (string.IsNullOrWhiteSpace(data.EInvoiceType) ? "(default->xrechnung)" : data.EInvoiceType)
                : "");
        var known = HistoryOf(input);
        known.DocumentNumber = documentNumber;
        known.DocumentType = type.Id;
        known.DataAreaId = area;
        Note(input, documentId, EventSteps.Read, EventStates.Ok,
            $"Read as {type.EffectiveDisplayName} {documentNumber} for company {area}" +
            (type.Handler == DocumentHandlerKind.EInvoice ? $", e-invoice format {(data.IsZugferd ? "ZUGFeRD" : "XRechnung")}." : "."));

        // ── 2. may it be processed? ───────────────────────────────────────
        if (!type.Enabled)
            throw new InvalidOperationException(
                $"Document type '{type.Id}' is disabled in Settings > Document types. Enable it, then drop the pair back into the input folder.");

        var handler = DocumentHandlers.For(type.Handler);
        if (!license.HasFeature(handler.RequiredFeature))
        {
            if (_featureWarned.Add(type.Id))
                _log.LogWarning(
                    "[{Tag}] {Id}: the installed license has no '{Feature}' feature, which document type '{Type}' needs. " +
                    "The pair stays in the input folder and is processed as soon as a license with that feature is installed.",
                    tag, documentId, handler.RequiredFeature, type.Id);
            else
                _log.LogDebug("[{Tag}] {Id}: held — license lacks '{Feature}'.", tag, documentId, handler.RequiredFeature);
            Note(input, documentId, EventSteps.Waiting, EventStates.Waiting,
                $"Waiting for a licence with the '{handler.RequiredFeature}' feature, which {type.EffectiveDisplayName} needs. " +
                "It is processed as soon as such a licence is installed.");
            return new ScanResult(0, 0, 1);
        }

        // ── 3. produce the output file ────────────────────────────────────
        // Missing clients are a recoverable configuration problem, not successful
        // delivery. Hold before any side effects so repairing settings resumes work.
        var deliveries = ResolveTargets(opts, route, type, uploader, mailer);
        foreach (var (options, target) in deliveries)
        {
            if (target.RequiredFeature is { } feature && !license.HasFeature(feature))
                throw new PendingDocumentException(
                    $"The {TargetLabel(options.Kind)} target needs the '{feature}' licence feature, which the installed licence does not have.");
            if (target.Unavailable(options) is { } unavailable)
                throw new PendingDocumentException(unavailable);
        }
        if ((type.Handler == DocumentHandlerKind.Pdf || data.IsZugferd) && !File.Exists(pdfPath))
            throw new PendingDocumentException("Waiting for the sibling PDF.");
        if (File.Exists(pdfPath)) EnsureReadable(pdfPath);
        var ctx = new DocumentContext(route, type, data, jsonPath, pdfPath, documentId, documentNumber,
            route.OutputDirFor(type), Warn, _log);
        var output = handler.Produce(ctx);
        var produced = DocumentFile.FromPath(output.File);
        Note(input, documentId, EventSteps.Produced, EventStates.Ok,
            $"Produced {FormatLabel(output.Format)} {produced.Name} ({SizeLabel(new FileInfo(output.File).Length)}).");

        // ── 4. deliver ────────────────────────────────────────────────────
        // Every target of the company entry gets the file, mail last. A failure throws, routes
        // the pair to error\, and the next drop-in retry regenerates cleanly: a target that
        // replaces by name simply runs again, any other target is handed the id of its earlier
        // delivery from the ledger and updates that document.
        if (!route.SharePoint.Enabled)
            _log.LogInformation("[{Tag}] {Id}: SharePoint upload skipped: SharePoint is off for {Type} / company {Area} (Settings > Document types).",
                tag, documentId, type.Id, route.DataAreaId);

        string? targetSite = null;
        string deliveryKey = $"{area}|{type.Id}|{documentId}|{Path.GetFileName(output.File)}";
        foreach (var (options, target) in deliveries)
        {
            if (known.DeliveredTargets.Contains(options.Key)) continue;
            string? previous = target.ReplacesOnRepeat ? null : _ledger?.DeliveryRemoteId(deliveryKey, options.Key);
            DeliveryResult delivered;
            try
            {
                delivered = target.Deliver(new DeliveryContext(route, type, options, data, json.RootElement,
                    documentId, documentNumber, produced, previous, tag, Warn, _log));
            }
            catch (DocuWareException ex) when (ex.RetryLater)
            {
                throw new PendingDocumentException($"{TargetLabel(options.Kind)}: {ex.Message} Hive BDC tries again on the next scan.");
            }
            catch (Exception ex)
            {
                known.TargetFailed = true;
                Note(input, documentId, EventSteps.Delivered, EventStates.Failed, $"{TargetLabel(options.Kind)}: {ex.Message}", options.Kind);
                throw;
            }
            known.DeliveredTargets.Add(options.Key);
            Note(input, documentId, EventSteps.Delivered, EventStates.Ok,
                delivered.Summary ?? $"Delivered to {TargetLabel(options.Kind)}.", options.Kind);
            if (options.Kind == TargetKinds.Mail) continue;
            targetSite ??= delivered.Location;
            _ledger?.RecordDelivery(deliveryKey, options.Key, options.Kind, delivered.Location, delivered.RemoteId);
        }

        if (!type.Mail)
            _log.LogInformation("[{Tag}] {Id}: mail skipped: mail is off for document type {Type}.", tag, documentId, type.Id);

        // ── 5. meter + move ───────────────────────────────────────────────
        // Best-effort: a metering hiccup (including the size stat) must never route an
        // already-delivered document to error\.
        if (_ledger is not null)
        {
            try
            {
                _ledger.Record(documentId, type.Id, output.Format, output.File, targetSite,
                    new FileInfo(output.File).Length, area, source);
            }
            catch (Exception mex)
            {
                _log.LogWarning("{Id}: metering skipped — {Message}", documentId, mex.Message);
            }
        }

        if (File.Exists(pdfPath)) MoveToFolder(pdfPath, folder.ProcessedDir);
        MoveToFolder(jsonPath, folder.ProcessedDir);
        _log.LogInformation("[{Tag}] {Id}: done — inputs moved to {Processed}", tag, documentId, folder.ProcessedDir);
        Note(input, documentId, EventSteps.Done, EventStates.Ok, "Finished. The input files are in the processed folder.");
        return new ScanResult(1, 0, 0);
    }

    /// <summary>
    /// Puts the files of <paramref name="documentId"/> back from the error folders into their input folder, so the next
    /// scan processes them again: the JSON and PDF pair ({id}_*.json / .pdf) and a Word document with that id, whose
    /// error note is removed. Returns the file names moved.
    /// </summary>
    public static List<string> PutBackForRetry(BDCOptions opts, string documentId)
    {
        var moved = new List<string>();
        string id = documentId.Trim();
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return moved;
        foreach (var folder in opts.ScanFolders())
        {
            if (!Directory.Exists(folder.ErrorDir)) continue;
            foreach (var file in Directory.EnumerateFiles(folder.ErrorDir))
            {
                string name = Path.GetFileName(file);
                string ext = Path.GetExtension(name).ToLowerInvariant();
                bool pair = (ext is ".json" or ".pdf") && name.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase);
                bool word = ext == ".docx" && string.Equals(WordDocumentId(file), id, StringComparison.OrdinalIgnoreCase);
                if (!pair && !word) continue;
                string target = Path.Combine(folder.Path, name);
                if (File.Exists(target)) continue;
                File.Move(file, target);
                moved.Add(name);
                if (word)
                {
                    try { File.Delete(Path.Combine(folder.ErrorDir, name + ".error.txt")); } catch (IOException) { }
                }
            }
        }
        return moved;
    }

    /// <summary>The company a folder serves, when it serves exactly one; else null.</summary>
    private static string? FolderCompany(ScanFolder folder)
    {
        var areas = folder.Routes.Select(r => r.Route.DataAreaId?.Trim() ?? "")
            .Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return areas.Count == 1 ? areas[0] : null;
    }

    /// <summary>Match an explicit company exactly; infer only an unambiguous company.</summary>
    private static DocumentRoute? PickRoute(IEnumerable<DocumentRoute> routes, string? dataAreaId)
    {
        var list = routes.ToList();
        if (list.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(dataAreaId))
        {
            var match = list.FirstOrDefault(r => string.Equals(r.DataAreaId, dataAreaId.Trim(), StringComparison.OrdinalIgnoreCase));
            return match;
        }
        return list.Select(r => r.DataAreaId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? list[0] : null;
    }

    private sealed class PendingDocumentException(string message) : Exception(message);

    private static JsonDocument ReadDocument(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return JsonDocument.Parse(stream, AxDocument.DocumentOptions);
        }
        catch (IOException) { throw new PendingDocumentException("Input is still being written or temporarily unavailable."); }
        catch (JsonException) when (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(30))
        { throw new PendingDocumentException("Waiting for the JSON write to finish."); }
    }

    private static void EnsureReadable(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { throw new PendingDocumentException("PDF is still being written or temporarily unavailable."); }
    }

    /// <summary>
    /// The targets one document goes to: the entry's SharePoint block, its further targets, then
    /// mail when the type mails. A kind this version does not know holds the document with a
    /// reason, so a settings file from a newer version stops that entry only.
    /// </summary>
    private List<(TargetOptions Options, IDocumentTarget Target)> ResolveTargets(BDCOptions opts, DocumentRoute route,
        DocumentTypeOptions type, SharePointUploader? uploader, MailSender? mailer)
    {
        var runtime = new TargetRuntime(opts, uploader, mailer, _log);
        var wanted = route.EffectiveTargets().ToList();
        if (type.Mail) wanted.Add(new TargetOptions { Kind = TargetKinds.Mail });

        var list = new List<(TargetOptions, IDocumentTarget)>();
        foreach (var options in wanted)
        {
            var target = _connectors.Target(options.Kind, runtime)
                ?? throw new PendingDocumentException(
                    $"Document type '{type.Id}' / company '{route.DataAreaId}' has a target of kind '{options.Kind}', which this version of Hive BDC does not know. " +
                    "Update Hive BDC or remove that target from the company entry; the pair remains pending.");
            list.Add((options, target));
        }
        return list;
    }

    private HistoryState HistoryOf(string input)
    {
        if (!_history.TryGetValue(input, out var state)) _history[input] = state = new HistoryState();
        return state;
    }

    /// <summary>
    /// One line of the document's history. A waiting document is read again on every scan, so its read lines and an
    /// unchanged waiting reason are written once per arrival, not on every scan; the facts read
    /// from the document (number, type, company) are added to every later line of the same input.
    /// </summary>
    private void Note(string input, string documentId, string step, string state, string message, string? target = null)
    {
        if (_ledger is null) return;
        var known = HistoryOf(input);
        if (step == EventSteps.Read && !known.ReadLines.Add(message)) return;
        if (step == EventSteps.Waiting)
        {
            if (known.LastWaiting == message) return;
            known.LastWaiting = message;
        }
        _ledger.RecordEvent(new DocumentEvent(documentId, Path.GetFileName(input), step, state, message,
            known.DocumentNumber, known.DocumentType, known.DataAreaId, target));
        if (step is EventSteps.Done or EventSteps.Failed) _history.Remove(input);
    }

    /// <summary>The first line of an arrival; a document that is already being worked on (waiting) gets no second one.</summary>
    private void NoteReceived(string input, string documentId, string folder)
    {
        if (_ledger is null || _history.ContainsKey(input)) return;
        Note(input, documentId, EventSteps.Received, EventStates.Ok, $"Received {Path.GetFileName(input)} in {folder}.");
    }

    private void NoteFailed(string input, string documentId, string reason)
    {
        bool afterTarget = _history.TryGetValue(input, out var known) && known.TargetFailed;
        Note(input, documentId, EventSteps.Failed, EventStates.Failed, afterTarget
            ? "Stopped after the failed delivery above. The input files are in the error folder; put them back into the input folder to retry. Targets that already received the document are not sent a second copy."
            : reason + " The input files are in the error folder; put them back into the input folder to retry.");
    }

    internal static string FormatLabel(string format) => format switch
    {
        "xrechnung" => "XRechnung XML",
        "zugferd" => "ZUGFeRD PDF",
        "pdf" => "PDF",
        _ => format,
    };

    internal static string TargetLabel(string kind) => kind switch
    {
        TargetKinds.SharePoint => "SharePoint",
        TargetKinds.Mail => "Mail",
        ConnectionKinds.DocuWare => "DocuWare",
        _ => kind.Length == 0 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    internal static string SizeLabel(long bytes) => bytes < 1024 ? $"{bytes} bytes"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    /// <summary>Log form of mail addresses: first character of the local part and the domain ("m***@contoso.com").</summary>
    internal static string MaskAddresses(IEnumerable<string> addresses) => string.Join(", ", addresses.Select(MaskAddress));

    internal static string MaskAddress(string address)
    {
        int at = address.IndexOf('@');
        return at <= 0 ? "***" : address[0] + "***" + address[at..];
    }

    /// <summary>Short log tag for a folder: its implied type, or "shared" when several types use it.</summary>
    private static string FolderTag(ScanFolder folder) => folder.ImpliedTypeId ?? "shared";

    private static void MoveToFolder(string file, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, Path.GetFileName(file));
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(file, dest);
    }
}
