using System.Security.Cryptography;
using System.Text;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>What compressing one PDF did, for the document history.</summary>
public sealed record PdfCompression(long Before, long After, string? NotCompressedReason = null)
{
    public bool Compressed => NotCompressedReason is null;

    /// <summary>English text for the history, so the numbers are written the same on every server culture.</summary>
    public string Summary => Compressed
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Compressed {Size(Before)} to {Size(After)} ({Percent}% smaller).")
        : $"Not compressed: {NotCompressedReason}.";

    private int Percent => Before <= 0 ? 0 : (int)Math.Round(100.0 * (Before - After) / Before);

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
    };
}

/// <summary>
/// Makes a PDF smaller without changing what it shows (the Compress PDF option of a document
/// type): fonts embedded in full keep only the glyphs used, lossless images placed above
/// 300 ppi are scaled to 200 ppi, identical streams are stored once, ASCII85 and ASCIIHex
/// wrappers go, uncompressed streams get Flate, and page thumbnails and PieceInfo are dropped.
/// Text and vector content are not touched: the result is kept only when it is smaller, has
/// the same pages and the same decoded content stream on every page. Signed and encrypted
/// PDFs are left alone, PDF/A documents too when the caller asks for it. Nothing here throws
/// for a PDF it cannot handle; the original stays and the reason is returned.
/// </summary>
public static class PdfCompressor
{
    public const int TargetPpi = 200;

    /// <summary>A stream that inflates beyond this is kept as it is rather than re-encoded.</summary>
    private const long MaxStreamLength = 64L * 1024 * 1024;
    public const double DownsampleThreshold = 1.5;
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>Compresses the file in place when that makes it smaller; the file is replaced in one move.</summary>
    public static PdfCompression CompressFile(string path, bool keepPdfA, Action<string>? log = null, TimeSpan? timeLimit = null)
    {
        byte[] original = File.ReadAllBytes(path);
        var (bytes, result) = Compress(original, keepPdfA, log, timeLimit);
        if (bytes is null) return result;
        string temp = path + ".compress.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        return result;
    }

