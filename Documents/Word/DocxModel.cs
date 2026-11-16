using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Balsoft.Hive.BDC.Documents.Word;

// ── model: the subset of WordprocessingML the renderer lays out (ECMA-376 Part 1, 17.3 to 17.6) ──

internal sealed record TextStyle(string Font, double Size, bool Bold, bool Italic, bool Underline, bool Strike,
    string? Color, string? Highlight, bool Caps, int VertAlign);

internal abstract record Inline;
internal sealed record TextInline(string Text, TextStyle Style) : Inline;
internal sealed record TabInline(TextStyle Style) : Inline;
internal sealed record BreakInline(bool Page) : Inline;
internal sealed record ImageInline(byte[] Data, double Width, double Height) : Inline;
internal sealed record FieldInline(string Kind, TextStyle Style) : Inline;

internal sealed record TabStopDef(double Position, char Kind);
internal sealed record BorderDef(string Kind, double Width, string Color);
internal sealed record Margins(double Top, double Left, double Bottom, double Right);

internal sealed class BorderSet
{
    public BorderDef? Top, Left, Bottom, Right, InsideH, InsideV;
}

internal sealed class ParaProps
{
    public char Align = 'l';
    public double IndentLeft, IndentRight, FirstLine, SpaceBefore, SpaceAfter;
    public double LineValue = 1.0;   // 'a': multiple of single, else points
    public char LineRule = 'a';
    public List<TabStopDef> Tabs = new();
    public bool KeepNext, KeepLines, PageBreakBefore;
    public string? Fill;
    public BorderDef? BorderTop, BorderBottom;
    public TextStyle Mark = DocxModel.DefaultRun;
}

internal abstract class Block;

internal sealed class ParaBlock : Block
{
    public required ParaProps Props;
    public List<Inline> Inlines = new();
}

internal sealed class TableBlock : Block
{
    public List<double> Grid = new();
    public double Indent;
    public char Align = 'l';
    public Margins CellMargins = new(0, 5.4, 0, 5.4);
    public BorderSet Borders = new();
    public List<RowModel> Rows = new();
}

internal sealed class RowModel
{
    public double Height;
    public char HeightRule = 'a';
    public bool CantSplit;
    public int GridBefore;
    public Margins? CellMargins;
    public BorderSet? Borders;
    public List<CellModel> Cells = new();
}

internal sealed class CellModel
{
    public int Span = 1;
    public char VMerge = 'n';   // n none, r restart, c continue
    public Margins? Margins;
    public BorderSet Borders = new();
    public string? Fill;
    public char VAlign = 't';
    public List<Block> Blocks = new();
}

internal sealed class DocModel
{
    public double PageWidth = 595.3, PageHeight = 841.9;
    public double MarginTop = 72, MarginBottom = 72, MarginLeft = 72, MarginRight = 72;
    public double HeaderDistance = 36, FooterDistance = 36;
    public bool TitlePage;
    public double DefaultTab = 36;
    public int CompatibilityMode = 12;   // a document without the setting opens in Word 2007 mode
    public List<Block> Body = new();
    public List<Block>? Header, FirstHeader, Footer, FirstFooter;
    public SortedSet<string> Unsupported = new(StringComparer.Ordinal);
}

/// <summary>Reads a .docx into <see cref="DocModel"/>: styles resolved, fields and content controls flattened.</summary>
internal sealed class DocxModel
{
    public static readonly TextStyle DefaultRun = new("Times New Roman", 10, false, false, false, false, null, null, false, 0);
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly Dictionary<string, OpenXmlElement> _styles = new(StringComparer.Ordinal);
    private string? _defaultParaStyle;
    private OpenXmlElement? _defaultPPr, _defaultRPr;
    private string _minorFont = "Calibri", _majorFont = "Calibri Light";
    private readonly DocModel _doc = new();
    private OpenXmlPart _part = null!;

