using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Test helper (--make-sample): writes one {id}_{seq}.json (+ carrier {id}_{seq}.pdf) per
/// built-in document type into the input folder, so a fresh install can be watched end to
/// end: two e-invoices (ZUGFeRD with PDF, XRechnung without) and four PDF pass-through
/// documents for customers and vendors.
/// </summary>
public static class SampleData
{
    public static void Write(string inputDir)
    {
        Directory.CreateDirectory(inputDir);
        var written = new List<string>();

        // 1) ZUGFeRD sales invoice (2 lines, 19% VAT, EUR, German seller+buyer, IBAN) + carrier PDF
        WritePair(inputDir, "2026001234_10000192", ZugferdJson, "RECHNUNG / INVOICE 2026001234", written);

        // 2) XRechnung sales invoice (no PDF needed)
        File.WriteAllText(Path.Combine(inputDir, "7000001234_99.json"), XRechnungJson);
        written.Add("7000001234_99.json");

        // 3..6) PDF pass-through documents: the PDF is the deliverable, the JSON is the envelope.
        WritePair(inputDir, "AB0004711_1", Envelope("SalesConfirmation", "Customer", "AB0004711", "Auftragsbestätigung %1",
            "ap@muster-kunde.de", "Sehr geehrte Damen und Herren,\n\nanbei erhalten Sie die Auftragsbestätigung %1.\n\nMit freundlichen Grüßen\nBalsoft GmbH"),
            "AUFTRAGSBESTÄTIGUNG / ORDER CONFIRMATION AB0004711", written);
        WritePair(inputDir, "AN0000815_1", Envelope("SalesQuotation", "Customer", "AN0000815", "Angebot %1",
            "einkauf@muster-kunde.de", "Sehr geehrte Damen und Herren,\n\nanbei unser Angebot %1.\n\nMit freundlichen Grüßen\nBalsoft GmbH"),
            "ANGEBOT / QUOTATION AN0000815", written);
        WritePair(inputDir, "PO0001234_1", Envelope("PurchaseOrder", "Vendor", "PO0001234", "Bestellung %1",
            "orders@muster-lieferant.de", "Sehr geehrte Damen und Herren,\n\nanbei unsere Bestellung %1.\n\nMit freundlichen Grüßen\nBalsoft GmbH"),
            "BESTELLUNG / PURCHASE ORDER PO0001234", written);
        WritePair(inputDir, "ZA0000042_1", Envelope("PaymentAdvice", "Vendor", "ZA0000042", "Zahlungsavis %1",
            "buchhaltung@muster-lieferant.de", "Sehr geehrte Damen und Herren,\n\nanbei das Zahlungsavis %1 zu unserer SEPA-Zahlung.\n\nMit freundlichen Grüßen\nBalsoft GmbH"),
            "ZAHLUNGSAVIS / PAYMENT ADVICE ZA0000042", written);

        Console.WriteLine($"Wrote samples to {inputDir}: {string.Join(", ", written)}");
    }

    private static void WritePair(string dir, string baseName, string json, string pdfTitle, List<string> written)
    {
        File.WriteAllText(Path.Combine(dir, baseName + ".json"), json);
        MakeCarrierPdf(Path.Combine(dir, baseName + ".pdf"), pdfTitle);
        written.Add(baseName + ".json/.pdf");
    }

    private static void MakeCarrierPdf(string path, string title)
    {
        // PDFsharp embeds font subsets by default, so the carrier is PDF/A-friendly.
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont("Arial", 12);
        gfx.DrawString(title, font, XBrushes.Black, new XPoint(40, 60));
        gfx.DrawString("Balsoft GmbH — sample PDF (visual representation).", font, XBrushes.Black, new XPoint(40, 80));
        gfx.DrawString("Written by HiveBDC --make-sample.", font, XBrushes.Black, new XPoint(40, 100));
        pdf.Save(path);
    }

