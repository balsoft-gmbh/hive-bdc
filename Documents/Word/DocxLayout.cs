using System.Text.RegularExpressions;
using PdfSharp.Drawing;

namespace Balsoft.Hive.BDC.Documents.Word;

// ── galley: everything positioned on one endless page; pages are cut out of it afterwards ──

internal abstract record Item(double Top, double Bottom)
{
    public abstract Item Shift(double dx, double dy);
}

internal sealed record TextItem(double X, double Baseline, double Top, double Bottom, string Text, XFont Font, XColor Color,
    bool Underline, bool Strike, XColor? Highlight, double Width, string? Field) : Item(Top, Bottom)
{
    public override Item Shift(double dx, double dy) => this with { X = X + dx, Baseline = Baseline + dy, Top = Top + dy, Bottom = Bottom + dy };
}

internal sealed record ImageItem(double X, double Y, double Width, double Height, byte[] Data) : Item(Y, Y + Height)
{
    public override Item Shift(double dx, double dy) => this with { X = X + dx, Y = Y + dy, Top = Top + dy, Bottom = Bottom + dy };
}

internal sealed record LineItem(double X1, double Y1, double X2, double Y2, double Width, XColor Color, string Kind)
    : Item(Math.Min(Y1, Y2), Math.Max(Y1, Y2))
{
    public override Item Shift(double dx, double dy)
        => this with { X1 = X1 + dx, X2 = X2 + dx, Y1 = Y1 + dy, Y2 = Y2 + dy, Top = Top + dy, Bottom = Bottom + dy };
}

internal sealed record RectItem(double X, double Y, double Width, double Height, XColor Color) : Item(Y, Y + Height)
{
    public override Item Shift(double dx, double dy) => this with { X = X + dx, Y = Y + dy, Top = Top + dy, Bottom = Bottom + dy };
}

internal sealed class Galley
{
    public List<Item> Back { get; } = new();      // shading, drawn first
    public List<Item> Content { get; } = new();   // text and images
    public List<Item> Lines { get; } = new();     // borders, drawn last
    /// <summary>Ranges a page must not be cut inside: every text line, images, rows that cannot split, kept paragraphs.</summary>
    public List<(double Top, double Bottom)> Keep { get; } = new();
    /// <summary>Hard page breaks (y positions).</summary>
    public List<double> Breaks { get; } = new();
    public double Height { get; set; }

    public void AddShifted(Galley other, double dx, double dy)
    {
        Back.AddRange(other.Back.Select(i => i.Shift(dx, dy)));
        Content.AddRange(other.Content.Select(i => i.Shift(dx, dy)));
        Lines.AddRange(other.Lines.Select(i => i.Shift(dx, dy)));
        Keep.AddRange(other.Keep.Select(k => (k.Top + dy, k.Bottom + dy)));
        Breaks.AddRange(other.Breaks.Select(b => b + dy));
    }
}

/// <summary>
/// Lays out <see cref="Block"/>s: greedy line breaking at spaces with Word's tab stops, alignment and line spacing,
/// tables on their grid with nested tables, spans, vertical merges, margins, borders and shading.
/// </summary>
internal sealed class DocxLayout
{
    private readonly XGraphics _measure;
    private readonly double _defaultTab;
    private readonly Dictionary<(string, double, bool, bool), XFont> _fonts = new();
    private readonly Dictionary<(string, XFont), double> _widths = new();
    private static readonly Regex Words = new(@"\S+|\s+", RegexOptions.CultureInvariant);

    private readonly bool _legacyRowHeight;

