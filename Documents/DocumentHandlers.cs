using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Everything a handler needs to turn one dropped pair into an output file.
/// DocumentId is the id from the file name ({documentId}_{seq}.json); DocumentNumber is the
/// ERP's document number (falls back to DocumentId) and names the output (<see cref="FileStem"/>);
/// OutputDir is the resolved output folder for this company entry + type (created by the handler).
/// </summary>
public sealed record DocumentContext(
    DocumentRoute Route,
    DocumentTypeOptions Type,
    AxDocument Data,
    string JsonPath,
    string PdfPath,
    string DocumentId,
    string DocumentNumber,
    string OutputDir,
    Action<string> Warn,
    ILogger Log)
{
    /// <summary>
    /// Output file name without extension: the document number, so two documents that share
    /// an id (purchase order versions 4500001234-1 and -2) stay two files in the output folder
    /// and in SharePoint instead of the second replacing the first.
    /// </summary>
    public string FileStem => OutputFileNames.Stem(DocumentNumber, DocumentId);
}

/// <summary>Output file naming shared by every handler.</summary>
public static class OutputFileNames
{
    // Windows and SharePoint both reject these in a file name; listed explicitly because
    // Path.GetInvalidFileNameChars() depends on the OS the code runs on.
    private static readonly char[] Invalid = { '"', '*', ':', '<', '>', '?', '/', '\\', '|', '#', '%' };

    /// <summary>The document number made safe as a file name, else the document id.</summary>
    public static string Stem(string? documentNumber, string documentId)
    {
        string raw = string.IsNullOrWhiteSpace(documentNumber) ? documentId : documentNumber.Trim();
        var chars = raw.Select(c => c < 32 || Invalid.Contains(c) ? '_' : c).ToArray();
        string stem = new string(chars).Trim().TrimEnd('.', ' ');
        return stem.Length == 0 ? documentId : stem;
    }

    /// <summary>The file a document becomes: an e-invoice gives its XRechnung XML unless it is ZUGFeRD or NoEInvoice,
    /// everything else a PDF, as <see cref="EInvoiceHandler"/> and <see cref="PdfHandler"/> write it.</summary>
    public static string For(DocumentHandlerKind kind, AxDocument data, string documentId)
        => Stem(data.ResolveNumber(documentId), documentId)
           + (kind == DocumentHandlerKind.EInvoice && !data.IsZugferd && !data.IsNoEInvoice ? ".xml" : ".pdf");
}

/// <summary>What a handler produced: the file to deliver and a short format tag for the ledger ("xrechnung", "zugferd", "pdf").</summary>
public sealed record DocumentOutput(string File, string Format, PdfCompression? Compression = null);

/// <summary>
/// One strategy per <see cref="DocumentHandlerKind"/>. A handler only PRODUCES the output
/// file; delivery (SharePoint, mail), metering and moving the inputs are the processor's
/// job and identical for every type.
/// </summary>
public interface IDocumentHandler
{
    DocumentHandlerKind Kind { get; }

    /// <summary>The license feature this handler needs ("einvoice" / "documents").</summary>
    string RequiredFeature { get; }

    DocumentOutput Produce(DocumentContext ctx);
}

/// <summary>
/// The original Hive eInvoice feature: map the JSON to EN 16931 and write XRechnung XML,
/// or embed the same CII XML into the ERP's PDF as a ZUGFeRD PDF/A-3 (Balsoft.Hive.EInvoice). Routes strictly by
/// eInvoiceType: xrechnung -> {number}.xml only, zugferd -> {number}.pdf only, NoEInvoice -> the ERP's PDF as
/// <see cref="PdfHandler"/> delivers it, without mapping or XML.
/// </summary>
public sealed class EInvoiceHandler : IDocumentHandler
{
    public DocumentHandlerKind Kind => DocumentHandlerKind.EInvoice;
    public string RequiredFeature => LicenseFeatures.EInvoice;

    public DocumentOutput Produce(DocumentContext ctx)
    {
        var data = ctx.Data;
        if (data.IsNoEInvoice) return PdfHandler.PassThrough(ctx);

        var invoice = InvoiceMapper.Map(data, ctx.Warn, m => ctx.Log.LogInformation("{Id}: {Message}", ctx.DocumentId, m));
        EInvoiceGenerator.Preflight(invoice, ctx.Warn);
        Directory.CreateDirectory(ctx.OutputDir);

        if (data.IsZugferd)
        {
            if (!File.Exists(ctx.PdfPath))
                throw new FileNotFoundException($"ZUGFeRD requires sibling PDF '{Path.GetFileName(ctx.PdfPath)}' but it is missing.");

            string outFile = Path.Combine(ctx.OutputDir, ctx.FileStem + ".pdf");
            EInvoiceGenerator.WriteZugferd(invoice, ctx.PdfPath, outFile, ctx.Warn);
            ctx.Log.LogInformation("[zugferd] {Id} -> {Out}", ctx.DocumentId, outFile);
            var compression = ctx.Route.CompressesFor(ctx.Type) ? CompressZugferd(ctx, invoice, outFile) : null;
            return new DocumentOutput(outFile, "zugferd", compression);
        }

        if (!data.IsXRechnung)
            ctx.Log.LogWarning("{Id}: eInvoiceType '{Type}' unrecognized; producing XRechnung XML.", ctx.DocumentId, data.EInvoiceType);

        string xml = Path.Combine(ctx.OutputDir, ctx.FileStem + ".xml");
        EInvoiceGenerator.WriteXRechnung(invoice, xml);
        ctx.Log.LogInformation("[xrechnung] {Id} -> {Out}", ctx.DocumentId, xml);
        return new DocumentOutput(xml, "xrechnung");
    }

