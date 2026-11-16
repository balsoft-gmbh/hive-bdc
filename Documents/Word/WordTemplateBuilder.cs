using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace Balsoft.Hive.BDC.Documents.Word;

/// <summary>Values for a generated invoice; a tag without a value shows its placeholder.</summary>
public sealed class WordInvoiceContent
{
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Dictionary<string, string>> Lines { get; } = new();
}

/// <summary>
/// Builds the generic Hive BDC invoice template: a German invoice whose every value is a content control tagged with
/// its EN 16931 BT number, the lines a repeating section, the e-invoice format a drop-down. The dashboard saves it for
/// template authors; tests fill it to get documents for the reader and the renderer. The seller is fictitious.
/// </summary>
public static class WordTemplateBuilder
{
    public static readonly (string Display, string Code)[] TypeCodes =
    {
        ("RECHNUNG", "380"), ("ABSCHLAGSRECHNUNG", "875"), ("SCHLUSSRECHNUNG", "877"),
        ("TEILRECHNUNG", "326"), ("GUTSCHRIFT", "381"),
    };

    public static readonly (string Display, string Code)[] Units =
    {
        ("pauschal", "LS"), ("Stück", "H87"), ("m²", "MTK"), ("m", "MTR"), ("Std.", "HUR"), ("kg", "KGM"),
    };

    public static readonly (string Display, string Code)[] Formats = { ("XRechnung", "xrechnung"), ("ZUGFeRD", "zugferd") };

    /// <summary>Seller data of the template (fictitious), used where the caller gives none.</summary>
    public static readonly IReadOnlyDictionary<string, string> SampleSeller = new Dictionary<string, string>
    {
        ["BT-27"] = "Musterbau GmbH", ["BT-35"] = "Industriestraße 1", ["BT-38"] = "12345", ["BT-37"] = "Musterstadt",
        ["BT-40"] = "DE", ["BT-31"] = "DE123456789", ["BT-32"] = "12/345/67890", ["BT-84"] = "DE02120300000000202051",
        ["BT-86"] = "BYLADEM1001", ["BT-41"] = "Erika Muster", ["BT-42"] = "+49 123 4567-0", ["BT-43"] = "rechnung@musterbau.example",
    };

    private static int _id = 700000;

    public static void Write(Stream output, WordInvoiceContent? content = null)
    {
        content ??= new WordInvoiceContent();
        string V(string tag) => content.Fields.TryGetValue(tag, out var v) ? v : "";
        string Seller(string tag) => content.Fields.TryGetValue(tag, out var v) ? v : SampleSeller.GetValueOrDefault(tag, "");

        using var doc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document, true);
        var main = doc.AddMainDocumentPart();
        AddStyles(main);

        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(
            Table(new[] { 5600, 3900 },
                Row(Cell(5600,
                        Para(Field("BT-27", "BT-27 Verkäufer", Seller("BT-27"), locked: true, bold: true, size: 32)),
                        Para(Small("Lieferant elektronischer Rechnungen"))),
                    Cell(3900,
                        Para(JustificationValues.Right, Drop("BT-3", "BT-3 Rechnungsart", TypeCodes, V("BT-3"), bold: true, size: 24)),
                        Para(JustificationValues.Right, Label("Nummer: "), Field("BT-1", "BT-1 Rechnungsnummer", V("BT-1"), bold: true)),
                        Para(JustificationValues.Right, Label("Seite "), PageField("PAGE"), Label(" von "), PageField("NUMPAGES"))))),
            Para());
        string headerId = main.GetIdOfPart(header);

        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(
            Table(new[] { 3200, 3200, 3100 },
                Row(Cell(3200,
                        Para(Field("BT-27", "BT-27 Verkäufer", Seller("BT-27"), locked: true, size: 14)),
                        Para(Field("BT-35", "BT-35 Straße", Seller("BT-35"), locked: true, size: 14)),
                        Para(Field("BT-38", "BT-38 PLZ", Seller("BT-38"), locked: true, size: 14), Small(" "),
                            Field("BT-37", "BT-37 Ort", Seller("BT-37"), locked: true, size: 14), Small(", "),
                            Field("BT-40", "BT-40 Land", Seller("BT-40"), locked: true, size: 14))),
                    Cell(3200,
                        Para(Small("USt-IdNr.: "), Field("BT-31", "BT-31 USt-IdNr.", Seller("BT-31"), locked: true, size: 14)),
                        Para(Small("St.-Nr.: "), Field("BT-32", "BT-32 Steuernummer", Seller("BT-32"), locked: true, size: 14))),
                    Cell(3100,
                        Para(Small("IBAN: "), Field("BT-84", "BT-84 IBAN", Seller("BT-84"), locked: true, size: 14)),
                        Para(Small("BIC: "), Field("BT-86", "BT-86 BIC", Seller("BT-86"), locked: true, size: 14))))));
        string footerId = main.GetIdOfPart(footer);

