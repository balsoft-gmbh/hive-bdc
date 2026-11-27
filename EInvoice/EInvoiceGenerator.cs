using System.Text;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.Cii;
using Balsoft.Hive.EInvoice.Pdf;
using Balsoft.Hive.EInvoice.Validation;

namespace Balsoft.Hive.BDC.EInvoice;

/// <summary>
/// Produces the e-invoice artifacts with Balsoft.Hive.EInvoice:
///  - XRechnung: standalone XRechnung 3.0 CII XML.
///  - ZUGFeRD : CII XML of Factur-X profile EN 16931 embedded into the carrier PDF as a PDF/A-3b hybrid
///    (factur-x.xml attachment, Factur-X XMP, sRGB output intent, missing TrueType fonts embedded from the
///    system). With XRechnung in the eInvoiceType as well, the PDF carries the XRECHNUNG level instead
///    (xrechnung.xml), for public buyers who take a PDF.
/// Before writing, the invoice runs through the library's pre-flight check for its profile (EN 16931, plus the
/// XRechnung rules for XRechnung); every finding is logged as a warning and the document is still written, so a
/// data gap in the ERP never stops delivery.
/// </summary>
public static class EInvoiceGenerator
{
    /// <summary>
    /// The profile of a document's output (Factur-X 1.09 / ZUGFeRD 2.5, section 4): ZUGFeRD is EN 16931, ZUGFeRD together
    /// with XRechnung is the XRECHNUNG level inside the PDF, the XML output (also for an unknown eInvoiceType) is XRechnung 3.0.
    /// </summary>
    public static InvoiceProfile ProfileFor(AxDocument data)
        => data.IsZugferd && !data.IsXRechnung ? InvoiceProfile.EN16931 : InvoiceProfile.XRechnung;

    /// <summary>The name of a profile in the history and the log.</summary>
    public static string FormatName(AxDocument data)
        => !data.IsZugferd ? "XRechnung" : ProfileFor(data) == InvoiceProfile.EN16931 ? "ZUGFeRD (EN 16931)" : "ZUGFeRD (XRechnung)";

    private static readonly WriterOptions Options = new() { ValidateBeforeWriting = false };

    /// <summary>Reports every rule of the profile the invoice violates, one warning each.</summary>
    public static ValidationResult Preflight(Invoice invoice, InvoiceProfile profile, Action<string> warn)
    {
        var result = InvoiceValidator.Validate(invoice, profile);
        foreach (var issue in result.Issues)
            warn($"e-invoice check: {issue}");
        return result;
    }

    /// <summary>Generate the CII XML of the profile.</summary>
    public static byte[] BuildCiiXml(Invoice invoice, InvoiceProfile profile) => CiiWriter.Write(invoice, profile, Options);

    /// <summary>Write the standalone XRechnung XML file.</summary>
    public static void WriteXRechnung(Invoice invoice, string outXmlPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outXmlPath)!);
        File.WriteAllBytes(outXmlPath, BuildCiiXml(invoice, InvoiceProfile.XRechnung));
    }

    /// <summary>
    /// Embed the CII XML into the carrier PDF as a PDF/A-3b hybrid and write it. On failure
    /// the caller routes the input pair to error\ and nothing is produced.
    /// </summary>
    public static void WriteZugferd(Invoice invoice, InvoiceProfile profile, string sourcePdfPath, string outPdfPath, Action<string> log, bool subsetFonts = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPdfPath)!);
        byte[] xml = BuildCiiXml(invoice, profile);
        try
        {
            Embed(invoice, profile, xml, sourcePdfPath, outPdfPath, log, subsetFonts);
            return;
        }
        catch (Exception ex)
        {
            TryDelete(outPdfPath);

            // AX 2009 carrier PDFs are malformed in a way PDFsharp rejects outright
            // (see PdfRepair). Repair a copy and retry once before giving up.
            if (!PdfRepair.TryRepair(sourcePdfPath, out var repairedPath, out var summary))
                throw new InvalidOperationException($"ZUGFeRD PDF embedding failed: {ex.Message}", ex);

            try
            {
                Embed(invoice, profile, xml, repairedPath, outPdfPath, log, subsetFonts);
                log($"carrier PDF was malformed ({summary}) — repaired a copy and embedded successfully. " +
                    "The source PDF from the ERP is unchanged.");
            }
            catch (Exception retryEx)
            {
                TryDelete(outPdfPath);
                throw new InvalidOperationException(
                    $"ZUGFeRD PDF embedding failed, also after repairing the carrier PDF ({summary}): {retryEx.Message}", retryEx);
            }
            finally
            {
                TryDelete(repairedPath);
            }
        }
    }

    private static void Embed(Invoice invoice, InvoiceProfile profile, byte[] xml, string sourcePdfPath, string outPdfPath, Action<string> log, bool subsetFonts)
    {
        var options = new HybridPdfOptions
        {
            SubsetFonts = subsetFonts,
            Title = string.IsNullOrWhiteSpace(invoice.Number) ? "Invoice" : $"Invoice {invoice.Number}",
            Author = string.IsNullOrWhiteSpace(invoice.Seller.Name) ? null : invoice.Seller.Name,
            Creator = "Hive BDC, by Balsoft",
            // A font that cannot be embedded breaks PDF/A: worth a warning. Embedded fonts are routine.
            Log = message => { if (!message.StartsWith("Embedded font", StringComparison.Ordinal)) log(message); },
        };
        File.WriteAllBytes(outPdfPath, HybridPdf.Create(File.ReadAllBytes(sourcePdfPath), xml, profile, options));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>Diagnostic: pull the embedded invoice XML out of a hybrid PDF.</summary>
    public static string ExtractEmbeddedXml(string pdfPath)
        => HybridPdf.ExtractXml(pdfPath) is { } xml ? Encoding.UTF8.GetString(xml) : "(no embedded file found)";
}