    public static DocModel Read(WordprocessingDocument wd)
    {
        var main = wd.MainDocumentPart ?? throw new InvalidDataException("The file has no main document part.");
        var m = new DocxModel();
        m.LoadStyles(main);
        m._part = main;
        var body = main.Document?.Body ?? throw new InvalidDataException("The document has no body.");
        m._doc.Body = m.Blocks(body);

        var sect = body.ChildElements.LastOrDefault(e => e.LocalName == "sectPr");
        if (sect is not null) m.ReadSection(sect, main);
        if (main.DocumentSettingsPart?.Settings is { } settings)
        {
            if (Child(settings, "defaultTabStop") is { } tab)
                m._doc.DefaultTab = Twips(Attr(tab, "val")) is > 0 and var t ? t : 36;
            var mode = Child(settings, "compat")?.ChildElements.FirstOrDefault(c => c.LocalName == "compatSetting" && Attr(c, "name") == "compatibilityMode");
            if (int.TryParse(Attr(mode, "val"), out int compat)) m._doc.CompatibilityMode = compat;
        }
        return m._doc;
    }

    // ── section, header and footer ──────────────────────────────────────────

    private void ReadSection(OpenXmlElement sect, MainDocumentPart main)
    {
        if (Child(sect, "pgSz") is { } size)
        {
            _doc.PageWidth = Twips(Attr(size, "w")) ?? _doc.PageWidth;
            _doc.PageHeight = Twips(Attr(size, "h")) ?? _doc.PageHeight;
        }
        if (Child(sect, "pgMar") is { } mar)
        {
            _doc.MarginTop = Math.Abs(Twips(Attr(mar, "top")) ?? _doc.MarginTop);
            _doc.MarginBottom = Math.Abs(Twips(Attr(mar, "bottom")) ?? _doc.MarginBottom);
            _doc.MarginLeft = Twips(Attr(mar, "left")) ?? Twips(Attr(mar, "start")) ?? _doc.MarginLeft;
            _doc.MarginRight = Twips(Attr(mar, "right")) ?? Twips(Attr(mar, "end")) ?? _doc.MarginRight;
            _doc.HeaderDistance = Twips(Attr(mar, "header")) ?? _doc.HeaderDistance;
            _doc.FooterDistance = Twips(Attr(mar, "footer")) ?? _doc.FooterDistance;
        }
        if (Child(sect, "cols") is { } cols && int.TryParse(Attr(cols, "num"), out int n) && n > 1)
            _doc.Unsupported.Add("text columns");
        _doc.TitlePage = Child(sect, "titlePg") is { } tp && Toggle(tp);

        foreach (var reference in sect.ChildElements.Where(e => e.LocalName is "headerReference" or "footerReference"))
        {
            string? id = Attr(reference, "id", R);
            if (id is null || main.GetPartById(id) is not { } part) continue;
            string type = Attr(reference, "type") ?? "default";
            var root = part switch { HeaderPart h => (OpenXmlElement?)h.Header, FooterPart f => f.Footer, _ => null };
            if (root is null) continue;
            var saved = _part;
            _part = part;
            var blocks = Blocks(root);
            _part = saved;
            bool header = reference.LocalName == "headerReference";
            switch (type)
            {
                case "first" when header: _doc.FirstHeader = blocks; break;
                case "first": _doc.FirstFooter = blocks; break;
                case "even": break;
                default:
                    if (header) _doc.Header = blocks; else _doc.Footer = blocks;
                    break;
            }
        }
    }

    // ── blocks ──────────────────────────────────────────────────────────────

    private List<Block> Blocks(OpenXmlElement container)
    {
        var list = new List<Block>();
        foreach (var e in container.ChildElements)
        {
            switch (e.LocalName)
            {
                case "p":
                    list.Add(Paragraph(e));
                    if (Child(Child(e, "pPr"), "sectPr") is { } inner && Attr(Child(inner, "type"), "val") != "continuous")
                        list.Add(new ParaBlock { Props = new ParaProps(), Inlines = { new BreakInline(true) } });
                    break;
                case "tbl": list.Add(Table(e)); break;
                case "sdt": if (Child(e, "sdtContent") is { } c) list.AddRange(Blocks(c)); break;
                case "customXml": list.AddRange(Blocks(e)); break;
                case "altChunk": _doc.Unsupported.Add("embedded document (altChunk)"); break;
            }
        }
        return list;
    }

