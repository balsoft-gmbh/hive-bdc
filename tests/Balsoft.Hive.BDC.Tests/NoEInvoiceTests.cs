using System.Security.Cryptography;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Balsoft.Hive.EInvoice.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// An e-invoice type's export that says eInvoiceType NoEInvoice (an AX 2012 export, 2026-11-25) is no e-invoice:
/// the ERP's PDF goes out as it is, without mapping and without XML.
/// </summary>
public sealed class NoEInvoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-noeinvoice-" + Guid.NewGuid());

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "einvoice", "documents" } },
    };

    public NoEInvoiceTests() => Directory.CreateDirectory(_root);

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

    private Run Setup(bool compress = false)
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
            Companies = { new DocumentRoute { DataAreaId = "1000", Input = folder, Output = Path.Combine(folder, "out"), CompressPdf = compress } },
        });
        options.Normalize();
        return new Run(processor, ledger, log, options, folder);
    }

    private static string Json(string eInvoiceType, bool invoiceData = true) => invoiceData
        ? $$"""
          {
            "documentType": "SalesInvoice", "dataAreaId": "1000", "documentNumber": "2026000042", "invoiceNumber": "2026000042",
            "eInvoiceType": "{{eInvoiceType}}", "environment": "Dev", "invoiceTypeCode": "380",
            "issueDate": "2026-11-20", "dueDate": "2026-12-20", "currency": "EUR", "vatRate": "19.00",
            "senderName": "Muster Lieferant GmbH", "senderCountry": "DE", "senderVatId": "DE123456789", "senderStreet": "Weg 1",
            "senderPostcode": "10115", "senderCity": "Berlin", "senderPhone": "+49 30 000000",
            "recipientName": "Beispiel Kunde GmbH", "recipientCountry": "DE", "recipientPostcode": "20095", "recipientCity": "Hamburg",
            "recipientStreet": "Hafenstr. 2", "customerNumber": "K-1000", "buyerReference": "04011000-12345-67", "bankIban": "DE89370400440532013000",
            "netAmount": "105.00", "taxAmount": "19.95", "grossAmount": "124.95",
            "lineItems": [
              { "lineId": "1", "description": "Beratung", "quantity": "1.00", "unit": "piece", "unitPrice": "100.00", "vatRate": "19.00", "vatCategory": "S" },
              { "lineId": "2", "description": "Versand", "quantity": "1.00", "unit": "piece", "unitPrice": "5.00", "vatRate": "19.00", "vatCategory": "S" }
            ]
          }
          """
        : $$"""{ "documentType": "SalesInvoice", "dataAreaId": "1000", "documentNumber": "2026000042", "eInvoiceType": "{{eInvoiceType}}" }""";

    private static byte[] ErpPdf() => TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144));

    private static void Drop(Run run, string json, byte[]? pdf)
    {
        File.WriteAllText(Path.Combine(run.Folder, "2026000042_1.json"), json);
        if (pdf is not null) File.WriteAllBytes(Path.Combine(run.Folder, "2026000042_1.pdf"), pdf);
    }

    private static string Out(Run run, string extension) => Path.Combine(run.Folder, "out", "2026000042" + extension);

    [Theory]
    [InlineData("NoEInvoice")]
    [InlineData("noeinvoice")]
    [InlineData("  NoEInvoice ")]
    public void NoEInvoiceDeliversTheErpPdfUnchangedWithoutXml(string eInvoiceType)
    {
        var run = Setup();
        byte[] pdf = ErpPdf();
        Drop(run, Json(eInvoiceType), pdf);

        Assert.Equal(1, run.Processor.ScanOnce(run.Options, License, null, null).Processed);

        Assert.Equal(SHA256.HashData(pdf), SHA256.HashData(File.ReadAllBytes(Out(run, ".pdf"))));
        Assert.Null(HybridPdf.ExtractXml(Out(run, ".pdf")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(run.Folder, "out"), "*.xml"));
        Assert.Equal("pdf", Assert.Single(run.Ledger.Metrics(10).Recent).Format);
        var read = Assert.Single(run.Ledger.Events("2026000042", 100), e => e.Step == EventSteps.Read);
        Assert.Contains("no e-invoice (NoEInvoice)", read.Message);
        Assert.StartsWith("Produced PDF 2026000042.pdf", Assert.Single(run.Ledger.Events("2026000042", 100), e => e.Step == EventSteps.Produced).Message);
        Assert.DoesNotContain(run.Log.Lines, l => l.Contains("e-invoice check", StringComparison.Ordinal) || l.Contains("unrecognized", StringComparison.Ordinal));
    }

    [Fact]
    public void NoEInvoiceWithoutInvoiceDataIsStillDelivered()
    {
        var run = Setup();
        byte[] pdf = ErpPdf();
        Drop(run, Json("NoEInvoice", invoiceData: false), pdf);

        var result = run.Processor.ScanOnce(run.Options, License, null, null);

        Assert.Equal((1, 0), (result.Processed, result.Errors));
        Assert.Equal(pdf, File.ReadAllBytes(Out(run, ".pdf")));
        Assert.DoesNotContain(run.Log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public void NoEInvoiceWaitsForItsPdf()
    {
        var run = Setup();
        Drop(run, Json("NoEInvoice"), pdf: null);

        Assert.Equal(1, run.Processor.ScanOnce(run.Options, License, null, null).Held);
        Assert.True(File.Exists(Path.Combine(run.Folder, "2026000042_1.json")));
        Assert.False(Directory.Exists(Path.Combine(run.Folder, "out")));

        byte[] pdf = ErpPdf();
        File.WriteAllBytes(Path.Combine(run.Folder, "2026000042_1.pdf"), pdf);
        Assert.Equal(1, run.Processor.ScanOnce(run.Options, License, null, null).Processed);
        Assert.Equal(pdf, File.ReadAllBytes(Out(run, ".pdf")));
    }

    [Fact]
    public void NoEInvoiceWithCompressPdfIsCompressedAsAPdfAndStillHasNoXml()
    {
        var run = Setup(compress: true);
        byte[] pdf = ErpPdf();
        Drop(run, Json("NoEInvoice"), pdf);

        Assert.Equal(1, run.Processor.ScanOnce(run.Options, License, null, null).Processed);

        Assert.True(new FileInfo(Out(run, ".pdf")).Length < pdf.Length);
        Assert.Null(HybridPdf.ExtractXml(Out(run, ".pdf")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(run.Folder, "out"), "*.xml"));
        Assert.StartsWith("Compressed ", Assert.Single(run.Ledger.Events("2026000042", 100), e => e.Step == EventSteps.Compressed).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("XRechnung")]
    [InlineData("SomethingElse")]
    public void OtherValuesStillGiveXRechnung(string eInvoiceType)
    {
        var run = Setup();
        Drop(run, Json(eInvoiceType), ErpPdf());

        Assert.Equal(1, run.Processor.ScanOnce(run.Options, License, null, null).Processed);

        Assert.True(File.Exists(Out(run, ".xml")));
        Assert.False(File.Exists(Out(run, ".pdf")));
        Assert.Equal("xrechnung", Assert.Single(run.Ledger.Metrics(10).Recent).Format);
        Assert.Contains("e-invoice format XRechnung", Assert.Single(run.Ledger.Events("2026000042", 100), e => e.Step == EventSteps.Read).Message);
    }

    [Theory]
    [InlineData("NoEInvoice", ".pdf")]
    [InlineData("ZUGFeRD", ".pdf")]
    [InlineData("XRechnung", ".xml")]
    [InlineData("", ".xml")]
    public void OutputNameFollowsTheFormat(string eInvoiceType, string extension)
    {
        var data = AxDocument.Parse(Json(eInvoiceType));
        Assert.Equal("2026000042" + extension, OutputFileNames.For(DocumentHandlerKind.EInvoice, data, "2026000042"));
        Assert.Equal("2026000042.pdf", OutputFileNames.For(DocumentHandlerKind.Pdf, data, "2026000042"));
    }
}