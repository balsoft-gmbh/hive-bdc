using System.IO;
using System.Windows;
using System.Windows.Controls;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.Documents.Word;
using Microsoft.Win32;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>
/// Shows what Hive BDC reads from a Word invoice before anyone drops it into the input folder: every field with its
/// value and status, the lines, the totals check, the format, and Word elements the renderer does not draw. Runs the
/// service's own reader and renderer (linked into the dashboard); nothing is sent to the service.
/// </summary>
public partial class WordCheckDialog : UserControl
{
    private readonly string _path;

    public sealed record FieldRow(string Tag, string Title, string Value, string Status);
    public sealed record LineRow(string Position, string Name, string Quantity, string Unit, string Price, string Vat, string Amount);

    public string FileName { get; }
    public string Verdict { get; }
    public string Totals { get; } = "";
    public List<string> Problems { get; } = new();
    public List<string> Notes { get; } = new();
    public List<FieldRow> Fields { get; } = new();
    public List<LineRow> Lines { get; } = new();

    /// <summary>The dialog asks its host to close it.</summary>
    public event Action? CloseRequested;

    private static readonly HashSet<string> Required = new(StringComparer.OrdinalIgnoreCase) { "BT-1", "BT-2", "BT-44" };
    private static readonly HashSet<string> RequiredForXRechnung = new(StringComparer.OrdinalIgnoreCase) { "BT-10", "BT-49" };

    public WordCheckDialog(string path)
    {
        InitializeComponent();
        ListColumns.TabOnce(FieldsGrid);
        ListColumns.TabOnce(LinesGrid);
        _path = path;
        FileName = Path.GetFileName(path);

        WordInvoice invoice;
        IReadOnlyList<string> unsupported;
        try
        {
            invoice = WordInvoiceReader.Read(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            unsupported = DocxPdfRenderer.FindUnsupported(fs);
        }
        catch (Exception ex)
        {
            Verdict = "The file cannot be read: " + ex.Message;
            Problems.Add(Verdict);
            DataContext = this;
            return;
        }

        Problems.AddRange(invoice.Problems);
        if (unsupported.Count > 0)
            Problems.Add("Word elements Hive BDC cannot draw: " + string.Join(", ", unsupported) +
                         ". Remove them, or save the PDF from Word with the same name next to the .docx.");
        Notes.AddRange(invoice.Notes);

        string format = invoice.Format == "zugferd" ? "ZUGFeRD" : "XRechnung";
        string type = (string?)invoice.Json["invoiceTypeCode"] ?? "380";
        Verdict = Problems.Count == 0
            ? $"Ready: Hive BDC produces {format} (invoice type {type}) with {invoice.Lines.Count} line(s)."
            : $"Not ready: {Problems.Count} problem(s). Target format {format}, invoice type {type}.";
        if (invoice.Totals is { } t)
        {
            string printed = t.PrintedNet is null && t.PrintedTax is null && t.PrintedGross is null ? "no printed totals"
                : t.PrintedNet == t.Net && t.PrintedTax == t.Tax && t.PrintedGross == t.Gross ? "printed totals match" : "printed totals differ";
            Totals = $"Lines add up to net {WordInvoiceReader.Fmt(t.Net)} · VAT {WordInvoiceReader.Fmt(t.Tax)} · gross {WordInvoiceReader.Fmt(t.Gross)} ({printed}).";
        }

        foreach (var f in invoice.Fields)
            Fields.Add(new FieldRow(f.Tag, f.Title, f.IsPlaceholder ? "" : f.Value, Status(f, invoice.Format)));
        foreach (var line in invoice.Lines)
        {
            string V(string tag) => line.FirstOrDefault(f => string.Equals(f.Tag, tag, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            Lines.Add(new LineRow(V("BT-126"), V("BT-153"), V("BT-129"), V("BT-130"), V("BT-146"), V("BT-152"), V("BT-131")));
        }
        DataContext = this;
    }

    private static string Status(WordField f, string format)
    {
        bool required = Required.Contains(f.Tag) || (format == "xrechnung" && RequiredForXRechnung.Contains(f.Tag));
        if (f.IsPlaceholder) return required ? "required, still placeholder" : "placeholder (empty)";
        if (f.Value.Length == 0) return required ? "required, empty" : "empty";
        return "ok";
    }

    private void OnSavePreview(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dlg = new SaveFileDialog
        {
            Title = "Save the PDF Hive BDC would make",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(_path) + " (Hive BDC preview).pdf",
        };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            var result = DocxPdfRenderer.Render(_path);
            File.WriteAllBytes(dlg.FileName, result.Pdf);
            AppLog.Info($"Saved a Word PDF preview of {FileName} ({result.Pages} page(s)) to {Path.GetFileName(dlg.FileName)}.");
            string fonts = result.MissingFonts.Count == 0 ? "" :
                $"\n\nFonts not installed on this computer (replaced by {SystemFontResolver.FallbackFamily}): {string.Join(", ", result.MissingFonts)}.";
            MessageBox.Show(owner!, $"Saved {result.Pages} page(s) to {Path.GetFileName(dlg.FileName)}.{fonts}", "Hive BDC",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Word PDF preview of {FileName} failed: {ex.Message}");
            MessageBox.Show(owner!, "The preview could not be made: " + ex.Message, "Hive BDC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}