    /// <summary>The envelope every non-invoice document shares: type, number, party, recipient, mail text, environment.</summary>
    private static string Envelope(string type, string party, string number, string subject, string recipient, string body)
        => $$"""
        {
          "documentType": "{{type}}",
          "documentNumber": "{{number}}",
          "party": "{{party}}",
          "issueDate": "2026-09-16",
          "senderName": "Balsoft GmbH",
          "recipientName": "{{(party == "Vendor" ? "Muster Lieferant GmbH" : "Muster Kunde GmbH")}}",
          "recipientEmail": "{{recipient}}",
          "recipientEmailCc": "",
          "emailSubject": "{{subject}}",
          "emailBody": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "environment": "Test",
          "dataAreaId": "default"
        }
        """;

    private const string ZugferdJson = """
    {
      "documentType": "SalesInvoice",
      "documentNumber": "2026001234",
      "party": "Customer",
      "eInvoiceType": "ZUGFeRD",
      "invoiceNumber": "2026001234",
      "invoiceTypeCode": "380",
      "zrKennzeichen": "0042",
      "senderId": "99001",
      "senderGln": "4000001000005",
      "recipientId": "99002",
      "issueDate": "2026-06-15",
      "dueDate": "2026-06-29",
      "currency": "EUR",
      "reference": "PO-10000192",
      "senderName": "Balsoft GmbH",
      "senderStreet": "Herler Str. 109",
      "senderPostcode": "51067",
      "senderCity": "Köln",
      "senderCountry": "Germany",
      "senderTaxId": "215/5719/0815",
      "senderVatId": "DE452983132",
      "senderEmail": "invoice@balsoft.de",
      "senderPhone": "+49 221 1234567",
      "recipientName": "Muster Kunde GmbH",
      "recipientStreet": "Musterstr. 1",
      "recipientPostcode": "10115",
      "recipientCity": "Berlin",
      "recipientCountry": "Germany",
      "recipientVatId": "DE123456789",
      "recipientEmail": "ap@muster-kunde.de",
      "emailSubject": "Rechnung Nr. %1",
      "emailBody": "Sehr geehrte Damen und Herren,\n\nanbei erhalten Sie die Rechnung %1.\n\nMit freundlichen Grüßen\nBalsoft GmbH",
      "environment": "Test",
      "paymentTermsText": "Zahlbar ohne Abzug bis 29.06.2026.",
      "cashDiscountDate": "2026-06-22",
      "cashDiscountPercent": "2",
      "cashDiscountAmount": "28.38",
      "vatRate": "19",
      "bankIban": "DE89 3704 0044 0532 0130 00",
      "notes": "Vielen Dank fuer Ihren Auftrag.",
      "lineItems": [
        { "description": "Beratung D365FO", "quantity": "10", "unit": "hour", "unitPrice": "120.00" },
        { "description": "Lizenz Modul",     "quantity": "2",  "unit": "piece", "unitPrice": "499.50" }
      ]
    }
    """;

    private const string XRechnungJson = """
    {
      "documentType": "SalesInvoice",
      "documentNumber": "7000001234",
      "eInvoiceType": "XRechnung",
      "invoiceNumber": "7000001234",
      "issueDate": "2026-06-15",
      "dueDate": "2026-07-15",
      "currency": "EUR",
      "reference": "LEITWEG-99",
      "senderName": "Balsoft GmbH",
      "senderStreet": "Herler Str. 109",
      "senderPostcode": "51067",
      "senderCity": "Köln",
      "senderCountry": "DE",
      "senderVatId": "DE452983132",
      "senderEmail": "invoice@balsoft.de",
      "recipientName": "Behoerde XYZ",
      "recipientStreet": "Amtsweg 5",
      "recipientPostcode": "53111",
      "recipientCity": "Bonn",
      "recipientCountry": "DE",
      "recipientVatId": "DE987654321",
      "recipientEmail": "rechnung@behoerde-xyz.de",
      "environment": "Test",
      "vatRate": "19",
      "bankIban": "DE89370400440532013000",
      "lineItems": [
        { "description": "Softwarepflege Q2", "quantity": "1", "unit": "piece", "unitPrice": "2500.00" }
      ]
    }
    """;
}
