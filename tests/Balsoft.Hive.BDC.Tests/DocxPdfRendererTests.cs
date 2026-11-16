using Balsoft.Hive.BDC.Documents.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class DocxPdfRendererTests
{
    [Fact]
    public void FilledTemplateRendersToOnePdfPageWithoutUnsupportedElements()
    {
        var result = Render(WordTestDocuments.Build(WordTestDocuments.Filled()));
        Assert.Empty(result.Unsupported);
        Assert.Equal(1, result.Pages);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf, 0, 5));
        using var pdf = PdfReader.Open(new MemoryStream(result.Pdf), PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
        Assert.Equal(595, (int)pdf.Pages[0].Width.Point);
    }

    [Fact]
    public void LongInvoiceBreaksIntoPagesWithHeaderFooterAndPageCountOnEach()
    {
        var c = WordTestDocuments.Filled();
        for (int i = 3; i <= 60; i++)
            c.Lines.Add(new() { ["BT-126"] = $"{i}.0", ["BT-153"] = $"Position {i}", ["BT-154"] = "Beschreibung\nzweite Zeile",
                ["BT-129"] = "1", ["BT-130"] = "LS", ["BT-146"] = "10,00", ["BT-152"] = "19", ["BT-131"] = "10,00" });
        var bytes = WordTestDocuments.Build(c);
        var result = Render(bytes);
        Assert.True(result.Pages >= 3, $"pages: {result.Pages}");
        using var pdf = PdfReader.Open(new MemoryStream(result.Pdf), PdfDocumentOpenMode.Import);
        Assert.Equal(result.Pages, pdf.PageCount);
        var pages = PageText(bytes);
        for (int i = 0; i < pages.Count; i++)
        {
            Assert.Contains($"Seite {i + 1} von {pages.Count}", pages[i]);   // header on every page
            Assert.Contains("DE02120300000000202051", pages[i]);              // footer on every page
        }
        Assert.Contains("Position 60", pages[^1]);
    }

    [Fact]
    public void PaginationCutsBetweenLinesAndKeepsUnsplittableRowsWhole()
    {
        var g = new Galley { Height = 300 };
        for (double y = 0; y < 300; y += 12) g.Keep.Add((y, y + 12));   // text lines of 12 pt
        g.Keep.Add((72, 150));                                          // a row that cannot split, starting on a line boundary
        var cuts = DocxPdfRenderer.Paginate(g, _ => 0, _ => 100);
        Assert.Equal(0, cuts[0].From);
        Assert.Equal(72, cuts[0].To);          // 84 and 96 would split the row; the row moves whole to page 2
        Assert.Equal(72, cuts[1].From);
        Assert.Equal(300, cuts[^1].To);
        Assert.All(cuts, c => Assert.DoesNotContain(g.Keep, k => k.Top < c.To - 0.01 && k.Bottom > c.To + 0.01 && c.To < 300));
    }

    [Fact]
    public void HardPageBreakStartsANewPage()
    {
        var g = new Galley { Height = 50 };
        g.Keep.Add((0, 12));
        g.Keep.Add((12, 24));
        g.Breaks.Add(12);
        var cuts = DocxPdfRenderer.Paginate(g, _ => 0, _ => 500);
        Assert.Equal(new[] { (0d, 12d), (12d, 50d) }, cuts);
    }

    [Fact]
    public void PageFieldsShowPageAndTotal()
    {
        var bytes = Document(body => body.Append(
            new Paragraph(new Run(new Text("Seite ") { Space = SpaceProcessingModeValues.Preserve }),
                new SimpleField(new Run(new Text("1"))) { Instruction = " PAGE " },
                new Run(new Text(" von ") { Space = SpaceProcessingModeValues.Preserve }),
                new SimpleField(new Run(new Text("1"))) { Instruction = " NUMPAGES " }),
            new Paragraph(new Run(new Break { Type = BreakValues.Page })),
            new Paragraph(new Run(new Text("zweite Seite")))));
        var pages = PageText(bytes);
        Assert.Equal(2, pages.Count);
        Assert.Contains("Seite 1 von 2", pages[0]);
        Assert.Contains("zweite Seite", pages[1]);
    }

    [Theory]
    [InlineData("textbox", "shapes or text boxes")]
    [InlineData("floating", "floating images or shapes")]
    [InlineData("list", "numbered or bulleted lists")]
    public void UnsupportedElementsAreReportedNotDrawnWrong(string kind, string expected)
    {
        var bytes = Document(body =>
        {
            switch (kind)
            {
                case "textbox":
                    body.Append(new Paragraph(new Run(new Picture(new DocumentFormat.OpenXml.Vml.Shape(
                        new DocumentFormat.OpenXml.Vml.TextBox(new TextBoxContent(new Paragraph(new Run(new Text("im Textfeld"))))))))));
                    break;
                case "floating":
                    body.Append(new Paragraph(new Run(new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor()))));
                    break;
                case "list":
                    body.Append(new Paragraph(new ParagraphProperties(new NumberingProperties(new NumberingId { Val = 1 })), new Run(new Text("Punkt"))));
                    break;
            }
        });
        Assert.Contains(expected, Render(bytes).Unsupported);
        using var ms = new MemoryStream(bytes);
        Assert.Contains(expected, DocxPdfRenderer.FindUnsupported(ms));
    }

    [Fact]
    public void InlineImagesAreDrawnWithoutInterpolationForPdfA()
    {
        // 1x1 PNG; PDF/A-3 (the ZUGFeRD carrier) rejects /Interpolate true (ISO 19005-3, 6.2.8).
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var image = main.AddImagePart(ImagePartType.Png);
            using (var s = new MemoryStream(png)) image.FeedData(s);
            var shape = new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.ImageData { RelationshipId = main.GetIdOfPart(image) })
                { Style = "width:20pt;height:20pt" };
            main.Document = new Document(new Body(new Paragraph(new Run(new Picture(shape)))));
        }
        var result = Render(ms.ToArray());
        Assert.Empty(result.Unsupported);
        string pdf = System.Text.Encoding.Latin1.GetString(result.Pdf);
        Assert.Contains("/Subtype/Image", pdf);
        Assert.DoesNotContain("/Interpolate true", pdf);
    }

    [Fact]
    public void MissingFontFallsBackAndIsReported()
    {
        var bytes = Document(body => body.Append(new Paragraph(new Run(
            new RunProperties(new RunFonts { Ascii = "Hive Missing Sans" }), new Text("Text")))));
        var result = Render(bytes);
        Assert.Contains("Hive Missing Sans", result.MissingFonts);
        Assert.Equal(1, result.Pages);
    }

    [Fact]
    public void GermanTextAndNumbersSurviveIntoThePdf()
    {
        var text = string.Concat(PageText(WordTestDocuments.Build(WordTestDocuments.Filled())));
        Assert.Contains("RE-2026-0042", text);
        Assert.Contains("Modulgebäude", text);
        Assert.Contains("2.975,00", text);
    }

    private static WordRenderResult Render(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        return DocxPdfRenderer.Render(ms, "test");
    }

    private static byte[] Document(Action<Body> fill)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            fill(body);
            body.Append(new SectionProperties(new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 1417, Bottom = 1134, Left = 1417, Right = 1417, Header = 708, Footer = 708, Gutter = 0U }));
            doc.AddMainDocumentPart().Document = new Document(body);
        }
        return ms.ToArray();
    }

    /// <summary>The strings the renderer drew, one entry per page.</summary>
    private static List<string> PageText(byte[] docx)
    {
        var pages = new List<string>();
        using var ms = new MemoryStream(docx);
        DocxPdfRenderer.Render(ms, "test", null, pages);
        return pages;
    }
}