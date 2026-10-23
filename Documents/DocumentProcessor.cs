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
public sealed class DocumentProcessor
{
    private readonly ILogger _log;
    private readonly DocumentTypeRegistry _registry;
    private readonly ExportLedger? _ledger;

    /// <summary>Type ids already warned about for a missing license feature, so a held folder does not repeat the warning every poll.</summary>
    private readonly HashSet<string> _featureWarned = new(StringComparer.OrdinalIgnoreCase);
    // Pairs currently held (json path -> reason), so a hold is warned once, not every poll.
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pendingLoggedUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PendingReminder = TimeSpan.FromMinutes(15);

    public DocumentProcessor(ILogger log, DocumentTypeRegistry registry, ExportLedger? ledger)
    {
        _log = log;
        _registry = registry;
        _ledger = ledger;
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
            }
            catch (Exception ex)
            {
                _pending.Remove(jsonPath);
                _pendingLoggedUtc.Remove(jsonPath);
                result += new ScanResult(0, 1, 0);
                _log.LogError(ex, "{Base}: failed - {Message}", baseName, ex.Message);
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
        string jsonPath, string pdfPath, string documentId, SharePointUploader? uploader, MailSender? mailer)
    {
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
            return new ScanResult(0, 0, 1);
        }

        // ── 3. produce the output file ────────────────────────────────────
        // Missing clients are a recoverable configuration problem, not successful
        // delivery. Hold before any side effects so repairing settings resumes work.
        var spUploader = route.SharePoint.Enabled
            ? uploader ?? throw new PendingDocumentException("SharePoint is enabled but its connection is unavailable.")
            : null;
        if (type.Mail && opts.Mail.Enabled && mailer is null)
            throw new PendingDocumentException("Mail is enabled but its connection is unavailable.");
        if ((type.Handler == DocumentHandlerKind.Pdf || data.IsZugferd) && !File.Exists(pdfPath))
            throw new PendingDocumentException("Waiting for the sibling PDF.");
        if (File.Exists(pdfPath)) EnsureReadable(pdfPath);
        var ctx = new DocumentContext(route, type, data, jsonPath, pdfPath, documentId, documentNumber,
            route.OutputDirFor(type), Warn, _log);
        var output = handler.Produce(ctx);

        // ── 4a. SharePoint ────────────────────────────────────────────────
        // Upload BEFORE the inputs move to processed\ — an upload failure throws, routes the
        // pair to error\, and the next drop-in retry regenerates cleanly.
        string? targetSite = null;
        if (spUploader is null)
        {
            _log.LogInformation("[{Tag}] {Id}: SharePoint upload skipped: SharePoint is off for {Type} / company {Area} (Settings > Document types).",
                tag, documentId, type.Id, route.DataAreaId);
        }
        else
        {
            var sp = route.SharePoint;
            var target = sp.ResolveTarget(out var reason);
            if (target is null)
                throw new InvalidOperationException($"SharePoint upload for '{type.Id}' / company '{route.DataAreaId}': {reason} " +
                    "Set the site URL and library on that company entry in Settings > Document types.");

            var fields = SharePointFieldTemplate.Resolve(json.RootElement, sp.Fields, Warn);
            var empty = sp.Fields.Keys.Where(k => !fields.ContainsKey(k)).ToList();
            if (empty.Count > 0)
                _log.LogInformation("[sharepoint] {Id}: column(s) left empty because the document has no value for them: {Columns}",
                    documentId, string.Join(", ", empty));
            spUploader.Upload(output.File, target, fields);
            targetSite = target.SiteUrl;
            _log.LogInformation("[sharepoint] {Id} ({Type}, {Area}) -> {Site}/{Library}/{Folder}{Name} ({Fields} column(s) requested)",
                documentId, type.Id, route.DataAreaId, target.SiteUrl, target.Library,
                string.IsNullOrWhiteSpace(target.Folder) ? "" : target.Folder.TrimEnd('/') + "/",
                Path.GetFileName(output.File), fields.Count);
        }

        // ── 4b. mail ──────────────────────────────────────────────────────
        // Failures throw, so the pair lands in error\ and can be retried by dropping it back
        // in, rather than being metered as a finished export that nobody ever received.
        if (type.Mail)
            SendDocumentMail(mailer, type, data, documentId, documentNumber, output.File, tag);
        else
            _log.LogInformation("[{Tag}] {Id}: mail skipped: mail is off for document type {Type}.", tag, documentId, type.Id);