        var body = new Body();
        body.Append(
            Table(new[] { 5200, 4300 },
                Row(Cell(5200,
                        Para(Small(Seller("BT-27") + " · " + Seller("BT-35") + " · " + Seller("BT-38") + " " + Seller("BT-37"))),
                        Para(),
                        Para(Field("BT-44", "BT-44 Käufer Name", V("BT-44"))),
                        Para(Field("BT-50", "BT-50 Käufer Straße", V("BT-50"))),
                        Para(Field("BT-53", "BT-53 Käufer PLZ", V("BT-53")), Plain(" "), Field("BT-52", "BT-52 Käufer Ort", V("BT-52"))),
                        Para(Field("BT-55", "BT-55 Käufer Land", V("BT-55").Length > 0 ? V("BT-55") : "DE"))),
                    Cell(4300,
                        Info("Datum:", Date("BT-2", "BT-2 Rechnungsdatum", V("BT-2"))),
                        Info("Projekt:", Field("BT-11", "BT-11 Projektnummer", V("BT-11"))),
                        Info("Ansprechpartner:", Field("BT-41", "BT-41 Ansprechpartner", Seller("BT-41"))),
                        Info("Telefon:", Field("BT-42", "BT-42 Telefon", Seller("BT-42"))),
                        Info("E-Mail:", Field("BT-43", "BT-43 E-Mail", Seller("BT-43")))))),
            Para(),
            Table(new[] { 2600, 2150, 2600, 2150 },
                Row(InfoCell("Leitweg-ID / Ihre Referenz:", 2600), ValueCell(Field("BT-10", "BT-10 Käuferreferenz", V("BT-10")), 2150),
                    InfoCell("Bestellnummer:", 2600), ValueCell(Field("BT-13", "BT-13 Bestellnummer", V("BT-13")), 2150)),
                Row(InfoCell("Leistungszeitraum von:", 2600), ValueCell(Date("BT-73", "BT-73 Leistungszeitraum von", V("BT-73")), 2150),
                    InfoCell("bis:", 2600), ValueCell(Date("BT-74", "BT-74 Leistungszeitraum bis", V("BT-74")), 2150)),
                Row(InfoCell("E-Rechnungsadresse:", 2600), ValueCell(Field("BT-49", "BT-49 E-Rechnungsadresse Käufer", V("BT-49")), 2150),
                    InfoCell("E-Rechnung:", 2600), ValueCell(Drop(WordInvoiceReader.FormatTag, "E-Rechnung Format", Formats,
                        V(WordInvoiceReader.FormatTag).Length > 0 ? V(WordInvoiceReader.FormatTag) : "xrechnung"), 2150))),
            Para(),
            BlockField("BT-22", "BT-22 Bemerkung", V("BT-22")),
            Para(),
            LinesTable(content),
            Para(),
            Table(new[] { 5400, 2300, 1800 },
                TotalRow("Nettobetrag", Field("BT-109", "BT-109 Nettobetrag", V("BT-109")), Plain(" EUR"), bold: true),
                TotalRow(null, Field("BT-110", "BT-110 Umsatzsteuer", V("BT-110")), Plain(" EUR"), vatLabel: V("BT-119")),
                TotalRow("Gesamtbetrag", Field("BT-112", "BT-112 Gesamtbetrag", V("BT-112")), Plain(" "),
                    bold: true, currency: Field("BT-5", "BT-5 Währung", V("BT-5").Length > 0 ? V("BT-5") : "EUR"), topBorder: true)),
            Para(),
            Para(Label("Zahlbar bis "), Date("BT-9", "BT-9 Fälligkeitsdatum", V("BT-9")), Plain(" ohne Abzug.")),
            new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId },
                new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 2400, Right = 1134, Bottom = 1700, Left = 1418, Header = 567, Footer = 567, Gutter = 0U }));
        main.Document = new Document(body);
        main.Document.Save();
    }

    // ── document parts ──────────────────────────────────────────────────────

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                    new FontSize { Val = "20" }, new FontSizeComplexScript { Val = "20" },
                    new Languages { Val = "de-DE" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "Placeholder Text" }, new UIPriority { Val = 99 }, new SemiHidden(),
                new StyleRunProperties(new Color { Val = "808080" })) { Type = StyleValues.Character, StyleId = "PlaceholderText" });
    }

    private static Table LinesTable(WordInvoiceContent content)
    {
        int[] w = { 700, 3900, 900, 1000, 1200, 700, 1100 };
        var table = Table(w,
            Row(true, Head("Pos.", w[0]), Head("Bezeichnung", w[1]), Head("Menge", w[2], right: true), Head("Einheit", w[3]),
                Head("Einzelpreis", w[4], right: true), Head("USt %", w[5], right: true), Head("Gesamt", w[6], right: true)));

        var section = new SdtRow(
            new SdtProperties(new SdtAlias { Val = "Positionen" }, new Tag { Val = WordInvoiceReader.LinesTag }, new SdtId { Val = NextId() },
                new W15.SdtRepeatedSection(new W15.SectionTitle { Val = "Position" })),
            new SdtContentRow());
        var items = content.Lines.Count > 0 ? content.Lines : new List<Dictionary<string, string>> { new() };
        int n = 0;
        foreach (var line in items)
        {
            n++;
            string L(string tag) => line.TryGetValue(tag, out var v) ? v : "";
            var row = Row(
                ValueCell(Field("BT-126", "BT-126 Position", L("BT-126").Length > 0 || content.Lines.Count == 0 ? L("BT-126") : n.ToString())),
                new TableCell(Width(w[1]),
                    Para(Field("BT-153", "BT-153 Bezeichnung", L("BT-153"), bold: true)),
                    BlockField("BT-154", "BT-154 Beschreibung", L("BT-154"))),
                ValueCell(Field("BT-129", "BT-129 Menge", L("BT-129")), w[2], right: true),
                ValueCell(Drop("BT-130", "BT-130 Einheit", Units, L("BT-130")), w[3]),
                ValueCell(Field("BT-146", "BT-146 Einzelpreis", L("BT-146")), w[4], right: true),
                ValueCell(Field("BT-152", "BT-152 USt-Satz", L("BT-152")), w[5], right: true),
                ValueCell(Field("BT-131", "BT-131 Positionsbetrag", L("BT-131")), w[6], right: true));
            section.GetFirstChild<SdtContentRow>()!.Append(new SdtRow(
                new SdtProperties(new SdtId { Val = NextId() }, new W15.SdtRepeatedSectionItem()),
                new SdtContentRow(row)));
        }
        table.Append(section);
        return table;
    }

    private static TableRow TotalRow(string? label, SdtRun amount, Run suffix, bool bold = false, string? vatLabel = null,
        SdtRun? currency = null, bool topBorder = false)
    {
        var labelPara = label is not null
            ? Para(bold ? Bold(label) : Plain(label))
            : Para(Plain("USt. "), Field("BT-119", "BT-119 USt-Satz", vatLabel ?? "19"), Plain(" %"));
        var valueCell = new TableCell(Width(1800), Para(JustificationValues.Right, amount, suffix));
        if (currency is not null) valueCell.GetFirstChild<Paragraph>()!.Append(currency);
        if (topBorder)
            valueCell.TableCellProperties!.Append(new TableCellBorders(
                new TopBorder { Val = BorderValues.Single, Size = 6, Color = "000000" },
                new BottomBorder { Val = BorderValues.Double, Size = 4, Color = "000000" }));
        return Row(new TableCell(Width(5400), Para()), new TableCell(Width(2300), labelPara), valueCell);
    }

    // ── content controls ────────────────────────────────────────────────────

    private static SdtRun Field(string tag, string title, string value, bool locked = false, bool bold = false, int size = 0)
    {
        var pr = new SdtProperties(new SdtAlias { Val = title }, new Tag { Val = tag }, new SdtId { Val = NextId() });
        if (locked) pr.Append(new DocumentFormat.OpenXml.Wordprocessing.Lock { Val = LockingValues.SdtContentLocked });
        bool empty = value.Length == 0;
        if (empty) pr.Append(new ShowingPlaceholder());
        pr.Append(new SdtContentText());
        return new SdtRun(pr, new SdtContentRun(TextRun(empty ? title : value, bold, size, placeholder: empty)));
    }

    private static SdtRun Date(string tag, string title, string isoOrEmpty)
    {
        var pr = new SdtProperties(new SdtAlias { Val = title }, new Tag { Val = tag }, new SdtId { Val = NextId() });
        bool empty = !DateTime.TryParseExact(isoOrEmpty, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date);
        if (empty) pr.Append(new ShowingPlaceholder());
        var dateElement = new SdtContentDate(
            new DateFormat { Val = "dd.MM.yyyy" }, new LanguageId { Val = "de-DE" },
            new SdtDateMappingType { Val = DateFormatValues.DateTime }, new Calendar { Val = CalendarValues.Gregorian });
        if (!empty) dateElement.FullDate = new DateTimeValue(DateTime.SpecifyKind(date, DateTimeKind.Utc));
        pr.Append(dateElement);
        return new SdtRun(pr, new SdtContentRun(TextRun(empty ? "TT.MM.JJJJ" : date.ToString("dd.MM.yyyy",
            System.Globalization.CultureInfo.InvariantCulture), placeholder: empty)));
    }

    private static SdtRun Drop(string tag, string title, (string Display, string Code)[] items, string codeOrEmpty, bool bold = false, int size = 0)
    {
        var pr = new SdtProperties(new SdtAlias { Val = title }, new Tag { Val = tag }, new SdtId { Val = NextId() });
        var list = new SdtContentDropDownList();
        foreach (var (display, code) in items) list.Append(new ListItem { DisplayText = display, Value = code });
        var chosen = items.FirstOrDefault(i => string.Equals(i.Code, codeOrEmpty, StringComparison.OrdinalIgnoreCase));
        bool empty = chosen.Display is null;
        if (empty) pr.Append(new ShowingPlaceholder());
        pr.Append(list);
        return new SdtRun(pr, new SdtContentRun(TextRun(empty ? title : chosen.Display ?? title, bold, size, placeholder: empty)));
    }

    private static SdtBlock BlockField(string tag, string title, string value)
    {
        var pr = new SdtProperties(new SdtAlias { Val = title }, new Tag { Val = tag }, new SdtId { Val = NextId() });
        bool empty = value.Length == 0;
        if (empty) pr.Append(new ShowingPlaceholder());
        var content = new SdtContentBlock();
        foreach (var line in (empty ? title : value).Split('\n'))
            content.Append(new Paragraph(TextRun(line, placeholder: empty)));
        return new SdtBlock(pr, content);
    }

    private static int NextId() => Interlocked.Increment(ref _id);

    // ── plain building blocks ───────────────────────────────────────────────

    private static Run TextRun(string text, bool bold = false, int size = 0, bool placeholder = false)
    {
        var rpr = new RunProperties();
        if (placeholder) rpr.Append(new RunStyle { Val = "PlaceholderText" });
        if (bold) rpr.Append(new Bold());
        if (size > 0) rpr.Append(new FontSize { Val = size.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Run Plain(string text) => TextRun(text);
    private static Run Bold(string text) => TextRun(text, bold: true);
    private static Run Label(string text) => TextRun(text);
    private static Run Small(string text) => TextRun(text, size: 14);

    private static SimpleField PageField(string name)
        => new(new Run(new Text("1"))) { Instruction = $" {name} " };

    private static Paragraph Para(params OpenXmlElement[] content) => new(content);

    private static Paragraph Para(JustificationValues justify, params OpenXmlElement[] content)
    {
        var p = new Paragraph(new ParagraphProperties(new Justification { Val = justify }));
        p.Append(content);
        return p;
    }

    private static Paragraph Info(string label, OpenXmlElement value)
        => new(new ParagraphProperties(new Tabs(new TabStop { Val = TabStopValues.Left, Position = 1700 })),
            TextRun(label), new Run(new TabChar()), value);

    private static Table Table(int[] widths, params OpenXmlElement[] rows)
    {
        var grid = new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var t = new Table(new TableProperties(
                new TableWidth { Width = widths.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableCellMarginDefault(new TableCellLeftMargin { Width = 57, Type = TableWidthValues.Dxa },
                    new TableCellRightMargin { Width = 57, Type = TableWidthValues.Dxa })),
            grid);
        t.Append(rows);
        return t;
    }

    private static TableRow Row(params TableCell[] cells) => Row(false, cells);

    private static TableRow Row(bool header, params TableCell[] cells)
    {
        var row = new TableRow();
        if (header) row.Append(new TableRowProperties(new TableHeader()));
        row.Append(cells);
        return row;
    }

    private static TableCellProperties Width(int twips)
        => new(new TableCellWidth { Width = twips.ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa });

    private static TableCell Cell(int width, params Paragraph[] paragraphs)
    {
        var c = new TableCell(Width(width));
        c.Append(paragraphs);
        return c;
    }

    private static TableCell Head(string text, int width, bool right = false)
        => new(new TableCellProperties(new TableCellWidth { Width = width.ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
                new TableCellBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "000000" }),
                new Shading { Val = ShadingPatternValues.Clear, Fill = "EDEDED" }),
            right ? Para(JustificationValues.Right, Bold(text)) : Para(Bold(text)));

    private static TableCell InfoCell(string label, int width) => Cell(width, Para(TextRun(label)));

    private static TableCell ValueCell(OpenXmlElement value, int width = 700, bool right = false)
        => new(Width(width), right ? Para(JustificationValues.Right, value) : Para(value));
}