    /// <summary>The smaller PDF (null when the original stays) and what happened.</summary>
    public static (byte[]? Pdf, PdfCompression Result) Compress(byte[] pdf, bool keepPdfA, Action<string>? log = null, TimeSpan? timeLimit = null)
    {
        long before = pdf.Length;
        PdfCompression Keep(string reason) => new(before, before, reason);
        using var cancel = new CancellationTokenSource(timeLimit ?? TimeLimit);
        try
        {
            if (Contains(pdf, "/Encrypt")) return (null, Keep("encrypted PDF"));
            if (Contains(pdf, "/ByteRange")) return (null, Keep("signed PDF"));
            var doc = Open(pdf, out string? repaired);
            if (doc is null) return (null, Keep("the PDF could not be read"));
            using (doc)
            {
                if (repaired is not null) log?.Invoke($"PDF repaired before compressing ({repaired}).");
                if (IsSigned(doc)) return (null, Keep("signed PDF"));
                if (keepPdfA && IsPdfA(doc)) return (null, Keep("PDF/A document"));

                int pages = doc.PageCount;
                var contents = doc.Pages.Cast<PdfPage>().Select(PdfImageScaler.PageContent).ToList();
                var withoutGroup = doc.Pages.Cast<PdfPage>().Where(p => !p.Elements.ContainsKey("/Group")).ToList();
                var originalMetadata = doc.Internals.Catalog.Elements.GetReference("/Metadata")?.Value as PdfDictionary;

                int fonts = FontSubsetter.SubsetEmbeddedFonts(doc, log);
                cancel.Token.ThrowIfCancellationRequested();
                var images = PdfImageScaler.Run(doc, TargetPpi, DownsampleThreshold, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                int streams = CleanStreams(doc, cancel.Token);
                int merged = MergeDuplicates(doc, cancel.Token);
                DropExtras(doc);

                doc.Options.CompressContentStreams = true;
                doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
                doc.Options.NoCompression = false;
                using var output = new MemoryStream();
                doc.Save(output, closeStream: false);
                byte[] result = UndoSaveAdditions(output.ToArray(), doc, withoutGroup, originalMetadata);

                if (result.Length >= before) return (null, Keep("already compact"));
                if (!SameContent(result, pages, contents)) return (null, Keep("the page content would change"));
                log?.Invoke($"PDF compressed: {fonts} font(s) subset, {images.Scaled} image(s) scaled to {TargetPpi} ppi, " +
                    $"{images.Reencoded} re-encoded, {streams} stream(s) cleaned, {merged} duplicate(s) merged.");
                return (result, new PdfCompression(before, result.Length));
            }
        }
        catch (OperationCanceledException)
        {
            return (null, Keep($"took longer than {(timeLimit ?? TimeLimit).TotalSeconds:0} seconds"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Invoke($"PDF not compressed, the original is kept: {ex.Message}");
            return (null, Keep($"the PDF could not be processed ({ex.GetType().Name})"));
        }
    }

    private static PdfDocument? Open(byte[] pdf, out string? repaired)
    {
        repaired = null;
        try { return PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify); }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or FormatException or NotImplementedException or ArgumentException)
        {
            string source = Path.Combine(Path.GetTempPath(), $"hivebdc-compress-{Guid.NewGuid():N}.pdf");
            try
            {
                File.WriteAllBytes(source, pdf);
                if (!PdfRepair.TryRepair(source, out string fixedPath, out string summary)) return null;
                try
                {
                    repaired = summary;
                    return PdfReader.Open(new MemoryStream(File.ReadAllBytes(fixedPath)), PdfDocumentOpenMode.Modify);
                }
                catch (Exception again) when (again is PdfReaderException or InvalidOperationException or FormatException or NotImplementedException or ArgumentException)
                {
                    return null;
                }
                finally { TryDelete(fixedPath); }
            }
            finally { TryDelete(source); }
        }
    }

    private static bool IsSigned(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.ContainsKey("/Perms")) return true;
        if (catalog.Elements.GetDictionary("/AcroForm") is { } form && (form.Elements.GetInteger("/SigFlags") & 1) != 0) return true;
        return doc.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Any(d => d.Elements.ContainsKey("/ByteRange") || d.Elements.GetName("/Type") == "/Sig" || d.Elements.GetName("/FT") == "/Sig");
    }

    private static bool IsPdfA(PdfDocument doc)
    {
        if (doc.Internals.Catalog.Elements.GetReference("/Metadata")?.Value is not PdfDictionary meta || meta.Stream is null) return false;
        string xmp;
        try { xmp = Encoding.UTF8.GetString(meta.Stream.UnfilteredValue); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return true; }
        return xmp.Contains("pdfaid:part", StringComparison.Ordinal);
    }

    /// <summary>ASCII wrappers off, Flate on uncompressed streams, and a Flate stream re-encoded when that is smaller.</summary>
    private static int CleanStreams(PdfDocument doc, CancellationToken cancel)
    {
        int changed = 0;
        foreach (var dict in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            cancel.ThrowIfCancellationRequested();
            if (dict.Stream is null || dict.Elements.GetName("/Type") is "/Metadata" or "/EmbeddedFile") continue;
            var filters = Filters(dict, out var parms);
            if (filters is null) continue;
            bool touched = false;
            byte[] value = dict.Stream.Value;

            while (filters.Count > 0 && filters[0] is "/ASCII85Decode" or "/A85" or "/ASCIIHexDecode" or "/AHx")
            {
                value = PdfSharp.Pdf.Filters.Filtering.Decode(value, filters[0] is "/A85" ? "/ASCII85Decode" : filters[0] is "/AHx" ? "/ASCIIHexDecode" : filters[0]);
                filters.RemoveAt(0);
                if (parms.Count > 0) parms.RemoveAt(0);
                touched = true;
            }

            if (filters.Count == 0 && value.Length > 64)
            {
                byte[] flated = PdfImageScaler.Deflate(value);
                if (flated.Length < value.Length) { value = flated; filters.Add("/FlateDecode"); parms.Add(null); touched = true; }
            }
            else if (filters.Count == 1 && filters[0] is "/FlateDecode" or "/Fl" && !touched)
            {
                try
                {
                    byte[] again = PdfImageScaler.Deflate(PdfImageScaler.Inflate(value, MaxStreamLength));
                    if (again.Length < value.Length) { value = again; touched = true; }
                }
                catch (InvalidDataException) { }
            }

            if (!touched) continue;
            dict.Stream.Value = value;
            WriteFilters(dict, filters, parms);
            changed++;
        }
        return changed;
    }

