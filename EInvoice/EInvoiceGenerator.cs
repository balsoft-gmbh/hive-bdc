using System.Text;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.Cii;
using Balsoft.Hive.EInvoice.Pdf;
using Balsoft.Hive.EInvoice.Validation;

namespace Balsoft.Hive.BDC.EInvoice;

/// <summary>
/// Produces the e-invoice artifacts with Balsoft.Hive.EInvoice:
///  - XRechnung: standalone XRechnung 3.0 CII XML.
///  - ZUGFeRD : the same CII XML embedded into the carrier PDF as a PDF/A-3b hybrid
///    (xrechnung.xml attachment, Factur-X XMP, sRGB output intent, missing TrueType fonts
///    embedded from the system).
/// Before writing, the invoice runs through the library's pre-flight check (EN 16931 and
/// XRechnung rules with their official ids); every finding is logged as a warning and the
/// document is still written, so a data gap in the ERP never stops delivery.
/// </summary>
public static class EInvoiceGenerator
{
    /// <summary>The profile Hive BDC writes: XRechnung 3.0, also inside ZUGFeRD PDFs.</summary>
    public const InvoiceProfile Profile = InvoiceProfile.XRechnung;

    private static readonly WriterOptions Options = new() { ValidateBeforeWriting = false };

    /// <summary>Reports every EN 16931 / XRechnung rule the invoice violates, one warning each.</summary>
    public static ValidationResult Preflight(Invoice invoice, Action<string> warn)
    {
        var result = InvoiceValidator.Validate(invoice, Profile);
        foreach (var issue in result.Issues)
            warn($"e-invoice check: {issue}");
        return result;
    }

    /// <summary>Generate the CII XML (XRechnung 3.0).</summary>
    public static byte[] BuildCiiXml(Invoice invoice) => CiiWriter.Write(invoice, Profile, Options);

    /// <summary>Write the standalone XRechnung XML file.</summary>
    public static void WriteXRechnung(Invoice invoice, string outXmlPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outXmlPath)!);
        File.WriteAllBytes(outXmlPath, BuildCiiXml(invoice));
    }

    /// <summary>
    /// Embed the CII XML into the carrier PDF as a PDF/A-3b hybrid and write it. On failure
    /// the caller routes the input pair to error\ and nothing is produced.
    /// </summary>
    public static void WriteZugferd(Invoice invoice, string sourcePdfPath, string outPdfPath, Action<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPdfPath)!);
        byte[] xml = BuildCiiXml(invoice);
        try
        {
            Embed(invoice, xml, sourcePdfPath, outPdfPath, log);
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
                Embed(invoice, xml, repairedPath, outPdfPath, log);
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

    private static void Embed(Invoice invoice, byte[] xml, string sourcePdfPath, string outPdfPath, Action<string> log)
    {
        var options = new HybridPdfOptions
        {
            Title = string.IsNullOrWhiteSpace(invoice.Number) ? "Invoice" : $"Invoice {invoice.Number}",
            Author = string.IsNullOrWhiteSpace(invoice.Seller.Name) ? null : invoice.Seller.Name,
            Creator = "Hive BDC, by Balsoft",
            // A font that cannot be embedded breaks PDF/A: worth a warning. Embedded fonts are routine.
            Log = message => { if (!message.StartsWith("Embedded font", StringComparison.Ordinal)) log(message); },
        };
        File.WriteAllBytes(outPdfPath, HybridPdf.Create(File.ReadAllBytes(sourcePdfPath), xml, Profile, options));
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
