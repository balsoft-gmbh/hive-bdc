using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Documents.Word;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Balsoft.Hive.EInvoice.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Small PDFs written object by object, so a test controls every key an image or stream has.</summary>
internal static class TestPdf
{
    public static byte[] Build(IReadOnlyList<(string Dict, byte[]? Stream)> objects, string trailerExtra = "")
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n");
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            var (dict, stream) = objects[i];
            W($"{i + 1} 0 obj\n");
            if (stream is null) W(dict + "\nendobj\n");
            else
            {
                int close = dict.LastIndexOf(">>", StringComparison.Ordinal);
                W(dict[..close] + $" /Length {stream.Length} >>\nstream\n");
                ms.Write(stream);
                W("\nendstream\nendobj\n");
            }
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>RGB samples with smooth gradients and fine detail, as a scanned logo or photo has.</summary>
    public static byte[] Rgb(int width, int height, int seed = 1)
    {
        var data = new byte[width * height * 3];
        var random = new Random(seed);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                data[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                data[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                data[i + 2] = (byte)random.Next(256);
            }
        return data;
    }

    public static byte[] Gray(int width, int height)
    {
        var data = new byte[width * height];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i % width) * 255 / Math.Max(1, width - 1));
        return data;
    }

    public static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    /// <summary>One page drawing image /Im1 at each of the given sizes in points.</summary>
    public static List<(string, byte[]?)> ImagePage(int width, int height, params double[] placedPoints)
    {
        var content = new StringBuilder();
        foreach (double size in placedPoints) content.Append($"q {size} 0 0 {size} 20 20 cm /Im1 Do Q\n");
        content.Append("BT /F1 12 Tf 40 800 Td (Rechnung) Tj ET\n");
        return new List<(string, byte[]?)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im1 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R >>", null),
            ("<< /Filter /FlateDecode >>", Flate(Encoding.ASCII.GetBytes(content.ToString()))),
            ($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode >>",
                Flate(Rgb(width, height))),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
        };
    }

    public static PdfDocument Open(byte[] pdf) => PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);

    public static PdfDictionary Image(PdfDocument doc, string name = "/Im1")
        => (PdfDictionary)doc.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements.GetReference(name)!.Value;
}

