using System.Text.Json;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Documents.Word;
using Balsoft.Hive.BDC.EInvoice;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class WordInvoiceReaderTests
{
    [Fact]
    public void TemplateIsAValidWordDocument()
    {
        foreach (var bytes in new[] { WordTestDocuments.Build(), WordTestDocuments.Build(WordTestDocuments.Filled()) })
        {
            using var ms = new MemoryStream(bytes);
            using var doc = WordprocessingDocument.Open(ms, false);
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(doc).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
        }
    }

    [Fact]
    public void FilledInvoiceBecomesTheErpJson()
    {
        var inv = WordTestDocuments.Read(WordTestDocuments.Build(WordTestDocuments.Filled()));
        Assert.Empty(inv.Problems);
        var j = inv.Json;
        Assert.Equal("RE-2026-0042", (string?)j["documentNumber"]);
        Assert.Equal("2026-06-12", (string?)j["issueDate"]);
        Assert.Equal("875", (string?)j["invoiceTypeCode"]);
        Assert.Equal("xrechnung", (string?)j["eInvoiceType"]);
        Assert.Equal("991-12345-67", (string?)j["BT-10"]);
        Assert.Equal("991-12345-67", (string?)j["BT-49"]);
        Assert.Null(j["recipientEmail"]);   // a Leitweg-ID is not a mail address
        Assert.Equal("Stadt Musterhausen Bauamt", (string?)j["recipientName"]);
        Assert.Equal("Musterbau GmbH", (string?)j["senderName"]);
        Assert.Equal("DE123456789", (string?)j["senderVatId"]);
        Assert.Equal("Erika Muster", (string?)j["BT-41"]);
        Assert.Equal("2026-03-01", (string?)j["servicePeriodFrom"]);
        Assert.Equal("Bauvorhaben Kita Sonnenweg\nHaus B", (string?)j["notes"]);
        Assert.Equal(19m, (decimal?)j["vatRate"]);
        var lines = j["lineItems"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Modulgebäude", (string?)lines[0]!["description"]);
        Assert.Equal("4 Raummodule\n-netto- 10.000,00 EUR, davon 20 %", (string?)lines[0]!["BT-154"]);
        Assert.Equal(2000m, (decimal?)lines[0]!["unitPrice"]);
        Assert.Equal("LS", (string?)lines[0]!["unit"]);
        Assert.Equal(new WordTotals(2500m, 475m, 2975m, 2500m, 475m, 2975m), inv.Totals);
        Assert.Equal("xrechnung", inv.Format);
    }

    [Fact]
    public void ReaderTotalsAgreeWithTheXmlMapper()
    {
        var inv = WordTestDocuments.Read(WordTestDocuments.Build(WordTestDocuments.Filled()));
        var data = AxDocument.Parse(inv.Json.ToJsonString());
        var totals = InvoiceMapper.Map(data).CalculateTotals();
        Assert.Equal(inv.Totals!.Net, totals.TaxExclusiveAmount);
        Assert.Equal(inv.Totals.Tax, totals.VatTotal);
        Assert.Equal(inv.Totals.Gross, totals.TaxInclusiveAmount);
    }

    [Fact]
    public void EmptyTemplateListsTheRequiredFieldsAndReadsPlaceholdersAsEmpty()
    {
        var inv = WordTestDocuments.Read(WordTestDocuments.Build());
        Assert.True(inv.HasFields);
        var problem = Assert.Single(inv.Problems, p => p.StartsWith("Required field"));
        foreach (var bt in new[] { "BT-1", "BT-2", "BT-44", "BT-10", "BT-49" })
            Assert.Contains(bt, problem);
        Assert.DoesNotContain(inv.Problems, p => p.Contains("not a date") || p.Contains("not a number"));
        Assert.Contains(inv.Problems, p => p.StartsWith("Line 1:") || p.Contains("no invoice line"));
    }

    [Fact]
    public void ZugferdNeedsNoLeitwegId()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["eInvoiceType"] = "zugferd";
        c.Fields.Remove("BT-10");
        c.Fields["BT-49"] = "einkauf@kunde.example";
        var inv = WordTestDocuments.Read(WordTestDocuments.Build(c));
        Assert.Empty(inv.Problems);
        Assert.Equal("zugferd", inv.Format);
        Assert.Equal("einkauf@kunde.example", (string?)inv.Json["recipientEmail"]);
    }

    [Fact]
    public void ZugferdWithXRechnungStillNeedsTheLeitwegId()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["eInvoiceType"] = "zugferd-xrechnung";
        c.Fields.Remove("BT-10");
        c.Fields["BT-49"] = "einkauf@kunde.example";
        var inv = WordTestDocuments.Read(WordTestDocuments.Build(c));
        Assert.Equal("zugferd", inv.Format);
        Assert.Contains(inv.Problems, p => p.StartsWith("Required field") && p.Contains("BT-10"));
    }

    [Theory]
    [InlineData("BT-112", "2.975,01", "Gross amount (BT-112): printed 2.975,01, the lines add up to 2.975,00.")]
    [InlineData("BT-109", "2.400,00", "Net amount (BT-109): printed 2.400,00, the lines add up to 2.500,00.")]
    public void PrintedTotalThatDiffersByACentIsAProblem(string tag, string printed, string expected)
    {
        var c = WordTestDocuments.Filled();
        c.Fields[tag] = printed;
        Assert.Contains(expected, WordTestDocuments.Read(WordTestDocuments.Build(c)).Problems);
    }

    [Fact]
    public void LineAmountThatIsNotQuantityTimesPriceIsAProblem()
    {
        var c = WordTestDocuments.Filled();
        c.Lines[1]["BT-131"] = "550,00";
        Assert.Contains(WordTestDocuments.Read(WordTestDocuments.Build(c)).Problems, p => p.StartsWith("Line 2: printed line amount 550,00"));
    }

    [Fact]
    public void UnreadableNumberAndDateAreProblemsNamingTheField()
    {
        var c = WordTestDocuments.Filled();
        c.Lines[0]["BT-129"] = "abc";
        var bytes = WordTestDocuments.Build(c);
        bytes = ReplaceText(bytes, "BT-2", "31.13.2026");   // typed over the date picker
        var problems = WordTestDocuments.Read(bytes).Problems;
        Assert.Contains(problems, p => p.Contains("BT-129") && p.Contains("'abc' is not a number"));
        Assert.Contains(problems, p => p.Contains("BT-2") && p.Contains("'31.13.2026' is not a date"));
    }

    [Fact]
    public void DocumentWithoutContentControlsHasNoFields()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Rechnung 4711")))));
        }
        var inv = WordTestDocuments.Read(ms.ToArray());
        Assert.False(inv.HasFields);
        Assert.Equal("No Hive BDC fields found: the document has no content controls tagged with BT numbers.", Assert.Single(inv.Problems));
    }

    [Fact]
    public void ClearedFormatDefaultsToXRechnungWithANote()
    {
        var bytes = WordTestDocuments.Build(WordTestDocuments.Filled());
        bytes = Mutate(bytes, main =>
        {
            var sdt = main.Document!.Descendants<SdtElement>().Single(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val == "eInvoiceType");
            sdt.SdtProperties!.Append(new ShowingPlaceholder());
        });
        var inv = WordTestDocuments.Read(bytes);
        Assert.Equal("xrechnung", inv.Format);
        Assert.Contains("No E-Rechnung format in the document; XRechnung is produced.", inv.Notes);
    }

    [Theory]
    [InlineData("721.613,72 EUR", 721613.72)]
    [InlineData("19,0 %", 19.0)]
    [InlineData("-200,00", -200)]
    [InlineData("200,00-", -200)]
    [InlineData("1.000", 1000)]
    [InlineData("2.5", 2.5)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("€ 12,5", 12.5)]
    public void GermanAmountsParse(string text, double expected)
    {
        Assert.True(WordInvoiceReader.TryParseAmount(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1,2,3")]
    [InlineData("")]
    public void NonAmountsDoNotParse(string text) => Assert.False(WordInvoiceReader.TryParseAmount(text, out _));

    // Schlussrechnung: gross 2.975,00 (see Filled), earlier partial invoices paid 2.000,00, so 975,00 is due.
    [Fact]
    public void PaidAmountAndAmountDueOfAFinalInvoiceReachTheXml()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["BT-3"] = "877";
        var inv = WordTestDocuments.Read(AddFields(WordTestDocuments.Build(c), ("BT-113", "2.000,00"), ("BT-115", "975,00")));
        Assert.Empty(inv.Problems);
        Assert.Equal(2000m, (decimal?)inv.Json["prepaidAmount"]);
        Assert.Equal(2975m, inv.Totals!.PrintedGross);
        var mapped = InvoiceMapper.Map(AxDocument.Parse(inv.Json.ToJsonString()));
        var totals = mapped.CalculateTotals();
        Assert.Equal(2975m, totals.TaxInclusiveAmount);
        Assert.Equal(975m, totals.AmountDue);
    }

    [Fact]
    public void AmountDueThatIgnoresThePaidAmountIsAProblem()
    {
        var c = WordTestDocuments.Filled();
        c.Fields["BT-3"] = "877";
        var inv = WordTestDocuments.Read(AddFields(WordTestDocuments.Build(c), ("BT-113", "2.000,00"), ("BT-115", "2.975,00")));
        Assert.Contains("Amount due (BT-115): printed 2.975,00, gross 2.975,00 minus paid amount (BT-113) 2.000,00 is 975,00.",
            inv.Problems);
    }

    [Fact]
    public void PaidAmountWithoutPrintedAmountDueIsNoProblem()
    {
        var inv = WordTestDocuments.Read(AddFields(WordTestDocuments.Build(WordTestDocuments.Filled()), ("BT-113", "500,00")));
        Assert.Empty(inv.Problems);
        Assert.Equal(2475m, InvoiceMapper.Map(AxDocument.Parse(inv.Json.ToJsonString())).CalculateTotals().AmountDue);
    }

    [Fact]
    public void AmountDueWithoutPaidAmountStillStandsInForTheGross()
    {
        var c = WordTestDocuments.Filled();
        c.Fields.Remove("BT-112");
        var inv = WordTestDocuments.Read(AddFields(WordTestDocuments.Build(c), ("BT-115", "2.900,00")));
        Assert.Contains("Gross amount (BT-112): printed 2.900,00, the lines add up to 2.975,00.", inv.Problems);
    }

    private static byte[] AddFields(byte[] docx, params (string Tag, string Value)[] fields) => Mutate(docx, main =>
    {
        foreach (var (tag, value) in fields)
            main.Document!.Body!.AppendChild(new Paragraph(new SdtRun(
                new SdtProperties(new SdtAlias { Val = tag }, new Tag { Val = tag }, new SdtContentText()),
                new SdtContentRun(new Run(new Text(value))))));
    });

    private static byte[] ReplaceText(byte[] docx, string tag, string text) => Mutate(docx, main =>
    {
        foreach (var root in new OpenXmlElement[] { main.Document!.Body! }.Concat(main.HeaderParts.Select(h => (OpenXmlElement)h.Header!)))
            foreach (var sdt in root.Descendants<SdtElement>().Where(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val == tag))
            {
                sdt.SdtProperties!.GetFirstChild<SdtContentDate>()?.Remove();
                sdt.Descendants<Text>().First().Text = text;
            }
    });

    private static byte[] Mutate(byte[] docx, Action<MainDocumentPart> change)
    {
        using var ms = new MemoryStream();
        ms.Write(docx);
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            change(doc.MainDocumentPart!);
            doc.MainDocumentPart!.Document!.Save();
            foreach (var h in doc.MainDocumentPart.HeaderParts) h.Header!.Save();
        }
        return ms.ToArray();
    }
}