using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Balsoft.Hive.BDC.Documents.Word;

/// <summary>The rendered PDF and what the renderer could not draw.</summary>
public sealed record WordRenderResult(byte[] Pdf, int Pages, IReadOnlyList<string> Unsupported, IReadOnlyList<string> MissingFonts);

/// <summary>
/// Hive BDC's own Word to PDF renderer for invoice templates: page setup, header and footer, paragraphs, tables
/// (nested), inline images, content controls, PAGE and NUMPAGES. Layout runs on an endless galley; pages are cut
/// between lines and rows. Word elements outside that subset are reported in <see cref="WordRenderResult.Unsupported"/>
/// instead of being drawn wrong; the caller decides whether the PDF may be used.
/// </summary>
public static class DocxPdfRenderer
{
    private const double Eps = 0.01;

    public static WordRenderResult Render(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Render(fs, Path.GetFileNameWithoutExtension(path));
    }

    public static WordRenderResult Render(Stream docx, string title = "", Action<string>? trace = null)
        => Render(docx, title, trace, null);

    /// <param name="pageText">Receives the strings drawn on each page, in drawing order (tests).</param>
    internal static WordRenderResult Render(Stream docx, string title, Action<string>? trace, List<string>? pageText)
    {
        var fonts = SystemFontResolver.Install();
        if (fonts.FontCount == 0)
            throw new InvalidOperationException("No TrueType fonts found in " + string.Join(", ", fonts.Folders) + ".");

        DocModel model;
        using (var wd = WordprocessingDocument.Open(docx, false))
            model = DocxModel.Read(wd);

        var layout = new DocxLayout(model.DefaultTab, model.CompatibilityMode) { Trace = trace };
        double contentWidth = model.PageWidth - model.MarginLeft - model.MarginRight;
        trace?.Invoke($"page {model.PageWidth:0.0} x {model.PageHeight:0.0}, margins top {model.MarginTop:0.0} bottom {model.MarginBottom:0.0}, " +
            $"header {model.HeaderDistance:0.0}, footer {model.FooterDistance:0.0}");

        trace?.Invoke("== header");
        var header = layout.Layout(model.Header, model.MarginLeft, contentWidth);
        var firstHeader = model.TitlePage ? layout.Layout(model.FirstHeader, model.MarginLeft, contentWidth) : header;
        trace?.Invoke("== footer");
        var footer = layout.Layout(model.Footer, model.MarginLeft, contentWidth);
        var firstFooter = model.TitlePage ? layout.Layout(model.FirstFooter, model.MarginLeft, contentWidth) : footer;
        trace?.Invoke("== body");
        var body = layout.Layout(model.Body, model.MarginLeft, contentWidth);
        trace?.Invoke($"header {header.Height:0.0}, footer {footer.Height:0.0}, body {body.Height:0.0}");

        double BodyTop(int page) => Math.Max(model.MarginTop, model.HeaderDistance + (page == 0 ? firstHeader : header).Height);
        double BodyBottom(int page) => model.PageHeight - Math.Max(model.MarginBottom, model.FooterDistance + (page == 0 ? firstFooter : footer).Height);

        var cuts = Paginate(body, BodyTop, BodyBottom);

        using var pdf = new PdfDocument();
        pdf.Info.Title = title;
        pdf.Info.Creator = "Hive BDC";
        for (int i = 0; i < cuts.Count; i++)
        {
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(model.PageWidth);
            page.Height = XUnit.FromPoint(model.PageHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            var ctx = new PageContext(i + 1, cuts.Count, pageText is null ? null : new System.Text.StringBuilder());

            var h = i == 0 ? firstHeader : header;
            Draw(gfx, h, 0, model.HeaderDistance, double.NegativeInfinity, double.PositiveInfinity, ctx);

            double top = BodyTop(i), bottom = BodyBottom(i);
            var (from, to) = cuts[i];
            var state = gfx.Save();
            gfx.IntersectClip(new XRect(0, top - 1, model.PageWidth, bottom - top + 2));
            Draw(gfx, body, 0, top - from, from, to, ctx);
            gfx.Restore(state);

            var f = i == 0 ? firstFooter : footer;
            Draw(gfx, f, 0, model.PageHeight - model.FooterDistance - f.Height, double.NegativeInfinity, double.PositiveInfinity, ctx);
            if (ctx.Text is not null) pageText!.Add(ctx.Text.ToString());
        }

        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        var missing = layout.Families.Where(f => !fonts.Has(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new WordRenderResult(ms.ToArray(), cuts.Count, model.Unsupported.ToList(), missing);
    }

    /// <summary>What a template uses that the renderer does not draw (empty = renderable).</summary>
    public static IReadOnlyList<string> FindUnsupported(Stream docx)
    {
        using var wd = WordprocessingDocument.Open(docx, false);
        return DocxModel.Read(wd).Unsupported.ToList();
    }

    private sealed record PageContext(int Page, int Pages, System.Text.StringBuilder? Text);

    /// <summary>
    /// Page ranges on the galley. A page ends at the last position no kept range (a text line, an image, a row that
    /// cannot split, a paragraph kept with the next) crosses; a hard page break ends it early.
    /// </summary>
    internal static List<(double From, double To)> Paginate(Galley body, Func<int, double> top, Func<int, double> bottom)
    {
        var cuts = new List<(double, double)>();
        var candidates = body.Keep.SelectMany(k => new[] { k.Top, k.Bottom }).Append(body.Height).Distinct().OrderBy(v => v).ToArray();
        double pos = 0;
        for (int page = 0; page < 1000; page++)
        {
            double room = Math.Max(bottom(page) - top(page), 10);
            double limit = pos + room;
            double? hard = body.Breaks.Where(b => b > pos + Eps && b <= limit + Eps).Select(b => (double?)b).Min();
            double cut;
            if (hard is { } h) cut = h;
            else if (body.Height <= limit + Eps) cut = body.Height;
            else
            {
                cut = candidates.Where(c => c > pos + Eps && c <= limit + Eps && IsSafe(body, c)).DefaultIfEmpty(double.NaN).Max();
                if (double.IsNaN(cut)) cut = limit;   // one kept block taller than a page: cut it
            }
            cuts.Add((pos, cut));
            pos = cut;
            if (pos >= body.Height - Eps) break;
        }
        if (cuts.Count == 0) cuts.Add((0, 0));
        return cuts;
    }

    private static bool IsSafe(Galley g, double y) => !g.Keep.Any(k => k.Top < y - Eps && k.Bottom > y + Eps);

    private static void Draw(XGraphics gfx, Galley g, double dx, double dy, double from, double to, PageContext ctx)
    {
        bool Visible(Item i) => i.Bottom > from + Eps && i.Top < to - Eps;
        bool Starts(Item i) => i.Top >= from - Eps && i.Top < to - Eps;

        foreach (var item in g.Back.Where(Visible).Cast<RectItem>())
            gfx.DrawRectangle(new XSolidBrush(item.Color), item.X + dx, item.Y + dy, item.Width, item.Height);

        foreach (var item in g.Content.Where(Starts))
        {
            switch (item)
            {
                case TextItem t:
                    string text = t.Field switch
                    {
                        "PAGE" => ctx.Page.ToString(CultureInfo.InvariantCulture),
                        "NUMPAGES" => ctx.Pages.ToString(CultureInfo.InvariantCulture),
                        _ => t.Text,
                    };
                    double width = t.Field is null ? t.Width : gfx.MeasureString(text, t.Font).Width;
                    if (t.Highlight is { } hl)
                        gfx.DrawRectangle(new XSolidBrush(hl), t.X + dx, t.Top + dy, width, t.Bottom - t.Top);
                    if (text.Trim().Length > 0)
                        gfx.DrawString(text, t.Font, new XSolidBrush(t.Color), t.X + dx, t.Baseline + dy, XStringFormats.BaseLineLeft);
                    ctx.Text?.Append(text);
                    if (t.Underline || t.Strike)
                    {
                        var m = t.Font.Metrics;
                        double scale = t.Font.Size / m.UnitsPerEm;
                        var pen = new XPen(t.Color, Math.Max(m.UnderlineThickness * scale, 0.5));
                        if (t.Underline)
                        {
                            double uy = t.Baseline + dy - m.UnderlinePosition * scale;
                            gfx.DrawLine(pen, t.X + dx, uy, t.X + dx + width, uy);
                        }
                        if (t.Strike)
                        {
                            double sy = t.Baseline + dy - m.StrikethroughPosition * scale;
                            gfx.DrawLine(pen, t.X + dx, sy, t.X + dx + width, sy);
                        }
                    }
                    break;
                case ImageItem img:
                    using (var ms = new MemoryStream(img.Data))
                    {
                        var image = XImage.FromStream(ms);
                        image.Interpolate = false;   // PDF/A forbids /Interpolate true (ISO 19005-3, 6.2.8), the ZUGFeRD carrier must pass
                        gfx.DrawImage(image, img.X + dx, img.Y + dy, img.Width, img.Height);
                    }
                    break;
            }
        }

        foreach (var line in g.Lines.Where(Visible).Cast<LineItem>())
        {
            var pen = new XPen(line.Color, line.Width)
            {
                DashStyle = line.Kind switch { "dotted" => XDashStyle.Dot, "dashed" => XDashStyle.Dash, _ => XDashStyle.Solid },
            };
            if (line.Kind == "double")
            {
                bool horizontal = Math.Abs(line.Y1 - line.Y2) < Eps;
                double gap = Math.Max(line.Width * 1.5, 1);
                double ox = horizontal ? 0 : gap / 2, oy = horizontal ? gap / 2 : 0;
                gfx.DrawLine(pen, line.X1 + dx - ox, line.Y1 + dy - oy, line.X2 + dx - ox, line.Y2 + dy - oy);
                gfx.DrawLine(pen, line.X1 + dx + ox, line.Y1 + dy + oy, line.X2 + dx + ox, line.Y2 + dy + oy);
            }
            else
                gfx.DrawLine(pen, line.X1 + dx, line.Y1 + dy, line.X2 + dx, line.Y2 + dy);
        }
    }
}