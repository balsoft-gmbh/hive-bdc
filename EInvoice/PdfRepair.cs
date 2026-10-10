using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf.IO;

namespace Balsoft.Hive.BDC.EInvoice;

/// <summary>
/// Repairs carrier PDFs that PdfSharp refuses to open.
///
/// AX 2009's report writer (/Creator (Axapta)) emits the document-info object
/// without its closing <c>endobj</c>, so the next object header follows straight
/// after the dictionary. Acrobat and most viewers tolerate it; PdfSharp is a
/// strict parser and throws "Token 'N' was not expected", which fails the whole
/// ZUGFeRD embedding for every invoice printed by that AX.
///
/// Inserting the missing marker shifts every later byte offset, so the classic
/// xref table is rebuilt from the repaired positions. Only classic tables are
/// handled (AX writes PDF 1.3); a file using a cross-reference stream is left
/// alone.
/// </summary>
static class PdfRepair
{
    static readonly Regex ObjectHeader =
        new(@"^(\d+) (\d+) obj", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Writes a repaired copy of <paramref name="sourcePath"/> to a temp file and
    /// reports what was fixed. Returns false (leaving <paramref name="repairedPath"/>
    /// empty) when nothing needed repairing, the layout is not one this can fix, or
    /// the result still does not parse. Caller owns the temp file.
    /// </summary>
    public static bool TryRepair(string sourcePath, out string repairedPath, out string summary)
    {
        repairedPath = "";
        summary = "";
        string text;
        try { text = Encoding.Latin1.GetString(File.ReadAllBytes(sourcePath)); }
        catch { return false; }

        // Latin1 keeps one char per byte, so string indexes are byte offsets.
        var headers = ObjectHeader.Matches(text);
        int xrefPos = text.LastIndexOf("\nxref", StringComparison.Ordinal);
        if (headers.Count == 0 || xrefPos < 0) return false;

        var missing = new List<(int At, string Obj)>();
        for (int i = 0; i < headers.Count; i++)
        {
            int start = headers[i].Index;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : xrefPos;
            if (text.IndexOf("endobj", start, end - start, StringComparison.Ordinal) < 0)
                missing.Add((end, headers[i].Groups[1].Value));
        }
        if (missing.Count == 0) return false;

        var sb = new StringBuilder(text.Length + missing.Count * 8);
        int prev = 0;
        foreach (var (at, _) in missing)
        {
            sb.Append(text, prev, at - prev).Append("endobj\n");
            prev = at;
        }
        sb.Append(text, prev, text.Length - prev);
        string fixedText = sb.ToString();

        string? rebuilt = RebuildXref(fixedText);
        if (rebuilt is null) return false;

        string temp = Path.Combine(Path.GetTempPath(),
            $"hive-bdc-repair-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(temp, Encoding.Latin1.GetBytes(rebuilt));
            using (var probe = PdfReader.Open(temp, PdfDocumentOpenMode.Import)) { }
        }
        catch
        {
            TryDelete(temp);
            return false;
        }

        repairedPath = temp;
        summary = "missing endobj for object " + string.Join(", ", missing.Select(m => m.Obj));
        return true;
    }

    /// <summary>
    /// Replaces the trailing xref table with one built from the current object
    /// offsets, keeping the original trailer dictionary. Null when the tail does
    /// not have the classic xref/trailer/startxref shape.
    /// </summary>
    static string? RebuildXref(string text)
    {
        int xrefPos = text.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        if (xrefPos <= 0) return null;
        int trailerPos = text.IndexOf("trailer", xrefPos, StringComparison.Ordinal);
        if (trailerPos < 0) return null;
        int startxrefPos = text.IndexOf("startxref", trailerPos, StringComparison.Ordinal);
        if (startxrefPos < 0) return null;

        string body = text[..xrefPos];
        var offsets = new Dictionary<int, int>();
        foreach (Match m in ObjectHeader.Matches(body))
            offsets[int.Parse(m.Groups[1].Value)] = m.Index;
        if (offsets.Count == 0) return null;

        int size = offsets.Keys.Max() + 1;
        var table = new StringBuilder();
        table.Append("xref\n0 ").Append(size).Append('\n');
        table.Append("0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
            table.Append(offsets.TryGetValue(n, out int off)
                ? $"{off:D10} 00000 n \n"
                : "0000000000 65535 f \n");

        string trailer = text[trailerPos..startxrefPos].TrimEnd();
        return body + table + trailer + $"\nstartxref\n{xrefPos}\n%%EOF\n";
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