    public DocxLayout(double defaultTab, int compatibilityMode = 15)
    {
        _defaultTab = defaultTab > 0 ? defaultTab : 36;
        _legacyRowHeight = compatibilityMode <= 14;
        _measure = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards, null);
    }

    /// <summary>Receives one line per laid-out paragraph and table row (support tool --word-layout).</summary>
    public Action<string>? Trace { get; set; }
    private int _depth;

    /// <summary>Font families the laid-out documents asked for.</summary>
    public IEnumerable<string> Families => _fonts.Keys.Select(k => k.Item1).Distinct(StringComparer.OrdinalIgnoreCase);

    public Galley Layout(List<Block>? blocks, double x, double width, bool cell = false)
    {
        var g = new Galley();
        g.Height = LayoutBlocks(blocks ?? new List<Block>(), x, 0, width, g, cell);
        return g;
    }

    private double LayoutBlocks(List<Block> blocks, double x, double y, double width, Galley g, bool cell)
    {
        double? keepFrom = null;
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            double top = y;
            double firstBottom;
            // The paragraph Word requires after a nested table at the end of a cell takes no space when it is empty.
            if (cell && i == blocks.Count - 1 && i > 0 && blocks[i - 1] is TableBlock && block is ParaBlock { Inlines.Count: 0 })
                continue;
            switch (block)
            {
                case ParaBlock p:
                    if (p.Props.PageBreakBefore && y > 0) g.Breaks.Add(y);
                    (y, firstBottom) = LayoutParagraph(p, x, y, width, g);
                    break;
                case TableBlock t:
                    (y, firstBottom) = LayoutTable(t, x, y, width, g);
                    break;
                default:
                    continue;
            }
            if (keepFrom is { } from) g.Keep.Add((from, firstBottom));
            keepFrom = block is ParaBlock { Props.KeepNext: true } ? top : null;
        }
        return y;
    }

    // ── fonts and measuring ─────────────────────────────────────────────────

    internal XFont Font(TextStyle s)
    {
        double size = s.VertAlign == 0 ? s.Size : s.Size * 0.65;
        var key = (s.Font, size, s.Bold, s.Italic);
        if (!_fonts.TryGetValue(key, out var font))
        {
            var style = s.Bold && s.Italic ? XFontStyleEx.BoldItalic : s.Bold ? XFontStyleEx.Bold : s.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular;
            _fonts[key] = font = new XFont(s.Font, Math.Max(size, 1), style);
        }
        return font;
    }

    private double Width(string text, XFont font)
    {
        if (text.Length == 0) return 0;
        if (!_widths.TryGetValue((text, font), out double w))
            _widths[(text, font)] = w = _measure.MeasureString(text, font).Width;
        return w;
    }

    /// <summary>
    /// Ascent and descent for line height. Word's single line is the font's full line spacing (ascender, descender and
    /// line gap); the gap is added above the ascender so the baseline sits where Word puts it.
    /// </summary>
    internal static (double Ascent, double Descent) Metrics(XFont font)
    {
        var m = font.Metrics;
        double scale = font.Size / m.UnitsPerEm;
        double ascent = Math.Abs(m.Ascent) * scale, descent = Math.Abs(m.Descent) * scale;
        double gap = Math.Max(m.LineSpacing * scale - ascent - descent, 0);
        return (ascent + gap, descent);
    }

    // ── paragraphs ──────────────────────────────────────────────────────────

    private sealed class Token
    {
        public char Kind;            // w word, s space, t tab, b line break, p page break, i image, f field
        public string Text = "";
        public TextStyle? Style;
        public XFont? Font;
        public double Width, Ascent, Descent;
        public ImageInline? Image;
        public double X;
        public char TabKind;         // for tabs: l r c d
        public double TabStop;
    }

    private List<Token> Tokens(ParaBlock p)
    {
        var list = new List<Token>();
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    var font = Font(t.Style);
                    var (a, d) = Metrics(font);
                    string text = t.Style.Caps ? t.Text.ToUpperInvariant() : t.Text;
                    foreach (Match m in Words.Matches(text))
                        list.Add(new Token { Kind = char.IsWhiteSpace(m.Value[0]) ? 's' : 'w', Text = m.Value, Style = t.Style, Font = font,
                            Width = Width(m.Value, font), Ascent = a, Descent = d });
                    break;
                case TabInline tab:
                    var tf = Font(tab.Style);
                    var (ta, td) = Metrics(tf);
                    list.Add(new Token { Kind = 't', Style = tab.Style, Font = tf, Ascent = ta, Descent = td });
                    break;
                case BreakInline br:
                    list.Add(new Token { Kind = br.Page ? 'p' : 'b' });
                    break;
                case ImageInline img:
                    list.Add(new Token { Kind = 'i', Image = img, Width = img.Width, Ascent = img.Height });
                    break;
                case FieldInline f:
                    var ff = Font(f.Style);
                    var (fa, fd) = Metrics(ff);
                    list.Add(new Token { Kind = 'f', Text = f.Kind, Style = f.Style, Font = ff, Width = Width("0", ff), Ascent = fa, Descent = fd });
                    break;
            }
        }
        return list;
    }

    private (double Y, double FirstBottom) LayoutParagraph(ParaBlock p, double x, double y, double width, Galley g)
    {
        var props = p.Props;
        y += props.SpaceBefore;
        double paraTop = y;
        double left = x + props.IndentLeft, right = x + width - props.IndentRight;
        var tokens = Tokens(p);
        var markFont = Font(props.Mark);
        var (markAscent, markDescent) = Metrics(markFont);

        var lines = new List<(List<Token> Tokens, bool HardEnd, bool PageBreakAfter)>();
        var current = new List<Token>();
        double cx = left + props.FirstLine;
        bool firstLine = true;

        void Finish(bool hard, bool page)
        {
            while (current.Count > 0 && current[^1].Kind == 's') current.RemoveAt(current.Count - 1);
            lines.Add((current, hard, page));
            current = new List<Token>();
            cx = left;
            firstLine = false;
        }

        foreach (var tok in tokens)
        {
            switch (tok.Kind)
            {
                case 'b': Finish(true, false); continue;
                case 'p': Finish(true, true); continue;
                case 't':
                    var (stop, kind) = NextTab(props, x, left, cx);
                    if (stop > right + 0.5 && current.Count > 0) { Finish(false, false); (stop, kind) = NextTab(props, x, left, cx); }
                    tok.TabStop = stop;
                    tok.TabKind = kind;
                    tok.X = cx;
                    current.Add(tok);
                    cx = kind == 'l' ? stop : cx;   // right/center/decimal tabs are resolved when the line is placed
                    continue;
                case 's':
                    if (current.Count == 0 && !firstLine) continue;   // no leading spaces on wrapped lines
                    tok.X = cx;
                    current.Add(tok);
                    cx += tok.Width;
                    continue;
            }
            // word, image, field
            if (cx + tok.Width > right + 0.01 && current.Any(t => t.Kind is 'w' or 'i' or 'f'))
            {
                Finish(false, false);
            }
            if (tok.Kind == 'w' && tok.Width > right - cx + 0.01 && tok.Text.Length > 1)
            {
                foreach (var piece in SplitLongWord(tok, right - cx, right - left))
                {
                    if (cx + piece.Width > right + 0.01 && current.Count > 0) Finish(false, false);
                    piece.X = cx;
                    current.Add(piece);
                    cx += piece.Width;
                }
                continue;
            }
            tok.X = cx;
            current.Add(tok);
            cx += tok.Width;
        }
        if (current.Count > 0 || lines.Count == 0 || lines[^1].HardEnd) Finish(true, false);

        double firstBottom = y;
        int index = 0;
        foreach (var (lineTokens, hardEnd, pageAfter) in lines)
        {
            double ascent = markAscent, descent = markDescent;
            if (lineTokens.Count > 0)
            {
                ascent = lineTokens.Max(t => t.Ascent);
                descent = lineTokens.Max(t => t.Descent);
                if (lineTokens.All(t => t.Kind == 'i')) descent = Math.Max(descent, markDescent);
            }
            double natural = ascent + descent;
            double height = props.LineRule switch
            {
                'e' => props.LineValue,
                'l' => Math.Max(natural, props.LineValue),
                _ => natural * props.LineValue,
            };
            double top = y, baseline = y + height - descent;
            ResolveTabs(lineTokens);
            Align(lineTokens, props.Align, left, right, index == 0 ? props.FirstLine : 0, hardEnd || index == lines.Count - 1);
            foreach (var t in Runs(lineTokens, props.Align == 'j')) Emit(t, baseline, top, top + height, g);
            g.Keep.Add((top, top + height));
            y += height;
            if (index == 0) firstBottom = y;
            if (pageAfter) g.Breaks.Add(y);
            index++;
        }

        if (props.Fill is { } fill)
            g.Back.Add(new RectItem(left, paraTop, right - left, y - paraTop, Color(fill)));
        if (props.BorderTop is { Kind: not "none" } bt) AddLine(g, left, paraTop, right, paraTop, bt);
        if (props.BorderBottom is { Kind: not "none" } bb) AddLine(g, left, y, right, y, bb);
        if (props.KeepLines) g.Keep.Add((paraTop, y));
        if (Trace is not null)
        {
            string tx = string.Concat(p.Inlines.OfType<TextInline>().Select(t => t.Text)).Trim();
            Trace($"{new string(' ', _depth * 2)}P y={paraTop - props.SpaceBefore:0.0} h={y + props.SpaceAfter - paraTop + props.SpaceBefore:0.0} " +
                $"lines={lines.Count} '{(tx.Length > 24 ? tx[..24] : tx)}' mark={props.Mark.Font} {props.Mark.Size}");
        }
        return (y + props.SpaceAfter, firstBottom);
    }

    private IEnumerable<Token> SplitLongWord(Token tok, double firstRoom, double lineWidth)
    {
        string rest = tok.Text;
        double room = Math.Max(firstRoom, 1);
        while (rest.Length > 0)
        {
            int n = rest.Length;
            while (n > 1 && Width(rest[..n], tok.Font!) > room) n--;
            string piece = rest[..n];
            yield return new Token { Kind = 'w', Text = piece, Style = tok.Style, Font = tok.Font, Width = Width(piece, tok.Font!),
                Ascent = tok.Ascent, Descent = tok.Descent };
            rest = rest[n..];
            room = lineWidth;
        }
    }

    /// <summary>Next tab stop after <paramref name="cx"/>: explicit stops (relative to the text column), the hanging
    /// indent, then Word's default stops every defaultTabStop from the column start.</summary>
    private (double Stop, char Kind) NextTab(ParaProps props, double column, double left, double cx)
    {
        foreach (var t in props.Tabs)
            if (column + t.Position > cx + 0.01) return (column + t.Position, t.Kind);
        if (props.FirstLine < 0 && left > cx + 0.01) return (left, 'l');
        double rel = cx - column;
        double next = (Math.Floor(rel / _defaultTab + 1e-6) + 1) * _defaultTab;
        return (column + next, 'l');
    }

    private static void ResolveTabs(List<Token> line)
    {
        for (int i = 0; i < line.Count; i++)
        {
            var tab = line[i];
            if (tab.Kind != 't' || tab.TabKind == 'l') continue;
            int end = i + 1;
            while (end < line.Count && line[end].Kind != 't') end++;
            if (end == i + 1) continue;
            double segStart = line[i + 1].X;
            double segWidth = line[end - 1].X + line[end - 1].Width - segStart;
            if (tab.TabKind == 'd')
            {
                int dot = i + 1;
                double before = 0;
                for (; dot < end; dot++)
                {
                    int k = line[dot].Text.IndexOfAny(new[] { ',', '.' });
                    if (k >= 0) { before += k > 0 ? line[dot].Width * k / line[dot].Text.Length : 0; break; }
                    before += line[dot].Width;
                }
                segWidth = dot < end ? before : segWidth;
            }
            double target = tab.TabKind == 'c' ? tab.TabStop - segWidth / 2 : tab.TabStop - segWidth;
            double shift = target - segStart;
            if (shift <= 0) continue;
            for (int k = i + 1; k < line.Count; k++) line[k].X += shift;
        }
    }

    private static void Align(List<Token> line, char align, double left, double right, double firstLine, bool lastLine)
    {
        if (line.Count == 0 || align == 'l') return;
        if (line.Any(t => t.Kind == 't')) return;   // tabbed lines keep their positions
        double start = left + firstLine;
        double end = line[^1].X + line[^1].Width;
        double room = right - end;
        if (room <= 0) return;
        if (align == 'j')
        {
            if (lastLine) return;
            var spaces = line.Where(t => t.Kind == 's').ToList();
            if (spaces.Count == 0) return;
            double extra = room / spaces.Count, shift = 0;
            foreach (var t in line)
            {
                t.X += shift;
                if (t.Kind == 's') { t.Width += extra; shift += extra; }
            }
            return;
        }
        double offset = align == 'c' ? room / 2 : room;
        if (line[0].X < start) offset = Math.Max(offset, 0);
        foreach (var t in line) t.X += offset;
    }

    /// <summary>
    /// Joins neighbouring words and spaces of one style into one drawn string, so the PDF carries real spaces (copy,
    /// search, screen readers). Justified lines keep word by word, their spaces are stretched.
    /// </summary>
    private static IEnumerable<Token> Runs(List<Token> line, bool justified)
    {
        Token? run = null;
        foreach (var t in line)
        {
            bool text = t.Kind is 'w' or 's';
            if (!justified && text && run is not null && Equals(run.Style, t.Style) && Math.Abs(run.X + run.Width - t.X) < 0.01)
            {
                run.Text += t.Text;
                run.Width += t.Width;
                continue;
            }
            if (run is not null) yield return run;
            run = text
                ? new Token { Kind = 'w', Text = t.Text, Style = t.Style, Font = t.Font, Width = t.Width, X = t.X, Ascent = t.Ascent, Descent = t.Descent }
                : null;
            if (run is null) yield return t;
        }
        if (run is not null) yield return run;
    }

    private static void Emit(Token t, double baseline, double top, double bottom, Galley g)
    {
        switch (t.Kind)
        {
            case 'w' or 's' or 'f':
                if (t.Kind == 's' && t.Style is { Underline: false, Highlight: null }) return;
                var s = t.Style!;
                double shift = s.VertAlign == 1 ? -s.Size * 0.33 : s.VertAlign == 2 ? s.Size * 0.14 : 0;
                g.Content.Add(new TextItem(t.X, baseline + shift, top, bottom, t.Kind == 'f' ? "" : t.Text, t.Font!,
                    s.Color is { } c ? Color(c) : XColors.Black, s.Underline, s.Strike, Highlight(s.Highlight), t.Width,
                    t.Kind == 'f' ? t.Text : null));
                return;
            case 'i':
                g.Content.Add(new ImageItem(t.X, baseline - t.Image!.Height, t.Image.Width, t.Image.Height, t.Image.Data));
                return;
        }
    }

    // ── tables ──────────────────────────────────────────────────────────────

    private sealed class PlacedCell
    {
        public required CellModel Cell;
        public int Column;
        public double X, Width;
        public Margins Margins = new(0, 0, 0, 0);
        public Galley? Content;
        public int MergeRows = 1;   // for a restart cell: how many rows it spans
    }

    private (double Y, double FirstBottom) LayoutTable(TableBlock t, double x, double y, double width, Galley g)
    {
        var grid = t.Grid.ToList();
        int columns = Math.Max(grid.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.GridBefore + r.Cells.Sum(c => c.Span)));
        if (grid.Count < columns)
        {
            double used = grid.Sum();
            double each = Math.Max((width - used) / Math.Max(columns - grid.Count, 1), 10);
            while (grid.Count < columns) grid.Add(each);
        }
        double tableWidth = grid.Sum();
        double left = x + t.Indent + (t.Align == 'c' ? (width - tableWidth) / 2 : t.Align == 'r' ? width - tableWidth : 0);
        double ColumnX(int c) => left + grid.Take(c).Sum();
        double ColumnsWidth(int c, int span) => grid.Skip(c).Take(span).Sum();

        // place cells on the grid and lay out their content
        Trace?.Invoke($"{new string(' ', _depth * 2)}TABLE y={y:0.0} cols={grid.Count} width={tableWidth:0.0}");
        _depth++;
        var rows = new List<List<PlacedCell>>();
        var heights = new List<double>();
        foreach (var row in t.Rows)
        {
            var placed = new List<PlacedCell>();
            int col = row.GridBefore;
            double h = 0, legacyAtLeast = 0;
            foreach (var cell in row.Cells)
            {
                var defaults = row.CellMargins ?? t.CellMargins;
                var m = cell.Margins is { } cm
                    ? new Margins(double.IsNaN(cm.Top) ? defaults.Top : cm.Top, double.IsNaN(cm.Left) ? defaults.Left : cm.Left,
                        double.IsNaN(cm.Bottom) ? defaults.Bottom : cm.Bottom, double.IsNaN(cm.Right) ? defaults.Right : cm.Right)
                    : defaults;
                var pc = new PlacedCell { Cell = cell, Column = col, X = ColumnX(col), Width = ColumnsWidth(col, cell.Span), Margins = m };
                if (cell.VMerge != 'c')
                {
                    pc.Content = Layout(cell.Blocks, 0, Math.Max(pc.Width - m.Left - m.Right, 1), cell: true);
                    if (cell.VMerge != 'r') h = Math.Max(h, pc.Content.Height + m.Top + m.Bottom);
                }
                legacyAtLeast = Math.Max(legacyAtLeast, row.Height + m.Top + m.Bottom);
                placed.Add(pc);
                col += cell.Span;
            }
            Trace?.Invoke($"{new string(' ', _depth * 2)}ROW content={h:0.0} rule={row.HeightRule} spec={row.Height:0.0}");
            // Word 2003 layout (compatibility mode 14 and below) counts an "at least" row height without the cell's
            // top and bottom margins; newer modes count the whole row.
            double atLeast = _legacyRowHeight ? legacyAtLeast : row.Height;
            h = row.HeightRule switch { 'e' => row.Height, 'l' => Math.Max(h, atLeast), _ => h };
            rows.Add(placed);
            heights.Add(h);
        }

        // vertical merges: the restart cell spans the following "continue" cells in the same column
        for (int r = 0; r < rows.Count; r++)
            foreach (var pc in rows[r].Where(c => c.Cell.VMerge == 'r'))
            {
                int span = 1;
                while (r + span < rows.Count && rows[r + span].Any(c => c.Column == pc.Column && c.Cell.VMerge == 'c')) span++;
                pc.MergeRows = span;
                double need = pc.Content!.Height + pc.Margins.Top + pc.Margins.Bottom;
                double have = heights.Skip(r).Take(span).Sum();
                if (need > have && t.Rows[r + span - 1].HeightRule != 'e') heights[r + span - 1] += need - have;
            }

        _depth--;
        double rowTop = y, firstBottom = y;
        for (int r = 0; r < rows.Count; r++)
        {
            var model = t.Rows[r];
            var borders = model.Borders ?? t.Borders;
            foreach (var pc in rows[r])
            {
                double cellHeight = heights.Skip(r).Take(pc.MergeRows).Sum();
                if (pc.Cell.VMerge == 'c') cellHeight = heights[r];
                if (pc.Cell.Fill is { } fill && pc.Cell.VMerge != 'c')
                    g.Back.Add(new RectItem(pc.X, rowTop, pc.Width, cellHeight, Color(fill)));
                if (pc.Content is { } content)
                {
                    double inner = cellHeight - pc.Margins.Top - pc.Margins.Bottom;
                    double offset = pc.Cell.VAlign switch
                    {
                        'c' => Math.Max((inner - content.Height) / 2, 0),
                        'b' => Math.Max(inner - content.Height, 0),
                        _ => 0,
                    };
                    g.AddShifted(content, pc.X + pc.Margins.Left, rowTop + pc.Margins.Top + offset);
                }

                bool firstCol = pc.Column == model.GridBefore, lastCol = pc == rows[r][^1];
                bool continuesBelow = r + 1 < rows.Count && rows[r + 1].Any(c => c.Column == pc.Column && c.Cell.VMerge == 'c');
                int lastRow = r + pc.MergeRows - 1;
                var top = pc.Cell.Borders.Top ?? (r == 0 ? borders.Top : borders.InsideH);
                var bottom = pc.Cell.Borders.Bottom ?? (lastRow == rows.Count - 1 ? borders.Bottom : borders.InsideH);
                var leftB = pc.Cell.Borders.Left ?? (firstCol ? borders.Left : borders.InsideV);
                var rightB = pc.Cell.Borders.Right ?? (lastCol ? borders.Right : borders.InsideV);
                if (pc.Cell.VMerge != 'c' && top is { Kind: not "none" }) AddLine(g, pc.X, rowTop, pc.X + pc.Width, rowTop, top);
                if (!continuesBelow && pc.Cell.VMerge != 'c' && bottom is { Kind: not "none" })
                    AddLine(g, pc.X, rowTop + cellHeight, pc.X + pc.Width, rowTop + cellHeight, bottom);
                if (leftB is { Kind: not "none" }) AddLine(g, pc.X, rowTop, pc.X, rowTop + cellHeight, leftB);
                if (rightB is { Kind: not "none" }) AddLine(g, pc.X + pc.Width, rowTop, pc.X + pc.Width, rowTop + cellHeight, rightB);
                if (pc.Cell.VMerge == 'c' && bottom is { Kind: not "none" } && !continuesBelow)
                    AddLine(g, pc.X, rowTop + cellHeight, pc.X + pc.Width, rowTop + cellHeight, bottom);
            }
            if (model.CantSplit || model.HeightRule == 'e') g.Keep.Add((rowTop, rowTop + heights[r]));
            rowTop += heights[r];
            if (r == 0) firstBottom = rowTop;
        }
        return (rowTop, firstBottom);
    }

    // ── drawing helpers ─────────────────────────────────────────────────────

    private static void AddLine(Galley g, double x1, double y1, double x2, double y2, BorderDef b)
        => g.Lines.Add(new LineItem(x1, y1, x2, y2, b.Width, Color(b.Color), b.Kind));

    internal static XColor Color(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v)) return XColors.Black;
        return XColor.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    private static XColor? Highlight(string? name) => name switch
    {
        null => null,
        _ when name.StartsWith('#') => Color(name),
        "yellow" => Color("FFFF00"), "green" => Color("00FF00"), "cyan" => Color("00FFFF"), "magenta" => Color("FF00FF"),
        "blue" => Color("0000FF"), "red" => Color("FF0000"), "darkBlue" => Color("000080"), "darkCyan" => Color("008080"),
        "darkGreen" => Color("008000"), "darkMagenta" => Color("800080"), "darkRed" => Color("800000"),
        "darkYellow" => Color("808000"), "darkGray" => Color("808080"), "lightGray" => Color("C0C0C0"),
        "black" => Color("000000"), "white" => Color("FFFFFF"), _ => Color("FFFF00"),
    };
}