    private static List<string>? Filters(PdfDictionary dict, out List<PdfItem?> parms)
    {
        parms = new List<PdfItem?>();
        var filter = PdfImageScaler.Resolve(dict.Elements["/Filter"]);
        var decode = PdfImageScaler.Resolve(dict.Elements["/DecodeParms"]);
        if (filter is null) return new List<string>();
        if (filter is PdfName one)
        {
            parms.Add(decode);
            return new List<string> { one.Value };
        }
        if (filter is not PdfArray many) return null;
        var names = new List<string>();
        for (int i = 0; i < many.Elements.Count; i++)
        {
            if (PdfImageScaler.Resolve(many.Elements[i]) is not PdfName name) return null;
            names.Add(name.Value);
            parms.Add(decode is PdfArray pa && i < pa.Elements.Count ? PdfImageScaler.Resolve(pa.Elements[i]) : null);
        }
        return names;
    }

    private static void WriteFilters(PdfDictionary dict, List<string> filters, List<PdfItem?> parms)
    {
        dict.Elements.Remove("/Filter");
        dict.Elements.Remove("/DecodeParms");
        if (filters.Count == 0) return;
        if (filters.Count == 1)
        {
            dict.Elements["/Filter"] = new PdfName(filters[0]);
            if (parms.Count > 0 && parms[0] is { } p && p is not PdfNull) dict.Elements["/DecodeParms"] = p;
            return;
        }
        var names = new PdfArray();
        foreach (var f in filters) names.Elements.Add(new PdfName(f));
        dict.Elements["/Filter"] = names;
        if (parms.Any(p => p is not null and not PdfNull))
        {
            var list = new PdfArray();
            foreach (var p in parms) list.Elements.Add(p ?? PdfNull.Value);
            dict.Elements["/DecodeParms"] = list;
        }
    }

    /// <summary>Stores identical streams (same dictionary, same bytes) once and points every reference at that one.</summary>
    private static int MergeDuplicates(PdfDocument doc, CancellationToken cancel)
    {
        var canonical = new Dictionary<string, PdfReference>(StringComparer.Ordinal);
        var replace = new Dictionary<PdfReference, PdfReference>(ReferenceEqualityComparer.Instance);
        foreach (var dict in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            cancel.ThrowIfCancellationRequested();
            if (dict.Stream is null || dict.Reference is null || dict.Elements.GetName("/Type") == "/Metadata") continue;
            string key = StreamKey(dict);
            if (canonical.TryGetValue(key, out var first)) replace[dict.Reference] = first;
            else canonical[key] = dict.Reference;
        }
        if (replace.Count == 0) return 0;
        var seen = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        foreach (var obj in doc.Internals.GetAllObjects()) Redirect(obj, replace, seen);
        Redirect(doc.Internals.Catalog, replace, seen);
        return replace.Count;
    }

