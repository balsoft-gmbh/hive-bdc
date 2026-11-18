using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Documents.Word;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

[Collection("WordIntake")]
public sealed class WordIntakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-word-" + Guid.NewGuid());
    private readonly string _input, _output;
    private readonly ExportLedger _ledger;

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "einvoice", "documents" } },
    };

    public WordIntakeTests()
    {
        _input = Path.Combine(_root, "in");
        _output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_input);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
        DocumentProcessor.WordSettleTime = TimeSpan.Zero;
    }

    public void Dispose()
    {
        DocumentProcessor.WordSettleTime = TimeSpan.FromSeconds(10);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private BDCOptions Options(string inputFormat = InputFormats.Word)
    {
        var options = new BDCOptions();
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "SalesInvoice", Handler = DocumentHandlerKind.EInvoice, Mail = false,
            Companies = { new DocumentRoute { DataAreaId = "1000", Input = _input, Output = _output, InputFormat = inputFormat } },
        });
        options.Normalize();
        return options;
    }

    private DocumentProcessor.ScanResult Scan(BDCOptions? options = null)
        => new DocumentProcessor(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger)
            .ScanOnce(options ?? Options(), License, null, null);

    private string Drop(string name, WordInvoiceContent? content = null, byte[]? bytes = null)
    {
        string path = Path.Combine(_input, name);
        File.WriteAllBytes(path, bytes ?? WordTestDocuments.Build(content ?? WordTestDocuments.Filled()));
        return path;
    }

    private string Processed(string name) => Path.Combine(_input, "processed", name);
    private string Error(string name) => Path.Combine(_input, "error", name);

    [Fact]
    public void FilledWordInvoiceBecomesAnXRechnungAndMovesToProcessed()
    {
        Drop("RE_2026_0042.docx");
        var result = Scan();
        Assert.Equal(1, result.Processed);
        string xml = File.ReadAllText(Path.Combine(_output, "RE-2026-0042.xml"));
        Assert.Contains(">875<", xml);                       // partial construction invoice
        Assert.Contains("schemeID=\"0204\"", xml);           // Leitweg-ID as buyer electronic address
        Assert.Contains("Erika Muster", xml);                // BT-41 seller contact
        Assert.Contains("2975.00", xml);
        Assert.True(File.Exists(Processed("RE_2026_0042.docx")));
        Assert.True(File.Exists(Processed("RE-2026-0042_word.json")));
        Assert.True(File.Exists(Processed("RE-2026-0042_word.pdf")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_input, "work")));
        Assert.Equal("word", _ledger.Metrics(5).Recent.Single().Source);
    }

    [Fact]
    public void ZugferdChoiceInTheDocumentProducesAHybridPdf()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["eInvoiceType"] = "zugferd";
        c.Fields["BT-49"] = "einkauf@kunde.example";
        Drop("zugferd.docx", c);
        Assert.Equal(1, Scan().Processed);
        byte[] pdf = File.ReadAllBytes(Path.Combine(_output, "RE-2026-0042.pdf"));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("xml", System.Text.Encoding.Latin1.GetString(pdf));   // the embedded invoice
        Assert.False(File.Exists(Path.Combine(_output, "RE-2026-0042.xml")));
    }

    [Fact]
    public void PdfSavedWithTheDocumentIsUsedInsteadOfRendering()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["eInvoiceType"] = "zugferd";
        c.Fields["BT-49"] = "einkauf@kunde.example";
        Drop("sibling.docx", c);
        using (var ms = new MemoryStream(WordTestDocuments.Build(c)))
            File.WriteAllBytes(Path.Combine(_input, "sibling.pdf"), DocxPdfRenderer.Render(ms, "saved from Word").Pdf);
        byte[] sibling = File.ReadAllBytes(Path.Combine(_input, "sibling.pdf"));
        Assert.Equal(1, Scan().Processed);
        Assert.Equal(sibling, File.ReadAllBytes(Processed("sibling_word.pdf")));
        Assert.True(File.Exists(Processed("sibling.pdf")));
    }

    [Fact]
    public void Word97DocumentGoesToErrorWithAReasonFile()
    {
        Drop("alt.doc", bytes: new byte[] { 0xD0, 0xCF, 0x11, 0xE0 });
        var result = Scan();
        Assert.Equal(1, result.Errors);
        Assert.True(File.Exists(Error("alt.doc")));
        string reason = File.ReadAllText(Error("alt.doc.error.txt"));
        Assert.Contains("Hive BDC hat diese Rechnung nicht verarbeitet", reason);
        Assert.Contains("save it as Word document (.docx)", reason);
    }

    [Fact]
    public void EmptyTemplateGoesToErrorNamingTheRequiredFields()
    {
        Drop("leer.docx", new WordInvoiceContent());
        Assert.Equal(1, Scan().Errors);
        string reason = File.ReadAllText(Error("leer.docx.error.txt"));
        Assert.Contains("BT-1 invoice number", reason);
        Assert.False(File.Exists(Path.Combine(_output, "RE-2026-0042.xml")));
    }

    [Fact]
    public void WrongTotalGoesToErrorAndNoXmlIsWritten()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["BT-112"] = "2.975,01";
        Drop("summe.docx", c);
        Assert.Equal(1, Scan().Errors);
        Assert.Contains("Gross amount (BT-112): printed 2.975,01", File.ReadAllText(Error("summe.docx.error.txt")));
        Assert.False(Directory.Exists(_output) && Directory.EnumerateFiles(_output).Any());
    }

    [Fact]
    public void UnsupportedElementGoesToErrorUnlessAPdfIsSavedWithIt()
    {
        byte[] withTextBox = AddTextBox(WordTestDocuments.Build(WordTestDocuments.Filled()));
        Drop("textfeld.docx", bytes: withTextBox);
        Assert.Equal(1, Scan().Errors);
        Assert.Contains("shapes or text boxes", File.ReadAllText(Error("textfeld.docx.error.txt")));

        Drop("textfeld2.docx", bytes: withTextBox);
        using (var ms = new MemoryStream(WordTestDocuments.Build(WordTestDocuments.Filled())))
            File.WriteAllBytes(Path.Combine(_input, "textfeld2.pdf"), DocxPdfRenderer.Render(ms).Pdf);
        Assert.Equal(1, Scan().Processed);
    }

    [Fact]
    public void JustSavedOrOpenDocumentIsHeldAndLockFilesAreSkipped()
    {
        string docx = Drop("offen.docx");
        Drop("~$offen.docx", bytes: new byte[] { 1, 2, 3 });
        using (new FileStream(docx, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var held = Scan();
            Assert.Equal(1, held.Held);
            Assert.Equal(0, held.Errors);
        }
        DocumentProcessor.WordSettleTime = TimeSpan.FromHours(1);
        Assert.Equal(1, Scan().Held);
        DocumentProcessor.WordSettleTime = TimeSpan.Zero;
        var done = Scan();
        Assert.Equal(1, done.Processed);
        Assert.Equal(0, done.Errors);
        Assert.True(File.Exists(Path.Combine(_input, "~$offen.docx")));   // never touched
    }

    [Fact]
    public void JsonEntryLeavesWordFilesAlone()
    {
        string docx = Drop("liegt.docx");
        var result = Scan(Options(InputFormats.Json));
        Assert.Equal(new DocumentProcessor.ScanResult(0, 0, 0), result);
        Assert.True(File.Exists(docx));
    }

    [Fact]
    public void InputFormatIsNormalizedAndCopiedWithTheRoute()
    {
        var route = new DocumentRoute { Input = _input, InputFormat = " word " };
        route.Normalize();
        Assert.Equal(InputFormats.Word, route.InputFormat);
        Assert.True(route.Clone().IsWord);
        var unknown = new DocumentRoute { Input = _input, InputFormat = "pdf" };
        unknown.Normalize();
        Assert.Equal(InputFormats.Json, unknown.InputFormat);
    }

    [Fact]
    public void FontNotOnTheServerIsNamedInTheHistory()
    {
        byte[] docx = WordTestDocuments.Build(WordTestDocuments.Filled());
        using (var ms = new MemoryStream())
        {
            ms.Write(docx);
            using (var doc = WordprocessingDocument.Open(ms, true))
            {
                doc.MainDocumentPart!.Document!.Body!.InsertAt(new Paragraph(new Run(
                    new RunProperties(new RunFonts { Ascii = "Hive Missing Sans", HighAnsi = "Hive Missing Sans" }),
                    new Text("Vielen Dank"))), 0);
                doc.MainDocumentPart.Document.Save();
            }
            docx = ms.ToArray();
        }
        Drop("schrift.docx", bytes: docx);
        Assert.Equal(1, Scan().Processed);
        var read = _ledger.Events("schrift", 50).Single(e => e.Step == EventSteps.Read && e.Message.StartsWith("Read the Word"));
        Assert.Contains("Fonts not on this server, drawn with Arial metrics: Hive Missing Sans.", read.Message);
    }

    [Fact]
    public void InstalledFontsLeaveTheHistoryNoteAsItWas()
    {
        Drop("normal.docx");
        Assert.Equal(1, Scan().Processed);
        var read = _ledger.Events("normal", 50).Single(e => e.Step == EventSteps.Read && e.Message.StartsWith("Read the Word"));
        Assert.DoesNotContain("Fonts not on this server", read.Message);
    }

    private static byte[] AddTextBox(byte[] docx)
    {
        using var ms = new MemoryStream();
        ms.Write(docx);
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var body = doc.MainDocumentPart!.Document!.Body!;
            var shape = new DocumentFormat.OpenXml.Vml.Shape(
                new DocumentFormat.OpenXml.Vml.TextBox(new TextBoxContent(new Paragraph(new Run(new Text("Hinweis"))))));
            body.InsertAt(new Paragraph(new Run(new Picture(shape))), 0);
            doc.MainDocumentPart.Document.Save();
        }
        return ms.ToArray();
    }
}