    private ParaBlock Paragraph(OpenXmlElement p)
    {
        var pPr = Child(p, "pPr");
        string? styleId = Attr(Child(pPr, "pStyle"), "val") ?? _defaultParaStyle;
        var chain = StyleChain(styleId);
        var props = ResolvePara(chain.Select(s => Child(s, "pPr")).Prepend(_defaultPPr).Append(pPr));
        var rprChain = chain.Select(s => Child(s, "rPr")).Prepend(_defaultRPr).ToList();
        props.Mark = ResolveRun(rprChain.Append(Child(pPr, "rPr")));
        if (Child(pPr, "numPr") is { } num && Attr(Child(num, "numId"), "val") is { } numId && numId != "0")
            _doc.Unsupported.Add("numbered or bulleted lists");

        var block = new ParaBlock { Props = props };
        var field = new FieldState();
        Inlines(p, block.Inlines, rprChain, field);
        return block;
    }

    private sealed class FieldState
    {
        public int Depth;
        public bool InCode;
        public bool Suppress;
        public string Instruction = "";
    }

    private void Inlines(OpenXmlElement container, List<Inline> output, List<OpenXmlElement?> rprChain, FieldState field)
    {
        foreach (var e in container.ChildElements)
        {
            switch (e.LocalName)
            {
                case "r": Run(e, output, rprChain, field); break;
                case "hyperlink" or "smartTag" or "customXml" or "ins" or "moveTo" or "dir" or "bdo":
                    Inlines(e, output, rprChain, field); break;
                case "sdt": if (Child(e, "sdtContent") is { } c) Inlines(c, output, rprChain, field); break;
                case "fldSimple":
                    string instr = (Attr(e, "instr") ?? "").Trim();
                    string kind = FieldKind(instr);
                    if (kind.Length > 0) output.Add(new FieldInline(kind, ResolveRun(rprChain.Append(Child(e.ChildElements.FirstOrDefault(x => x.LocalName == "r"), "rPr")))));
                    else Inlines(e, output, rprChain, field);
                    break;
                case "oMath" or "oMathPara": _doc.Unsupported.Add("equations"); break;
            }
        }
    }

    private static string FieldKind(string instruction)
    {
        string first = instruction.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "";
        return first is "PAGE" or "NUMPAGES" or "SECTIONPAGES" ? (first == "PAGE" ? "PAGE" : "NUMPAGES") : "";
    }

    private void Run(OpenXmlElement r, List<Inline> output, List<OpenXmlElement?> rprChain, FieldState field)
    {
        var rPr = Child(r, "rPr");
        string? charStyle = Attr(Child(rPr, "rStyle"), "val");
        var chain = new List<OpenXmlElement?>(rprChain);
        chain.AddRange(StyleChain(charStyle).Select(s => Child(s, "rPr")));
        chain.Add(rPr);
        var style = ResolveRun(chain);
        bool hidden = IsOn(chain, "vanish") || IsOn(chain, "specVanish");

        foreach (var e in r.ChildElements)
        {
            switch (e.LocalName)
            {
                case "fldChar":
                    string t = Attr(e, "fldCharType") ?? "";
                    if (t == "begin") { field.Depth++; field.InCode = true; field.Instruction = ""; }
                    else if (t == "separate")
                    {
                        field.InCode = false;
                        string kind = FieldKind(field.Instruction);
                        if (kind.Length > 0) { output.Add(new FieldInline(kind, style)); field.Suppress = true; }
                    }
                    else if (t == "end") { field.Depth = Math.Max(0, field.Depth - 1); field.InCode = false; field.Suppress = false; }
                    continue;
                case "instrText":
                    if (field.InCode) field.Instruction += e.InnerText;
                    continue;
            }
            if (field.InCode || field.Suppress || hidden) continue;
            switch (e.LocalName)
            {
                case "t": output.Add(new TextInline(e.InnerText, style)); break;
                case "tab" or "ptab": output.Add(new TabInline(style)); break;
                case "br": output.Add(new BreakInline(Attr(e, "type") == "page")); break;
                case "cr": output.Add(new BreakInline(false)); break;
                case "noBreakHyphen": output.Add(new TextInline("-", style)); break;
                case "sym": output.Add(new TextInline(Symbol(Attr(e, "char")), style)); break;
                case "drawing": Drawing(e, output); break;
                case "pict": Vml(e, output); break;
                case "object": _doc.Unsupported.Add("embedded objects"); break;
                case "AlternateContent": _doc.Unsupported.Add("shapes or text boxes"); break;
            }
        }
    }