public sealed class PdfCompressorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-compress-" + Guid.NewGuid());

    public PdfCompressorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static (byte[] Pdf, PdfCompression Result) Compressed(byte[] pdf, bool keepPdfA = true)
    {
        var (bytes, result) = PdfCompressor.Compress(pdf, keepPdfA);
        Assert.True(result.Compressed, result.Summary);
        Assert.NotNull(bytes);
        Assert.Equal(bytes.Length, result.After);
        return (bytes, result);
    }

    // ── images ────────────────────────────────────────────────────────────────

    [Fact]
    public void LosslessImageAbove300PpiIsScaledTo200Ppi()
    {
        byte[] pdf = TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144));   // 1200 px over 2 in = 600 ppi

        var (result, info) = Compressed(pdf);

        using var doc = TestPdf.Open(result);
        var image = TestPdf.Image(doc);
        Assert.Equal(400, image.Elements.GetInteger("/Width"));
        Assert.Equal(400, image.Elements.GetInteger("/Height"));
        Assert.Equal("/FlateDecode", image.Elements.GetName("/Filter"));
        Assert.True(info.After < info.Before / 3, info.Summary);
        Assert.Matches(@"^Compressed [\d.]+ (KB|MB|B) to [\d.]+ (KB|MB|B) \(\d+% smaller\)\.$", info.Summary);
    }

    [Fact]
    public void ImageAtOrBelow300PpiKeepsItsSize()
    {
        byte[] pdf = TestPdf.Build(TestPdf.ImagePage(600, 600, 144));   // 300 ppi: not above the threshold
        var (bytes, _) = PdfCompressor.Compress(pdf, keepPdfA: true);
        using var doc = TestPdf.Open(bytes ?? pdf);
        Assert.Equal(600, TestPdf.Image(doc).Elements.GetInteger("/Width"));
    }

    [Fact]
    public void TheLargestPlacementDecides()
    {
        byte[] pdf = TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144, 432));   // 600 ppi small, 200 ppi large
        var (bytes, _) = PdfCompressor.Compress(pdf, keepPdfA: true);
        using var doc = TestPdf.Open(bytes ?? pdf);
        Assert.Equal(1200, TestPdf.Image(doc).Elements.GetInteger("/Width"));
    }

    [Fact]
    public void SoftMaskIsScaledWithItsImage()
    {
        var objects = TestPdf.ImagePage(900, 600, 108);   // a square of 1.5 in: 600 ppi across, 400 ppi down; the lower one decides
        objects[4] = ("<< /Type /XObject /Subtype /Image /Width 900 /Height 600 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask 7 0 R >>",
            TestPdf.Flate(TestPdf.Rgb(900, 600)));
        objects.Add(("<< /Type /XObject /Subtype /Image /Width 900 /Height 600 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode >>",
            TestPdf.Flate(TestPdf.Gray(900, 600))));

        var (result, _) = Compressed(TestPdf.Build(objects));

        using var doc = TestPdf.Open(result);
        var image = TestPdf.Image(doc);
        var mask = (PdfDictionary)image.Elements.GetReference("/SMask")!.Value;
        Assert.Equal(450, image.Elements.GetInteger("/Width"));
        Assert.Equal(300, image.Elements.GetInteger("/Height"));
        Assert.Equal(450, mask.Elements.GetInteger("/Width"));
        Assert.Equal(300, mask.Elements.GetInteger("/Height"));
    }

    [Fact]
    public void JpegKeepsItsBytesAndLosesTheAsciiWrapper()
    {
        byte[] jpeg = new byte[60_000];
        new Random(7).NextBytes(jpeg);
        jpeg[0] = 0xFF; jpeg[1] = 0xD8;
        byte[] wrapped = PdfSharp.Pdf.Filters.Filtering.Encode(jpeg, "/ASCII85Decode");
        var objects = TestPdf.ImagePage(1200, 1200, 72);   // 1200 ppi, but a JPEG is never re-encoded
        objects[4] = ("<< /Type /XObject /Subtype /Image /Width 1200 /Height 1200 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter [/ASCII85Decode /DCTDecode] >>", wrapped);

        var (result, _) = Compressed(TestPdf.Build(objects));

        using var doc = TestPdf.Open(result);
        var image = TestPdf.Image(doc);
        Assert.Equal("/DCTDecode", image.Elements.GetName("/Filter"));
        Assert.Equal(jpeg, image.Stream.Value);
        Assert.Equal(1200, image.Elements.GetInteger("/Width"));
    }

    [Fact]
    public void IdenticalImagesAreStoredOnce()
    {
        var objects = TestPdf.ImagePage(300, 300, 144);
        objects[2] = ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im1 5 0 R /Im2 7 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R >>", null);
        objects[3] = ("<< >>", Encoding.ASCII.GetBytes("q 144 0 0 144 20 20 cm /Im1 Do Q q 144 0 0 144 220 20 cm /Im2 Do Q"));
        objects.Add(objects[4]);

        var (result, _) = Compressed(TestPdf.Build(objects));

        using var doc = TestPdf.Open(result);
        var xobjects = doc.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!;
        Assert.Same(xobjects.Elements.GetReference("/Im1")!.Value, xobjects.Elements.GetReference("/Im2")!.Value);
        Assert.Equal(1, doc.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => d.Elements.GetName("/Subtype") == "/Image"));
    }

    [Fact]
    public void UncompressedContentGetsFlateAndShowsTheSame()
    {
        string content = string.Concat(Enumerable.Range(0, 300).Select(i => $"BT /F1 9 Tf 40 {800 - i * 2} Td (Zeile {i} der Rechnung) Tj ET\n"));
        var objects = TestPdf.ImagePage(10, 10, 10);
        objects[3] = ("<< >>", Encoding.ASCII.GetBytes(content));
        byte[] pdf = TestPdf.Build(objects);

        var (result, _) = Compressed(pdf);

        using var doc = TestPdf.Open(result);
        Assert.Equal(content + "\n", Encoding.ASCII.GetString(PdfImageScaler.PageContent(doc.Pages[0])));
    }

    [Fact]
    public void TheSaveAddsNoTransparencyGroupAndKeepsTheDocumentsOwnXmp()
    {
        var plain = TestPdf.ImagePage(1200, 1200, 144);
        var (noXmp, _) = Compressed(TestPdf.Build(plain));
        using (var doc = TestPdf.Open(noXmp))
        {
            Assert.False(doc.Pages[0].Elements.ContainsKey("/Group"));
            Assert.False(doc.Internals.Catalog.Elements.ContainsKey("/Metadata"));
        }

        var withXmp = TestPdf.ImagePage(1200, 1200, 144);
        withXmp[0] = ("<< /Type /Catalog /Pages 2 0 R /Metadata 7 0 R >>", null);
        withXmp[2] = ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im1 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 4 0 R /Group << /S /Transparency /CS /DeviceCMYK /I true >> >>", null);
        withXmp.Add(("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><!-- the document's own --></x:xmpmeta>")));
        var (kept, _) = Compressed(TestPdf.Build(withXmp));
        using (var doc = TestPdf.Open(kept))
        {
            var group = doc.Pages[0].Elements.GetDictionary("/Group")!;
            Assert.Equal("/DeviceCMYK", group.Elements.GetName("/CS"));
            Assert.True(group.Elements.GetBoolean("/I"));
            var xmp = (PdfDictionary)doc.Internals.Catalog.Elements.GetReference("/Metadata")!.Value;
            Assert.Contains("the document's own", Encoding.UTF8.GetString(xmp.Stream.Value));
        }
    }

    [Fact]
    public void AnImageStreamThatInflatesFarBeyondItsSizeIsLeftAlone()
    {
        byte[] bomb = TestPdf.Flate(new byte[60_000_000]);   // claims 1200 x 1200 RGB (4.3 MB), inflates to 60 MB
        var objects = TestPdf.ImagePage(1200, 1200, 144);
        objects[4] = ("<< /Type /XObject /Subtype /Image /Width 1200 /Height 1200 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode >>", bomb);

        Assert.Throws<InvalidDataException>(() => PdfImageScaler.Inflate(bomb, 1_000_000));
        var (bytes, _) = PdfCompressor.Compress(TestPdf.Build(objects), keepPdfA: true);
        using var doc = TestPdf.Open(bytes ?? TestPdf.Build(objects));
        Assert.Equal(1200, TestPdf.Image(doc).Elements.GetInteger("/Width"));
    }

    // ── guards ────────────────────────────────────────────────────────────────

    [Fact]
    public void SignedPdfIsLeftAlone()
    {
        var objects = TestPdf.ImagePage(1200, 1200, 144);
        objects[0] = ("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [8 0 R] /SigFlags 3 >> >>", null);
        objects.Add(("<< /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 100 200 300] /Contents <00> >>", null));
        objects.Add(("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /V 7 0 R /Rect [0 0 0 0] /P 3 0 R >>", null));
        var (bytes, result) = PdfCompressor.Compress(TestPdf.Build(objects), keepPdfA: true);
        Assert.Null(bytes);
        Assert.Equal("Not compressed: signed PDF.", result.Summary);

        // The same signature fields in an object stream, where the raw bytes show no /ByteRange: the catalog flags it.
        objects.RemoveAt(6);
        objects[0] = ("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /SigFlags 3 >> >>", null);
        Assert.Equal("signed PDF", PdfCompressor.Compress(TestPdf.Build(objects), keepPdfA: true).Result.NotCompressedReason);
    }

    [Fact]
    public void EncryptedPdfIsLeftAlone()
    {
        var objects = TestPdf.ImagePage(1200, 1200, 144);
        objects.Add(("<< /Filter /Standard /V 2 /R 3 /O <00> /U <00> /P -4 >>", null));
        var (bytes, result) = PdfCompressor.Compress(TestPdf.Build(objects, " /Encrypt 7 0 R"), keepPdfA: true);
        Assert.Null(bytes);
        Assert.Equal("encrypted PDF", result.NotCompressedReason);
    }

    [Fact]
    public void PdfAIsLeftAloneOnlyWhenAsked()
    {
        var objects = TestPdf.ImagePage(1200, 1200, 144);
        objects[0] = ("<< /Type /Catalog /Pages 2 0 R /Metadata 7 0 R >>", null);
        objects.Add(("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"3\" pdfaid:conformance=\"B\"/></rdf:RDF></x:xmpmeta>")));
        byte[] pdf = TestPdf.Build(objects);

        var (kept, result) = PdfCompressor.Compress(pdf, keepPdfA: true);
        Assert.Null(kept);
        Assert.Equal("PDF/A document", result.NotCompressedReason);
        Compressed(pdf, keepPdfA: false);   // a ZUGFeRD carrier: the library writes the PDF/A parts afterwards
    }

    [Fact]
    public void WhatIsNoPdfKeepsTheOriginal()
    {
        var (bytes, result) = PdfCompressor.Compress(Encoding.ASCII.GetBytes("Synthetic pass-through fixture"), keepPdfA: true);
        Assert.Null(bytes);
        Assert.Equal("the PDF could not be read", result.NotCompressedReason);
    }

    [Fact]
    public void ASecondPassFindsNothingToGain()
    {
        var (once, _) = Compressed(TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144)));
        var (twice, result) = PdfCompressor.Compress(once, keepPdfA: true);
        Assert.Null(twice);
        Assert.Equal("already compact", result.NotCompressedReason);
    }

    [Fact]
    public void SameInputGivesTheSameSizeAndContent()
    {
        byte[] pdf = TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144));
        var (a, _) = Compressed(pdf);
        var (b, _) = Compressed(pdf);
        Assert.Equal(a.Length, b.Length);
        using var da = TestPdf.Open(a);
        using var db = TestPdf.Open(b);
        Assert.Equal(TestPdf.Image(da).Stream.Value, TestPdf.Image(db).Stream.Value);
    }

    [Fact]
    public void TheTimeLimitKeepsTheOriginal()
    {
        var (bytes, result) = PdfCompressor.Compress(TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144)), keepPdfA: true, timeLimit: TimeSpan.Zero);
        Assert.Null(bytes);
        Assert.StartsWith("took longer than", result.NotCompressedReason);
    }

    [Fact]
    public void CompressFileReplacesTheFileOnlyWhenItGotSmaller()
    {
        string pdf = Path.Combine(_root, "a.pdf"), text = Path.Combine(_root, "b.pdf");
        File.WriteAllBytes(pdf, TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144)));
        File.WriteAllText(text, "Synthetic pass-through fixture");
        long before = new FileInfo(pdf).Length;

        Assert.True(PdfCompressor.CompressFile(pdf, keepPdfA: true).Compressed);
        Assert.False(PdfCompressor.CompressFile(text, keepPdfA: true).Compressed);

        Assert.True(new FileInfo(pdf).Length < before);
        Assert.Equal("Synthetic pass-through fixture", File.ReadAllText(text));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SummaryNamesSizesAndReason()
    {
        Assert.Equal("Compressed 977 KB to 98 KB (90% smaller).", new PdfCompression(1_000_000, 100_000).Summary);
        Assert.Equal("Compressed 1.4 MB to 160 KB (89% smaller).", new PdfCompression(1_468_006, 163_840).Summary);
        Assert.Equal("Not compressed: signed PDF.", new PdfCompression(5, 5, "signed PDF").Summary);
    }

    // ── settings ──────────────────────────────────────────────────────────────

    [Fact]
    public void CompressPdfFollowsTheTypeUnlessTheCompanySetsIt()
    {
        var type = new DocumentTypeOptions { Id = "PurchaseOrder", CompressPdf = true };
        var inherits = new DocumentRoute { DataAreaId = "A", Input = _root };
        var off = new DocumentRoute { DataAreaId = "B", Input = _root, CompressPdf = false };
        Assert.True(inherits.CompressesFor(type));
        Assert.False(off.CompressesFor(type));
        Assert.False(new DocumentRoute().CompressesFor(new DocumentTypeOptions()));
        Assert.Null(inherits.Clone().CompressPdf);
        Assert.Null(off.Clone().CompressPdf);   // an auto-registered type follows its own default, as with Mail
    }

    [Fact]
    public void SettingsFileWritesTheSwitchOnlyWhenSetAndKeepsItForOldDashboards()
    {
        var type = new DocumentTypeOptions
        {
            Id = "SalesInvoice",
            Companies = { new DocumentRoute { DataAreaId = "001", Input = @"C:\Export" }, new DocumentRoute { DataAreaId = "002", Input = @"C:\Export", CompressPdf = true } },
        };
        var node = SettingsFile.DocumentTypeNode(type);
        Assert.False(node.ContainsKey("CompressPdf"));
        Assert.False(SettingsFile.RouteNode(type.Companies[0]).ContainsKey("CompressPdf"));
        Assert.True(SettingsFile.RouteNode(type.Companies[1])["CompressPdf"]!.GetValue<bool>());

        type.CompressPdf = true;
        var section = new System.Text.Json.Nodes.JsonObject { ["DocumentTypes"] = new System.Text.Json.Nodes.JsonArray(SettingsFile.DocumentTypeNode(type)) };
        Assert.True(SettingsFile.StoredCompressPdf(section, "salesinvoice"));
        Assert.Null(SettingsFile.StoredRouteCompressPdf(section, "SalesInvoice", "001", @"C:\Export\"));
        Assert.True(SettingsFile.StoredRouteCompressPdf(section, "SalesInvoice", "002", @"C:\Export"));
        Assert.False(SettingsFile.StoredCompressPdf(null, "SalesInvoice"));

        var reloaded = JsonSerializer.Deserialize<DocumentTypeOptions>(section["DocumentTypes"]![0]!.ToJsonString())!;
        Assert.True(reloaded.CompressPdf);
        Assert.Null(reloaded.Companies[0].CompressPdf);
        Assert.True(reloaded.Companies[1].CompressPdf);
        Assert.False(JsonSerializer.Deserialize<DocumentTypeOptions>("""{ "Id": "SalesInvoice" }""")!.CompressPdf);
    }

    // ── the pipeline ──────────────────────────────────────────────────────────

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "einvoice", "documents" } },
    };

    private (DocumentProcessor Processor, ExportLedger Ledger) Processor(string name)
    {
        var ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, name + ".db"));
        return (new DocumentProcessor(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, name + ".json"), NullLogger.Instance), ledger), ledger);
    }

    private BDCOptions Options(string id, DocumentHandlerKind handler, string folder, bool compress)
    {
        var options = new BDCOptions();
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = id, Handler = handler, Mail = false,
            Companies = { new DocumentRoute { DataAreaId = "1000", Input = folder, Output = Path.Combine(folder, "out"), CompressPdf = compress } },
        });
        options.Normalize();
        return options;
    }

    [Fact]
    public void PdfDocumentIsDeliveredCompressedAndTheErpPdfStaysAsItWas()
    {
        string folder = Path.Combine(_root, "po");
        Directory.CreateDirectory(folder);
        byte[] original = TestPdf.Build(TestPdf.ImagePage(1200, 1200, 144));
        File.WriteAllText(Path.Combine(folder, "PO-1_1.json"), JsonSerializer.Serialize(new { documentType = "PurchaseOrder", dataAreaId = "1000", documentNumber = "PO-1" }));
        File.WriteAllBytes(Path.Combine(folder, "PO-1_1.pdf"), original);
        var (processor, ledger) = Processor("po");

        Assert.Equal(1, processor.ScanOnce(Options("PurchaseOrder", DocumentHandlerKind.Pdf, folder, compress: true), License, null, null).Processed);

        long delivered = new FileInfo(Path.Combine(folder, "out", "PO-1.pdf")).Length;
        Assert.True(delivered < original.Length / 3, $"{delivered:N0} of {original.Length:N0} bytes");
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder, "processed", "PO-1_1.pdf")));
        var step = Assert.Single(ledger.Events("PO-1", 100), e => e.Step == EventSteps.Compressed);
        Assert.StartsWith("Compressed ", step.Message);
    }

    [Fact]
    public void ADocumentThatCannotBeCompressedIsStillDelivered()
    {
        string folder = Path.Combine(_root, "text");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "PO-2_1.json"), JsonSerializer.Serialize(new { documentType = "PurchaseOrder", dataAreaId = "1000", documentNumber = "PO-2" }));
        File.WriteAllText(Path.Combine(folder, "PO-2_1.pdf"), "Synthetic pass-through fixture");
        var (processor, ledger) = Processor("text");

        Assert.Equal(1, processor.ScanOnce(Options("PurchaseOrder", DocumentHandlerKind.Pdf, folder, compress: true), License, null, null).Processed);

        Assert.Equal("Synthetic pass-through fixture", File.ReadAllText(Path.Combine(folder, "out", "PO-2.pdf")));
        Assert.Equal("Not compressed: the PDF could not be read.", ledger.Events("PO-2", 100).Single(e => e.Step == EventSteps.Compressed).Message);
    }

    /// <summary>A carrier as the AX 2009 report writer prints it: Type0 Arial, bold named plain "Arial", no font program.</summary>
    private static byte[] Ax2009Carrier()
    {
        SystemFontResolver.Install();
        var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Muster Lieferant · Rechnung 2026000001", new XFont("Arial", 16, XFontStyleEx.Bold, options), XBrushes.Black, 40, 60);
            g.DrawString("Beratung   2 × 150,00 €   300,00 €", new XFont("Arial", 11, XFontStyleEx.Regular, options), XBrushes.Black, 40, 100);
        }
        using var saved = new MemoryStream();
        doc.Save(saved, false);
        using var carrier = PdfReader.Open(new MemoryStream(saved.ToArray()), PdfDocumentOpenMode.Modify);
        var fonts = carrier.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!;
        foreach (var key in fonts.Elements.Keys)
        {
            var type0 = fonts.Elements.GetDictionary(key)!;
            var cid = (PdfDictionary)((PdfReference)type0.Elements.GetArray("/DescendantFonts")!.Elements[0]).Value;
            var descriptor = cid.Elements.GetDictionary("/FontDescriptor")!;
            bool bold = type0.Elements.GetName("/BaseFont").Contains("Bold", StringComparison.Ordinal);
            type0.Elements["/BaseFont"] = new PdfName("/Arial");
            cid.Elements["/BaseFont"] = new PdfName("/Arial");
            cid.Elements.Remove("/CIDToGIDMap");
            descriptor.Elements["/FontName"] = new PdfName("/Arial");
            descriptor.Elements["/Flags"] = new PdfInteger(bold ? 262176 : 32);
            descriptor.Elements.Remove("/FontFile2");
        }
        using var output = new MemoryStream();
        carrier.Save(output, false);
        return output.ToArray();
    }

    private static string ZugferdJson() => """
        {
          "documentType": "SalesInvoice", "dataAreaId": "1000", "documentNumber": "2026000001", "eInvoiceType": "ZUGFeRD",
          "issueDate": "2026-09-22", "dueDate": "2026-10-22", "currency": "EUR", "vatRate": "19",
          "senderName": "Muster Lieferant", "senderCountry": "DE", "senderVatId": "DE123456789", "senderStreet": "Weg 1",
          "senderPostcode": "10115", "senderCity": "Berlin", "senderEmail": "rechnung@example.com", "senderPhone": "+49 30 000000",
          "recipientName": "Balsoft", "recipientCountry": "DE", "recipientPostcode": "20095", "recipientCity": "Hamburg",
          "recipientEmail": "ap@example.com", "buyerReference": "04011000-12345-67", "bankIban": "DE89370400440532013000",
          "lineItems": [ { "description": "Beratung", "quantity": "2", "unit": "piece", "unitPrice": "150.00" } ]
        }
        """;

    [InstalledFontFact("Arial")]
    public void ZugferdFromAnAx2009CarrierEmbedsOnlyTheGlyphsUsed()
    {
        byte[] carrier = Ax2009Carrier();
        var sizes = new Dictionary<bool, long>();
        var xml = new Dictionary<bool, byte[]?>();
        foreach (bool compress in new[] { false, true })
        {
            string folder = Path.Combine(_root, compress ? "zugferd-on" : "zugferd-off");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "2026000001_1.json"), ZugferdJson());
            File.WriteAllBytes(Path.Combine(folder, "2026000001_1.pdf"), carrier);
            var (processor, ledger) = Processor(compress ? "on" : "off");

            Assert.Equal(1, processor.ScanOnce(Options("SalesInvoice", DocumentHandlerKind.EInvoice, folder, compress), License, null, null).Processed);

            string output = Path.Combine(folder, "out", "2026000001.pdf");
            sizes[compress] = new FileInfo(output).Length;
            xml[compress] = HybridPdf.ExtractXml(output);
            var step = ledger.Events("2026000001", 100).Where(e => e.Step == EventSteps.Compressed).ToList();
            if (compress) Assert.StartsWith("Compressed ", Assert.Single(step).Message);
            else Assert.Empty(step);
        }
        Assert.True(sizes[true] < sizes[false] / 3, $"compressed {sizes[true]:N0}, plain {sizes[false]:N0} bytes");
        Assert.NotNull(xml[true]);
        Assert.Equal(xml[false], xml[true]);
    }
}