        // ── 5. meter + move ───────────────────────────────────────────────
        // Best-effort: a metering hiccup (including the size stat) must never route an
        // already-delivered document to error\.
        if (_ledger is not null)
        {
            try
            {
                _ledger.Record(documentId, type.Id, output.Format, output.File, targetSite,
                    new FileInfo(output.File).Length, area);
            }
            catch (Exception mex)
            {
                _log.LogWarning("{Id}: metering skipped — {Message}", documentId, mex.Message);
            }
        }

        if (File.Exists(pdfPath)) MoveToFolder(pdfPath, folder.ProcessedDir);
        MoveToFolder(jsonPath, folder.ProcessedDir);
        _log.LogInformation("[{Tag}] {Id}: done — inputs moved to {Processed}", tag, documentId, folder.ProcessedDir);
        return new ScanResult(1, 0, 0);
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
    /// Mail the produced file, when mail is configured and the document names a recipient.
    ///
    /// The environment stamped by the ERP decides who receives it: a production document goes
    /// to the customer/vendor, anything else (including a document whose JSON carries no
    /// environment) goes to the redirect list from the settings. A document with no recipient
    /// at all is not an error — plenty are printed or posted rather than mailed — so it is
    /// logged and skipped.
    /// </summary>
    private void SendDocumentMail(MailSender? mailer, DocumentTypeOptions type, AxDocument data,
        string documentId, string documentNumber, string outFile, string tag)
    {
        if (mailer is null)
        {
            _log.LogInformation("[{Tag}] {Id}: mail skipped: document mail is not enabled.", tag, documentId);
            return;
        }

        var plan = mailer.PlanRecipients(data.Environment, data.RecipientEmail, data.RecipientEmailCc);
        string partyLabel = type.Party == DocumentParty.Vendor ? "vendor" : "customer";

        if (plan.Redirected && string.IsNullOrWhiteSpace(data.Environment))
            _log.LogWarning(
                "[{Tag}] {Id}: the JSON carries no 'environment' — treating it as NOT production and redirecting the mail. " +
                "Update the AX export (BDCEnvironmentHelper) so production documents reach the {Party}.", tag, documentId, partyLabel);

        if (plan.To.Count == 0)
        {
            if (plan.Redirected)
                throw new InvalidOperationException(
                    $"Mail is enabled and this document came from a '{plan.Environment}' system, but no redirect " +
                    "recipients are configured. Add them in Settings > Connections > Document mail > Test recipients.");

            _log.LogWarning("[{Tag}] {Id}: no recipient e-mail in the document — mail skipped.", tag, documentId);
            return;
        }

        if (string.IsNullOrWhiteSpace(data.EmailBody))
            _log.LogWarning("[{Tag}] {Id}: the JSON has no emailBody — sending with an empty body.", tag, documentId);

        string subject = MailSender.BuildSubject(data.EmailSubject, type.EffectiveDisplayName, documentNumber, plan);
        var body = MailSender.BuildBody(data.EmailBody, type.EffectiveDisplayName, documentNumber, partyLabel, plan);

        try
        {
            mailer.Send(plan, subject, body, outFile);
        }
        catch (Exception ex)
        {
            // Production and upload already succeeded, so name the document and the step
            // rather than letting the bare Graph error stand on its own.
            throw new InvalidOperationException(
                $"The {type.EffectiveDisplayName} was produced and uploaded, but mailing it failed: {ex.Message} " +
                "Fix the mail settings, then drop the pair back into the input folder to retry.", ex);
        }

        if (plan.Redirected)
            _log.LogInformation(
                "[mail] {Id} ({Type}) REDIRECTED — environment={Env}, sent to {To} instead of {Original}; {Kind} body, attachment {File}",
                documentId, type.Id, plan.Environment, MaskAddresses(plan.To),
                plan.OriginalTo.Count > 0 ? MaskAddresses(plan.OriginalTo) : "(none)", body.IsHtml ? "HTML" : "text", Path.GetFileName(outFile));
        else
            _log.LogInformation(
                "[mail] {Id} ({Type}) -> {To}{Cc}; from {From}, {Kind} body, attachment {File}",
                documentId, type.Id, MaskAddresses(plan.To),
                plan.Cc.Count > 0 ? " cc " + MaskAddresses(plan.Cc) : "",
                mailer.From, body.IsHtml ? "HTML" : "text", Path.GetFileName(outFile));
    }

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