    /// <summary>
    /// The ZUGFeRD again from a compressed copy of the carrier, with the fonts it embeds as subsets. It replaces the plain
    /// one only when it is smaller; any failure keeps the plain one, so compression never stops a delivery.
    /// </summary>
    private static PdfCompression CompressZugferd(DocumentContext ctx, Balsoft.Hive.EInvoice.Invoice invoice, string outFile)
    {
        long plain = new FileInfo(outFile).Length;
        string carrier = Path.Combine(Path.GetTempPath(), $"hivebdc-carrier-{Guid.NewGuid():N}.pdf");
        string candidate = outFile + ".compressed.tmp";
        try
        {
            File.Copy(ctx.PdfPath, carrier, overwrite: true);
            var carrierResult = PdfCompressor.CompressFile(carrier, keepPdfA: false, m => ctx.Log.LogInformation("{Id}: {Message}", ctx.DocumentId, m));
            if (!carrierResult.Compressed)
                ctx.Log.LogInformation("{Id}: carrier PDF {Reason}; fonts are still embedded as subsets.", ctx.DocumentId, carrierResult.Summary);
            EInvoiceGenerator.WriteZugferd(invoice, carrier, candidate, ctx.Warn, subsetFonts: true);
            long compressed = new FileInfo(candidate).Length;
            if (compressed >= plain) return new PdfCompression(plain, plain, "already compact");
            File.Move(candidate, outFile, overwrite: true);
            ctx.Log.LogInformation("[zugferd] {Id}: compressed {Before} to {After} bytes", ctx.DocumentId, plain, compressed);
            return new PdfCompression(plain, compressed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ctx.Warn($"The compressed ZUGFeRD could not be made, the uncompressed one is delivered: {ex.Message}");
            return new PdfCompression(plain, plain, "the compressed version could not be made");
        }
        finally
        {
            foreach (var temp in new[] { carrier, candidate })
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

/// <summary>
/// Pass-through for every other document: the PDF the ERP rendered IS the deliverable.
/// It is copied (not moved) into the output folder under the document id, so the ledger
/// and the dashboard's double-click point at a file that outlives the processed\ inputs.
/// Nothing inside the PDF is touched unless the type or company has Compress PDF on; then the
/// copy is compressed (<see cref="PdfCompressor"/>) and the ERP's PDF stays as it was.
/// </summary>
public sealed class PdfHandler : IDocumentHandler
{
    public DocumentHandlerKind Kind => DocumentHandlerKind.Pdf;
    public string RequiredFeature => LicenseFeatures.Documents;

    public DocumentOutput Produce(DocumentContext ctx) => PassThrough(ctx);

    /// <summary>The copy and optional compression, shared with an e-invoice type's NoEInvoice documents.</summary>
    internal static DocumentOutput PassThrough(DocumentContext ctx)
    {
        if (!File.Exists(ctx.PdfPath))
            throw new FileNotFoundException(
                $"Document type '{ctx.Type.Id}' delivers the ERP's PDF, but sibling '{Path.GetFileName(ctx.PdfPath)}' is missing.");

        Directory.CreateDirectory(ctx.OutputDir);
        string outFile = Path.Combine(ctx.OutputDir, ctx.FileStem + ".pdf");
        File.Copy(ctx.PdfPath, outFile, overwrite: true);
        ctx.Log.LogInformation("[pdf] {Id} ({Type}) -> {Out}", ctx.DocumentId, ctx.Type.Id, outFile);
        if (!ctx.Route.CompressesFor(ctx.Type)) return new DocumentOutput(outFile, "pdf");
        var compression = PdfCompressor.CompressFile(outFile, keepPdfA: true, m => ctx.Log.LogInformation("{Id}: {Message}", ctx.DocumentId, m));
        ctx.Log.LogInformation("[pdf] {Id}: {Summary}", ctx.DocumentId, compression.Summary);
        return new DocumentOutput(outFile, "pdf", compression);
    }
}

/// <summary>The handler instances, one per kind. Stateless, so shared.</summary>
public static class DocumentHandlers
{
    private static readonly IDocumentHandler[] All = { new EInvoiceHandler(), new PdfHandler() };

    public static IDocumentHandler For(DocumentHandlerKind kind)
        => All.FirstOrDefault(h => h.Kind == kind)
           ?? throw new InvalidOperationException($"No handler for document handler kind '{kind}'.");
}
