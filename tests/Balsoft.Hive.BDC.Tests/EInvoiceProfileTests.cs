using System.Xml.Linq;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The profile values of each output, pinned to the specifications (design 2026-11-27-bdc-zugferd-en16931-profile):
/// ZUGFeRD is Factur-X profile EN 16931, ZUGFeRD with XRechnung in the eInvoiceType is the XRECHNUNG level inside the PDF,
/// the XML output is XRechnung 3.0. Values are read back from the files the pipeline writes.
/// </summary>
public sealed class EInvoiceProfileTests : IDisposable
{
    private const string En16931Id = "urn:cen.eu:en16931:2017";
    private const string XRechnungId = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    private const string PeppolProcess = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";
    private static readonly XNamespace Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-profile-" + Guid.NewGuid());

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "einvoice", "documents" } },
    };

    public EInvoiceProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private sealed record Run(DocumentProcessor Processor, ExportLedger Ledger, ListLogger Log, BDCOptions Options, string Folder);

    private Run Setup()
    {
        string folder = Path.Combine(_root, "in");
        Directory.CreateDirectory(folder);
        var log = new ListLogger();
        var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
        var processor = new DocumentProcessor(log, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), ledger);
        var options = new BDCOptions();
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "SalesInvoice", Handler = DocumentHandlerKind.EInvoice, Mail = false,
            Companies = { new DocumentRoute { DataAreaId = "1000", Input = folder, Output = Path.Combine(folder, "out") } },
        });
        options.Normalize();
        return new Run(processor, ledger, log, options, folder);
    }

    // A business invoice without buyer reference (BT-10) and without buyer electronic address (BT-49): valid EN 16931,
    // not valid XRechnung.
    private static string Json(string eInvoiceType) => $$"""
        {
          "documentType": "SalesInvoice", "dataAreaId": "1000", "documentNumber": "2026000042", "invoiceNumber": "2026000042",
          "eInvoiceType": "{{eInvoiceType}}", "environment": "Dev", "invoiceTypeCode": "380",
          "issueDate": "2026-11-20", "dueDate": "2026-12-20", "currency": "EUR", "vatRate": "19.00",
          "senderName": "Muster Lieferant GmbH", "senderCountry": "DE", "senderVatId": "DE123456789", "senderStreet": "Weg 1",
          "senderPostcode": "10115", "senderCity": "Berlin", "senderPhone": "+49 30 000000", "senderEmail": "rechnung@example.com",
          "recipientName": "Beispiel Kunde GmbH", "recipientCountry": "DE", "recipientPostcode": "20095", "recipientCity": "Hamburg",
          "recipientStreet": "Hafenstr. 2", "customerNumber": "K-1000", "bankIban": "DE89370400440532013000",
          "lineItems": [
            { "lineId": "1", "description": "Beratung", "quantity": "1.00", "unit": "piece", "unitPrice": "100.00", "vatRate": "19.00", "vatCategory": "S" }
          ]
        }
        """;

    private sealed record Written(XDocument Xml, string? ConformanceLevel, string? XmpVersion, string? FileName, string Format, List<string> Log);

    private Written Produce(string eInvoiceType)
    {
        var run = Setup();
        File.WriteAllText(Path.Combine(run.Folder, "2026000042_1.json"), Json(eInvoiceType));
        File.WriteAllBytes(Path.Combine(run.Folder, "2026000042_1.pdf"), TestPdf.Build(TestPdf.ImagePage(600, 600, 72)));

        var result = run.Processor.ScanOnce(run.Options, License, null, null);
        Assert.Equal((1, 0), (result.Processed, result.Errors));
        string format = Assert.Single(run.Ledger.Metrics(10).Recent).Format;

        string pdfOut = Path.Combine(run.Folder, "out", "2026000042.pdf");
        string xmlOut = Path.Combine(run.Folder, "out", "2026000042.xml");
        if (!File.Exists(pdfOut))
            return new Written(XDocument.Load(xmlOut), null, null, null, format, run.Log.Lines);

        byte[] pdf = File.ReadAllBytes(pdfOut);
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var metadata = (PdfDictionary)doc.Internals.Catalog.Elements.GetReference("/Metadata")!.Value;
        var xmp = XDocument.Parse(System.Text.Encoding.UTF8.GetString(metadata.Stream.UnfilteredValue));
        string? Fx(string name) => xmp.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Name.NamespaceName.StartsWith("urn:factur-x", StringComparison.Ordinal))?.Value;
        return new Written(XDocument.Parse(System.Text.Encoding.UTF8.GetString(HybridPdf.ExtractXml(pdf)!)),
            Fx("ConformanceLevel"), Fx("Version"), Fx("DocumentFileName"), format, run.Log.Lines);
    }

    private static string? Bt24(XDocument xml) => xml.Root!.Element(Rsm + "ExchangedDocumentContext")!
        .Element(Ram + "GuidelineSpecifiedDocumentContextParameter")?.Element(Ram + "ID")?.Value;

    private static string? Bt23(XDocument xml) => xml.Root!.Element(Rsm + "ExchangedDocumentContext")!
        .Element(Ram + "BusinessProcessSpecifiedDocumentContextParameter")?.Element(Ram + "ID")?.Value;

    private static bool XRechnungRulesRan(List<string> log)
        => log.Any(l => l.Contains("e-invoice check", StringComparison.Ordinal) && (l.Contains("BR-DE-", StringComparison.Ordinal) || l.Contains("PEPPOL-EN16931-R", StringComparison.Ordinal)));

    [Theory]
    [InlineData("ZUGFeRD")]
    [InlineData("zugferd")]
    [InlineData("ZUGFeRD 2.3")]
    public void ZugferdIsTheEn16931Profile(string eInvoiceType)
    {
        var written = Produce(eInvoiceType);

        Assert.Equal("zugferd", written.Format);
        Assert.Equal(En16931Id, Bt24(written.Xml));
        Assert.Null(Bt23(written.Xml));
        Assert.Equal("EN 16931", written.ConformanceLevel);
        Assert.Equal("1.0", written.XmpVersion);
        Assert.Equal("factur-x.xml", written.FileName);
        Assert.False(XRechnungRulesRan(written.Log), string.Join("\n", written.Log));
    }

    [Theory]
    [InlineData("ZUGFeRD-XRechnung")]
    [InlineData("ZUGFeRD XRechnung")]
    [InlineData("xrechnung_zugferd")]
    public void ZugferdWithXRechnungIsTheXRechnungLevelInsideThePdf(string eInvoiceType)
    {
        var written = Produce(eInvoiceType);

        Assert.Equal("zugferd", written.Format);
        Assert.Equal(XRechnungId, Bt24(written.Xml));
        Assert.Equal(PeppolProcess, Bt23(written.Xml));
        Assert.Equal("XRECHNUNG", written.ConformanceLevel);
        // Factur-X 1.09.2 BR-HYBRID-09 (Fatal): a HybridDocumentVersion value; BR-HYBRID-10: SHOULD be 1.0.
        Assert.Equal("1.0", written.XmpVersion);
        Assert.Equal("xrechnung.xml", written.FileName);
        Assert.True(XRechnungRulesRan(written.Log), "the XRechnung rules must check an XRechnung inside a PDF");
    }

    [Theory]
    [InlineData("XRechnung", false)]
    [InlineData("", true)]
    [InlineData("Unknown", true)]
    public void TheXmlOutputStaysXRechnung(string eInvoiceType, bool unrecognized)
    {
        var written = Produce(eInvoiceType);

        Assert.Equal("xrechnung", written.Format);
        Assert.Equal(XRechnungId, Bt24(written.Xml));
        Assert.Equal(PeppolProcess, Bt23(written.Xml));
        Assert.True(XRechnungRulesRan(written.Log), "BT-10 and BT-49 are missing, so the XRechnung rules must report them");
        Assert.Equal(unrecognized, written.Log.Any(l => l.Contains("unrecognized", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("ZUGFeRD", InvoiceProfile.EN16931)]
    [InlineData("ZUGFeRD-XRechnung", InvoiceProfile.XRechnung)]
    [InlineData("XRechnung", InvoiceProfile.XRechnung)]
    [InlineData("", InvoiceProfile.XRechnung)]
    [InlineData("Unknown", InvoiceProfile.XRechnung)]
    public void TheProfileFollowsTheEInvoiceType(string eInvoiceType, InvoiceProfile expected)
    {
        var data = AxDocument.Parse($$"""{ "documentType": "SalesInvoice", "documentNumber": "1", "eInvoiceType": "{{eInvoiceType}}" }""");
        Assert.Equal(expected, EInvoiceGenerator.ProfileFor(data));
    }

    [Fact]
    public void TheHistoryNamesTheProfile()
    {
        var run = Setup();
        File.WriteAllText(Path.Combine(run.Folder, "2026000042_1.json"), Json("ZUGFeRD"));
        File.WriteAllBytes(Path.Combine(run.Folder, "2026000042_1.pdf"), TestPdf.Build(TestPdf.ImagePage(600, 600, 72)));
        run.Processor.ScanOnce(run.Options, License, null, null);

        var read = Assert.Single(run.Ledger.Events("2026000042", 100), e => e.Step == EventSteps.Read);
        Assert.Contains("e-invoice format ZUGFeRD (EN 16931)", read.Message);
    }
}