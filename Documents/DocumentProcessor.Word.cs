using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Word input: a filled Hive BDC Word invoice in an input folder whose company entry takes Word becomes the same
/// JSON + PDF pair an ERP export drops, and runs through <see cref="ProcessPair"/> unchanged. The .docx, the JSON
/// read from it and the PDF move together to processed\ or error\; a failure leaves a reason file next to the .docx
/// for the people who type the invoices.
/// </summary>
public sealed partial class DocumentProcessor
{
    /// <summary>A Word file saved this recently may still be written (Word saves in several steps).</summary>
    internal static TimeSpan WordSettleTime { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions WorkJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ScanResult ScanWordDocuments(BDCOptions opts, ScanFolder folder, LicenseResult license,
        SharePointUploader? uploader, MailSender? mailer)
    {
        var result = new ScanResult();
        string tag = FolderTag(folder);
        var files = Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
            .Where(f => Path.GetExtension(f).Equals(".docx", StringComparison.OrdinalIgnoreCase)
                     || Path.GetExtension(f).Equals(".doc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count > 0)
            _log.LogInformation("scan [{Tag}] {Input}: {Count} Word document(s) to process.", tag, folder.Path, files.Count);

        foreach (var docx in files)
        {
            string name = Path.GetFileName(docx);
            string documentId = WordDocumentId(docx);
            string? siblingPdf = SiblingPdf(docx);
            string workJson = Path.Combine(folder.WorkDir, documentId + "_word.json");
            string workPdf = Path.Combine(folder.WorkDir, documentId + "_word.pdf");
            NoteReceived(docx, documentId, folder.Path);
            try
            {
                if (Path.GetExtension(docx).Equals(".doc", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "This is a Word 97-2003 document (.doc), which cannot carry Hive BDC fields. Open it in Word and save it as Word document (.docx).");
                EnsureSettled(docx);

                var invoice = WordInvoiceReader.Read(docx);
                if (invoice.Problems.Count > 0)
                    throw new InvalidDataException(string.Join(Environment.NewLine, invoice.Problems));
                foreach (var note in invoice.Notes) _log.LogInformation("[{Tag}] {File}: {Note}", tag, name, note);

                Directory.CreateDirectory(folder.WorkDir);
                File.WriteAllText(workJson, invoice.Json.ToJsonString(WorkJson), new UTF8Encoding(false));
                if (siblingPdf is not null)
                {
                    EnsureReadable(siblingPdf);
                    File.Copy(siblingPdf, workPdf, overwrite: true);
                    _log.LogInformation("[{Tag}] {File}: using the PDF saved with it ({Pdf}).", tag, name, Path.GetFileName(siblingPdf));
                }
                else
                {
                    var rendered = DocxPdfRenderer.Render(docx);
                    if (rendered.Unsupported.Count > 0)
                        throw new InvalidDataException(
                            "The document uses Word elements Hive BDC cannot draw: " + string.Join(", ", rendered.Unsupported) +
                            ". Remove them from the template, or save the PDF from Word with the same name next to the .docx.");
                    foreach (var font in rendered.MissingFonts)
                        _log.LogWarning("[{Tag}] {File}: font '{Font}' is not installed on this server; {Fallback} is used in the PDF.",
                            tag, name, font, SystemFontResolver.FallbackFamily);
                    File.WriteAllBytes(workPdf, rendered.Pdf);
                }
                _log.LogInformation("[{Tag}] {File}: read {Fields} field(s), {Lines} line(s); {Format}; totals net {Net} VAT {Tax} gross {Gross}.",
                    tag, name, invoice.Fields.Count, invoice.Lines.Count, invoice.Format,
                    invoice.Totals?.Net, invoice.Totals?.Tax, invoice.Totals?.Gross);

                Note(docx, documentId, EventSteps.Read, EventStates.Ok,
                    $"Read the Word document: {invoice.Fields.Count} field(s), {invoice.Lines.Count} line(s); " +
                    (siblingPdf is not null ? $"the PDF saved with it ({Path.GetFileName(siblingPdf)}) is used." : "the PDF was made by Hive BDC."));
                var pair = ProcessPair(opts, folder, license, workJson, workPdf, documentId, uploader, mailer, "word", docx);
                if (pair.Held > 0)
                {
                    DeleteQuietly(workJson, workPdf);   // rebuilt on the next scan
                    result += pair;
                    continue;
                }
                MoveToFolder(docx, folder.ProcessedDir);
                if (siblingPdf is not null) MoveToFolder(siblingPdf, folder.ProcessedDir);
                DeleteQuietly(Path.Combine(folder.ErrorDir, name + ".error.txt"));
                _pending.Remove(docx);
                _pendingLoggedUtc.Remove(docx);
                result += pair;
            }
            catch (PendingDocumentException ex)
            {
                DeleteQuietly(workJson, workPdf);
                result += new ScanResult(0, 0, 1);
                bool known = _pending.TryGetValue(docx, out var reason) && reason == ex.Message;
                if (!known) _log.LogWarning("{File}: pending: {Reason}", name, ex.Message);
                else _log.LogDebug("{File}: still pending: {Reason}", name, ex.Message);
                _pending[docx] = ex.Message;
                Note(docx, documentId, EventSteps.Waiting, EventStates.Waiting, ex.Message);
            }
            catch (Exception ex)
            {
                _pending.Remove(docx);
                _pendingLoggedUtc.Remove(docx);
                result += new ScanResult(0, 1, 0);
                _log.LogError("{File}: failed - {Message}", name, ex.Message);
                NoteFailed(docx, documentId, ex.Message);
                try
                {
                    Directory.CreateDirectory(folder.ErrorDir);
                    File.WriteAllText(Path.Combine(folder.ErrorDir, name + ".error.txt"), ReasonText(name, ex.Message), new UTF8Encoding(true));
                    MoveToFolder(docx, folder.ErrorDir);
                    if (siblingPdf is not null && File.Exists(siblingPdf)) MoveToFolder(siblingPdf, folder.ErrorDir);
                    foreach (var work in new[] { workJson, workPdf })
                        if (File.Exists(work)) MoveToFolder(work, folder.ErrorDir);
                }
                catch (Exception archiveError)
                {
                    _log.LogError(archiveError, "{File}: could not move it to the error folder; continuing with other documents.", name);
                }
            }
        }
        return result;
    }

    /// <summary>The document id for the pipeline: the file name without '_' (the pair naming uses '_' as separator).</summary>
    internal static string WordDocumentId(string docx)
    {
        string stem = Path.GetFileNameWithoutExtension(docx).Replace('_', '-').Trim();
        return stem.Length == 0 ? "word" : stem;
    }

    private static string? SiblingPdf(string docx)
    {
        string pdf = Path.ChangeExtension(docx, ".pdf");
        return File.Exists(pdf) ? pdf : null;
    }

    private static void EnsureSettled(string path)
    {
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < WordSettleTime)
            throw new PendingDocumentException("Just saved; waiting a few seconds before reading it.");
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { throw new PendingDocumentException("Still open in Word (the file is locked); it is read once it is closed."); }
    }

    private static void DeleteQuietly(params string[] files)
    {
        foreach (var f in files)
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string ReasonText(string file, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Hive BDC hat diese Rechnung nicht verarbeitet. / Hive BDC did not process this invoice.");
        sb.AppendLine();
        sb.AppendLine($"Datei / File: {file}");
        sb.AppendLine($"Zeit / Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("Grund / Reason:");
        foreach (var line in reason.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            sb.AppendLine("- " + line);
        sb.AppendLine();
        sb.AppendLine("Korrigieren Sie die Datei und legen Sie sie wieder in den Eingangsordner.");
        sb.AppendLine("Fix the file and put it back into the input folder.");
        return sb.ToString();
    }
}