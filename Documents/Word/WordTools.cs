using System.Text.Json;

namespace Balsoft.Hive.BDC.Documents.Word;

/// <summary>Command line utilities for Word input: read, render, write the template. Null = not a Word command.</summary>
public static class WordTools
{
    public static int? TryRun(string[] args)
    {
        string? Arg(string flag, int offset = 1)
        {
            int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + offset < args.Length ? args[i + offset] : null;
        }

        if (Arg("--word-read") is { } read)
        {
            var inv = WordInvoiceReader.Read(read);
            Console.WriteLine(inv.Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            if (inv.Totals is { } t)
                Console.WriteLine($"totals: net {t.Net} tax {t.Tax} gross {t.Gross}; printed net {t.PrintedNet} tax {t.PrintedTax} gross {t.PrintedGross}");
            Console.WriteLine($"format: {inv.Format}; fields {inv.Fields.Count}, lines {inv.Lines.Count}");
            foreach (var n in inv.Notes) Console.WriteLine("note: " + n);
            foreach (var p in inv.Problems) Console.WriteLine("problem: " + p);
            return inv.Problems.Count == 0 ? 0 : 1;
        }
        if (Arg("--word-pdf") is { } source)
        {
            string target = Arg("--word-pdf", 2) is { } o && o.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? o : Path.ChangeExtension(source, ".pdf");
            var result = DocxPdfRenderer.Render(source);
            File.WriteAllBytes(target, result.Pdf);
            Console.WriteLine($"written: {target} ({result.Pages} page(s), {result.Pdf.Length} bytes)");
            foreach (var u in result.Unsupported) Console.WriteLine("unsupported: " + u);
            foreach (var f in result.MissingFonts) Console.WriteLine($"font not installed, {SystemFontResolver.FallbackFamily} used: " + f);
            return result.Unsupported.Count == 0 ? 0 : 1;
        }
        if (Arg("--word-layout") is { } layoutFile)
        {
            using var fs = new FileStream(layoutFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = DocxPdfRenderer.Render(fs, "", Console.WriteLine);
            Console.WriteLine($"pages: {result.Pages}");
            return 0;
        }
        if (Arg("--word-template") is { } template)
        {
            using (var fs = File.Create(template)) WordTemplateBuilder.Write(fs);
            Console.WriteLine("written: " + template);
            return 0;
        }
        return null;
    }
}