    private static string Symbol(string? hex)
    {
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)) return "";
        if (code >= 0xF000) code -= 0xF000;
        return code switch { 0xB7 => "•", 0xA7 => "▪", 0xD8 => "➢", 0xFC => "✓", _ => ((char)code).ToString() };
    }

    private void Drawing(OpenXmlElement drawing, List<Inline> output)
    {
        var inline = Child(drawing, "inline");
        if (inline is null)
        {
            _doc.Unsupported.Add("floating images or shapes");
            return;
        }
        var extent = Child(inline, "extent");
        double w = (Long(Attr(extent, "cx", "")) ?? 0) / 12700.0, h = (Long(Attr(extent, "cy", "")) ?? 0) / 12700.0;
        var blip = inline.Descendants().FirstOrDefault(d => d.LocalName == "blip");
        string? id = blip is null ? null : Attr(blip, "embed", R);
        if (id is null || inline.Descendants().Any(d => d.LocalName is "txbx" or "wsp" or "chart"))
        {
            _doc.Unsupported.Add("shapes, charts or text boxes");
            return;
        }
        AddImage(id, w, h, output);
    }

    private void Vml(OpenXmlElement pict, List<Inline> output)
    {
        var shape = pict.Descendants().FirstOrDefault(d => d.LocalName is "shape" or "rect" or "oval" or "roundrect");
        var imageData = pict.Descendants().FirstOrDefault(d => d.LocalName == "imagedata");
        string style = shape is null ? "" : Attr(shape, "style", "") ?? "";
        if (imageData is null || pict.Descendants().Any(d => d.LocalName == "textbox"))
        {
            _doc.Unsupported.Add("shapes or text boxes");
            return;
        }
        if (style.Contains("position:absolute", StringComparison.OrdinalIgnoreCase))
        {
            _doc.Unsupported.Add("floating images or shapes");
            return;
        }
        string? id = Attr(imageData, "id", R);
        if (id is null) return;
        AddImage(id, CssLength(style, "width"), CssLength(style, "height"), output);
    }

    private void AddImage(string relationshipId, double width, double height, List<Inline> output)
    {
        if (_part.GetPartById(relationshipId) is not ImagePart image) return;
        string type = image.ContentType.ToLowerInvariant();
        if (!(type.Contains("png") || type.Contains("jpeg") || type.Contains("jpg") || type.Contains("gif") || type.Contains("bmp")))
        {
            _doc.Unsupported.Add($"images of type {type}");
            return;
        }
        using var s = image.GetStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        if (width > 0 && height > 0) output.Add(new ImageInline(ms.ToArray(), width, height));
    }

    private static double CssLength(string style, string name)
    {
        foreach (var part in style.Split(';'))
        {
            var kv = part.Split(':', 2);
            if (kv.Length != 2 || !kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string v = kv[1].Trim().ToLowerInvariant();
            double factor = v.EndsWith("pt") ? 1 : v.EndsWith("in") ? 72 : v.EndsWith("cm") ? 72 / 2.54 : v.EndsWith("mm") ? 72 / 25.4 : v.EndsWith("px") ? 0.75 : 1;
            string num = new string(v.TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray());
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d * factor : 0;
        }
        return 0;
    }

    // ── tables ──────────────────────────────────────────────────────────────

    private TableBlock Table(OpenXmlElement tbl)
    {
        var t = new TableBlock();
        var tblPr = Child(tbl, "tblPr");
        var styleTblPr = StyleChain(Attr(Child(tblPr, "tblStyle"), "val")).Select(s => Child(s, "tblPr")).ToList();
        foreach (var pr in styleTblPr.Append(tblPr))
        {
            if (pr is null) continue;
            if (Child(pr, "tblInd") is { } ind) t.Indent = Twips(Attr(ind, "w")) ?? t.Indent;
            if (Attr(Child(pr, "jc"), "val") is { } jc) t.Align = jc is "center" ? 'c' : jc is "right" or "end" ? 'r' : 'l';
            if (Child(pr, "tblBorders") is { } b) MergeBorders(t.Borders, b);
            if (Child(pr, "tblCellMar") is { } m) t.CellMargins = MarginsFrom(m, t.CellMargins);
        }
        foreach (var col in Child(tbl, "tblGrid")?.ChildElements.Where(c => c.LocalName == "gridCol") ?? Enumerable.Empty<OpenXmlElement>())
            t.Grid.Add(Twips(Attr(col, "w")) ?? 0);
        Rows(tbl, t);
        return t;
    }

    private void Rows(OpenXmlElement container, TableBlock t)
    {
        foreach (var e in container.ChildElements)
        {
            switch (e.LocalName)
            {
                case "tr": t.Rows.Add(Row(e, t)); break;
                case "sdt": if (Child(e, "sdtContent") is { } c) Rows(c, t); break;
                case "customXml": Rows(e, t); break;
            }
        }
    }

    private RowModel Row(OpenXmlElement tr, TableBlock t)
    {
        var row = new RowModel();
        var trPr = Child(tr, "trPr");
        if (Child(trPr, "trHeight") is { } h)
        {
            row.Height = Twips(Attr(h, "val")) ?? 0;
            row.HeightRule = Attr(h, "hRule") switch { "exact" => 'e', "auto" => 'a', _ => row.Height > 0 ? 'l' : 'a' };
        }
        row.CantSplit = Child(trPr, "cantSplit") is { } cs && Toggle(cs);
        row.GridBefore = int.TryParse(Attr(Child(trPr, "gridBefore"), "val"), out int gb) ? gb : 0;
        if (Child(tr, "tblPrEx") is { } ex)
        {
            if (Child(ex, "tblCellMar") is { } m) row.CellMargins = MarginsFrom(m, t.CellMargins);
            if (Child(ex, "tblBorders") is { } b) { row.Borders = new BorderSet(); MergeBorders(row.Borders, b); }
        }
        Cells(tr, row);
        return row;
    }

    private void Cells(OpenXmlElement container, RowModel row)
    {
        foreach (var e in container.ChildElements)
        {
            switch (e.LocalName)
            {
                case "tc": row.Cells.Add(Cell(e)); break;
                case "sdt": if (Child(e, "sdtContent") is { } c) Cells(c, row); break;
                case "customXml": Cells(e, row); break;
            }
        }
    }

    private CellModel Cell(OpenXmlElement tc)
    {
        var cell = new CellModel();
        var pr = Child(tc, "tcPr");
        if (int.TryParse(Attr(Child(pr, "gridSpan"), "val"), out int span) && span > 1) cell.Span = span;
        if (Child(pr, "vMerge") is { } vm) cell.VMerge = Attr(vm, "val") == "restart" ? 'r' : 'c';
        if (Child(pr, "tcBorders") is { } b) MergeBorders(cell.Borders, b);
        cell.Fill = Fill(Child(pr, "shd"));
        cell.VAlign = Attr(Child(pr, "vAlign"), "val") switch { "center" => 'c', "bottom" => 'b', _ => 't' };
        if (Child(pr, "tcMar") is { } m) cell.Margins = MarginsFrom(m, new Margins(double.NaN, double.NaN, double.NaN, double.NaN));
        cell.Blocks = Blocks(tc);
        return cell;
    }

    private static Margins MarginsFrom(OpenXmlElement m, Margins fallback)
    {
        double Get(string a, string b, double f) => Twips(Attr(Child(m, a), "w")) ?? Twips(Attr(Child(m, b), "w")) ?? f;
        return new Margins(Get("top", "top", fallback.Top), Get("left", "start", fallback.Left),
            Get("bottom", "bottom", fallback.Bottom), Get("right", "end", fallback.Right));
    }

    private static void MergeBorders(BorderSet set, OpenXmlElement borders)
    {
        foreach (var b in borders.ChildElements)
        {
            var def = Border(b);
            switch (b.LocalName)
            {
                case "top": set.Top = def; break;
                case "left" or "start": set.Left = def; break;
                case "bottom": set.Bottom = def; break;
                case "right" or "end": set.Right = def; break;
                case "insideH": set.InsideH = def; break;
                case "insideV": set.InsideV = def; break;
            }
        }
    }

    /// <summary>A border, or a "none" marker (Kind "none") that switches off an inherited one.</summary>
    private static BorderDef Border(OpenXmlElement b)
    {
        string kind = Attr(b, "val") ?? "none";
        if (kind is "nil" or "none") return new BorderDef("none", 0, "000000");
        double width = (double.TryParse(Attr(b, "sz"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sz) ? sz : 4) / 8.0;
        string color = Attr(b, "color") is { } c && c != "auto" ? c : "000000";
        string k = kind.Contains("double") ? "double" : kind.Contains("dot") ? "dotted" : kind.Contains("dash") ? "dashed" : "single";
        return new BorderDef(k, Math.Max(width, 0.25), color);
    }

    private static string? Fill(OpenXmlElement? shd)
    {
        string? fill = Attr(shd, "fill");
        return fill is null || fill == "auto" || fill.Length != 6 ? null : fill;
    }

    // ── styles ──────────────────────────────────────────────────────────────

    private void LoadStyles(MainDocumentPart main)
    {
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is not null)
        {
            var defaults = Child(styles, "docDefaults");
            _defaultPPr = Child(Child(defaults, "pPrDefault"), "pPr");
            _defaultRPr = Child(Child(defaults, "rPrDefault"), "rPr");
            foreach (var s in styles.ChildElements.Where(e => e.LocalName == "style"))
            {
                if (Attr(s, "styleId") is not { } id) continue;
                _styles[id] = s;
                if (Attr(s, "type") == "paragraph" && Attr(s, "default") is "1" or "true" or "on") _defaultParaStyle = id;
            }
        }
        var fonts = main.ThemePart?.Theme?.Descendants().FirstOrDefault(e => e.LocalName == "fontScheme");
        if (fonts is not null)
        {
            _minorFont = Attr(Child(Child(fonts, "minorFont"), "latin"), "typeface", "") ?? _minorFont;
            _majorFont = Attr(Child(Child(fonts, "majorFont"), "latin"), "typeface", "") ?? _majorFont;
        }
    }

    /// <summary>The style and its basedOn ancestors, base first.</summary>
    private List<OpenXmlElement> StyleChain(string? styleId)
    {
        var chain = new List<OpenXmlElement>();
        var seen = new HashSet<string>();
        while (styleId is not null && seen.Add(styleId) && _styles.TryGetValue(styleId, out var style))
        {
            chain.Insert(0, style);
            styleId = Attr(Child(style, "basedOn"), "val");
        }
        return chain;
    }

    private ParaProps ResolvePara(IEnumerable<OpenXmlElement?> layers)
    {
        var p = new ParaProps();
        foreach (var pr in layers)
        {
            if (pr is null) continue;
            if (Attr(Child(pr, "jc"), "val") is { } jc)
                p.Align = jc switch { "center" => 'c', "right" or "end" => 'r', "both" or "distribute" => 'j', _ => 'l' };
            if (Child(pr, "ind") is { } ind)
            {
                p.IndentLeft = Twips(Attr(ind, "left")) ?? Twips(Attr(ind, "start")) ?? p.IndentLeft;
                p.IndentRight = Twips(Attr(ind, "right")) ?? Twips(Attr(ind, "end")) ?? p.IndentRight;
                if (Twips(Attr(ind, "hanging")) is { } hanging) p.FirstLine = -hanging;
                else if (Twips(Attr(ind, "firstLine")) is { } first) p.FirstLine = first;
            }
            if (Child(pr, "spacing") is { } sp)
            {
                p.SpaceBefore = Twips(Attr(sp, "before")) ?? p.SpaceBefore;
                p.SpaceAfter = Twips(Attr(sp, "after")) ?? p.SpaceAfter;
                if (Attr(sp, "beforeAutospacing") is "1" or "true" or "on") p.SpaceBefore = 14;
                if (Attr(sp, "afterAutospacing") is "1" or "true" or "on") p.SpaceAfter = 14;
                if (double.TryParse(Attr(sp, "line"), NumberStyles.Float, CultureInfo.InvariantCulture, out double line))
                {
                    p.LineRule = Attr(sp, "lineRule") switch { "exact" => 'e', "atLeast" => 'l', _ => 'a' };
                    p.LineValue = p.LineRule == 'a' ? line / 240.0 : line / 20.0;
                }
            }
            if (Child(pr, "tabs") is { } tabs)
                foreach (var tab in tabs.ChildElements.Where(e => e.LocalName == "tab"))
                {
                    double pos = Twips(Attr(tab, "pos")) ?? 0;
                    string val = Attr(tab, "val") ?? "left";
                    p.Tabs.RemoveAll(x => Math.Abs(x.Position - pos) < 0.5);
                    if (val != "clear")
                        p.Tabs.Add(new TabStopDef(pos, val switch { "right" or "end" => 'r', "center" => 'c', "decimal" => 'd', _ => 'l' }));
                }
            if (Child(pr, "keepNext") is { } kn) p.KeepNext = Toggle(kn);
            if (Child(pr, "keepLines") is { } kl) p.KeepLines = Toggle(kl);
            if (Child(pr, "pageBreakBefore") is { } pb) p.PageBreakBefore = Toggle(pb);
            if (Child(pr, "shd") is { } shd) p.Fill = Fill(shd);
            if (Child(pr, "pBdr") is { } bdr)
            {
                if (Child(bdr, "top") is { } top) p.BorderTop = Border(top);
                if (Child(bdr, "bottom") is { } bottom) p.BorderBottom = Border(bottom);
            }
        }
        p.Tabs.Sort((a, b) => a.Position.CompareTo(b.Position));
        return p;
    }

    private TextStyle ResolveRun(IEnumerable<OpenXmlElement?> layers)
    {
        string font = DefaultRun.Font;
        double size = DefaultRun.Size;
        bool bold = false, italic = false, underline = false, strike = false, caps = false;
        string? color = null, highlight = null;
        int vert = 0;
        foreach (var pr in layers)
        {
            if (pr is null) continue;
            if (Child(pr, "rFonts") is { } f)
            {
                string? theme = Attr(f, "asciiTheme");
                font = Attr(f, "ascii") ?? (theme is null ? null : theme.StartsWith("major") ? _majorFont : _minorFont) ?? Attr(f, "hAnsi") ?? font;
            }
            if (double.TryParse(Attr(Child(pr, "sz"), "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sz)) size = sz / 2.0;
            if (Child(pr, "b") is { } b) bold = Toggle(b);
            if (Child(pr, "i") is { } i) italic = Toggle(i);
            if (Child(pr, "u") is { } u) underline = (Attr(u, "val") ?? "single") != "none";
            if (Child(pr, "strike") is { } s) strike = Toggle(s);
            if (Child(pr, "dstrike") is { } ds) strike |= Toggle(ds);
            if (Child(pr, "caps") is { } c) caps = Toggle(c);
            if (Child(pr, "color") is { } col) color = Attr(col, "val") is { } v && v != "auto" && v.Length == 6 ? v : null;
            if (Child(pr, "highlight") is { } hl) highlight = Attr(hl, "val") is { } hv && hv != "none" ? hv : null;
            if (Child(pr, "shd") is { } shd && Fill(shd) is { } runFill) highlight = "#" + runFill;
            if (Child(pr, "vertAlign") is { } va) vert = Attr(va, "val") switch { "superscript" => 1, "subscript" => 2, _ => 0 };
        }
        return new TextStyle(font, size, bold, italic, underline, strike, color, highlight, caps, vert);
    }

    private static bool IsOn(IEnumerable<OpenXmlElement?> layers, string name)
    {
        bool on = false;
        foreach (var pr in layers)
            if (Child(pr, name) is { } e) on = Toggle(e);
        return on;
    }

    // ── XML helpers ─────────────────────────────────────────────────────────

    internal static OpenXmlElement? Child(OpenXmlElement? e, string localName)
        => e?.ChildElements.FirstOrDefault(c => c.LocalName == localName);

    internal static string? Attr(OpenXmlElement? e, string localName, string? ns = W)
    {
        if (e is null) return null;
        foreach (var a in e.GetAttributes())
            if (a.LocalName == localName && (ns is null || a.NamespaceUri == ns || (ns == "" && string.IsNullOrEmpty(a.NamespaceUri))))
                return a.Value;
        return null;
    }

    private static bool Toggle(OpenXmlElement e, string attribute = "val")
        => Attr(e, attribute) is not ("0" or "false" or "off");

    private static double? Twips(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d / 20.0 : null;

    private static long? Long(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : null;
}