    private static string StreamKey(PdfDictionary dict)
    {
        var sb = new StringBuilder();
        foreach (var key in dict.Elements.Keys.Where(k => k != "/Length").OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(key).Append('=').Append(dict.Elements[key]?.ToString()).Append(';');
        byte[] hash = SHA256.HashData(dict.Stream.Value);
        return sb.Append(Convert.ToHexString(hash)).Append(':').Append(dict.Stream.Value.Length).ToString();
    }

    private static void Redirect(PdfItem? item, Dictionary<PdfReference, PdfReference> replace, HashSet<PdfItem> seen)
    {
        if (item is null || !seen.Add(item)) return;
        if (item is PdfDictionary dict)
        {
            foreach (var key in dict.Elements.Keys.ToList())
            {
                var value = dict.Elements[key];
                if (value is PdfReference r && replace.TryGetValue(r, out var to)) dict.Elements[key] = to;
                else if (value is PdfDictionary or PdfArray) Redirect(value, replace, seen);
            }
        }
        else if (item is PdfArray array)
        {
            for (int i = 0; i < array.Elements.Count; i++)
            {
                var value = array.Elements[i];
                if (value is PdfReference r && replace.TryGetValue(r, out var to)) array.Elements[i] = to;
                else if (value is PdfDictionary or PdfArray) Redirect(value, replace, seen);
            }
        }
    }

    private static void DropExtras(PdfDocument doc)
    {
        doc.Internals.Catalog.Elements.Remove("/PieceInfo");
        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
        }
    }

    /// <summary>
    /// A PDFsharp save gives every page a transparency group and the catalog a newly generated XMP stream. Neither is
    /// part of the document: the bytes are overwritten with spaces of the same length (every xref offset stays right),
    /// so pages without a group keep none and the catalog points at the XMP the document had, or at none.
    /// </summary>
    private static byte[] UndoSaveAdditions(byte[] saved, PdfDocument doc, IReadOnlyList<PdfPage> withoutGroup, PdfDictionary? originalMetadata)
    {
        byte[] bytes = (byte[])saved.Clone();
        string text = Encoding.Latin1.GetString(bytes);
        foreach (var page in withoutGroup)
        {
            if (page.Reference is null || ObjectSpan(text, page.Reference.ObjectNumber) is not { } span) continue;
            var group = System.Text.RegularExpressions.Regex.Match(text[span.Start..span.End], @"/Group\s*<<\s*/CS\s*/Device(RGB|CMYK|Gray)\s*/S\s*/Transparency\s*>>");
            if (group.Success) Blank(bytes, span.Start + group.Index, group.Length);
        }

        var generated = doc.Internals.Catalog.Elements.GetReference("/Metadata");
        if (generated is not null && !ReferenceEquals(generated.Value, originalMetadata)
            && doc.Internals.Catalog.Reference is { } catalog && ObjectSpan(text, catalog.ObjectNumber) is { } cat)
        {
            int cs = cat.Start;
            var entry = System.Text.RegularExpressions.Regex.Match(text[cat.Start..cat.End], $@"/Metadata\s*{generated.ObjectNumber}\s+{generated.GenerationNumber}\s+R");
            if (entry.Success)
            {
                string replacement = originalMetadata?.Reference is { } original ? $"/Metadata {original.ObjectNumber} {original.GenerationNumber} R" : "";
                if (replacement.Length <= entry.Length)
                {
                    Blank(bytes, cs + entry.Index, entry.Length);
                    Encoding.Latin1.GetBytes(replacement, 0, replacement.Length, bytes, cs + entry.Index);
                }
            }
        }
        return bytes;
    }

    private static (int Start, int End)? ObjectSpan(string text, int objectNumber)
    {
        var header = System.Text.RegularExpressions.Regex.Match(text, $@"(?<=^|[\r\n]){objectNumber}\s+0\s+obj\b", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!header.Success) return null;
        int end = text.IndexOf("endobj", header.Index, StringComparison.Ordinal);
        return end < 0 ? null : (header.Index, end);
    }

    private static void Blank(byte[] bytes, int start, int length)
    {
        for (int i = start; i < start + length; i++) bytes[i] = (byte)' ';
    }

    private static bool SameContent(byte[] result, int pages, IReadOnlyList<byte[]> contents)
    {
        using var check = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.Import);
        if (check.PageCount != pages) return false;
        for (int i = 0; i < pages; i++)
            if (!PdfImageScaler.PageContent(check.Pages[i]).AsSpan().SequenceEqual(contents[i])) return false;
        return true;
    }

    private static bool Contains(byte[] data, string token) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token)) >= 0;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
