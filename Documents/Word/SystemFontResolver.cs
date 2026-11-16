using System.Buffers.Binary;
using System.IO;
using System.Text;
using PdfSharp.Fonts;

namespace Balsoft.Hive.BDC.Documents.Word;

/// <summary>
/// PDFsharp font resolver over the machine's installed TrueType fonts (Windows, macOS, Linux folders). Each file is
/// indexed by the family and style its own 'name' and 'head' tables declare, so "Arial" bold finds arialbd.ttf on
/// Windows and "Arial Bold.ttf" on macOS. A family that is not installed falls back to Arial (then to any font) and
/// is recorded in <see cref="MissingFamilies"/>.
/// </summary>
public sealed class SystemFontResolver : IFontResolver
{
    private static readonly object Gate = new();
    private static SystemFontResolver? _installed;

    // family (lower case) -> style bits (1 = bold, 2 = italic) -> file
    private readonly Dictionary<string, Dictionary<int, string>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public const string FallbackFamily = "Arial";

    /// <summary>The folders searched, in order.</summary>
    public IReadOnlyList<string> Folders { get; }

    public int FontCount => _index.Values.Sum(v => v.Count);

    /// <summary>Families a document asked for that are not installed (rendered with the fallback).</summary>
    public IReadOnlyCollection<string> MissingFamilies { get { lock (_missing) return _missing.ToArray(); } }

    private SystemFontResolver(IReadOnlyList<string> folders)
    {
        Folders = folders;
        foreach (var dir in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                if (!TryReadNames(file, out var family, out int style)) continue;
                if (!_index.TryGetValue(family, out var styles)) _index[family] = styles = new Dictionary<int, string>();
                styles.TryAdd(style, file);
            }
        }
    }

    /// <summary>Registers the resolver with PDFsharp once per process and returns it.</summary>
    public static SystemFontResolver Install()
    {
        lock (Gate)
        {
            if (_installed is not null) return _installed;
            var resolver = new SystemFontResolver(DefaultFolders());
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = resolver;
            return _installed = resolver;
        }
    }

    private static List<string> DefaultFolders()
    {
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/System/Library/Fonts/Supplemental");
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/Library/Fonts");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts"));
        }
        else
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
        }
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToList();
    }

    public bool Has(string family) => _index.ContainsKey(family);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        string family = familyName;
        if (!_index.ContainsKey(family))
        {
            lock (_missing) _missing.Add(familyName);
            family = _index.ContainsKey(FallbackFamily) ? FallbackFamily : _index.Keys.FirstOrDefault() ?? "";
            if (family.Length == 0) return null;
        }
        var styles = _index[family];
        int want = (bold ? 1 : 0) | (italic ? 2 : 0);
        if (styles.TryGetValue(want, out var exact)) return new FontResolverInfo(exact);
        // Simulate what the family does not have, starting from the closest face.
        int[] order = want switch { 3 => new[] { 1, 2, 0 }, 1 => new[] { 0 }, 2 => new[] { 0 }, _ => new[] { 1, 2, 3 } };
        foreach (int s in order)
            if (styles.TryGetValue(s, out var file))
                return new FontResolverInfo(file, bold && (s & 1) == 0, italic && (s & 2) == 0);
        return new FontResolverInfo(styles.Values.First(), bold, italic);
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_bytes)
        {
            if (!_bytes.TryGetValue(faceName, out var data))
                _bytes[faceName] = data = File.ReadAllBytes(faceName);
            return data;
        }
    }

    /// <summary>Family (name id 1) and style (head.macStyle bold/italic) of a TrueType file; false for anything else.</summary>
    internal static bool TryReadNames(string file, out string family, out int style)
    {
        family = "";
        style = 0;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] head = ReadAt(fs, 0, 12);
            uint version = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (version != 0x00010000 && version != 0x74727565) return false;   // TrueType outlines only
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
            byte[] dir = ReadAt(fs, 12, numTables * 16);
            int nameOffset = -1, nameLength = 0, headOffset = -1;
            for (int i = 0; i < numTables; i++)
            {
                string tag = Encoding.ASCII.GetString(dir, i * 16, 4);
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(i * 16 + 8));
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(i * 16 + 12));
                if (tag == "name") { nameOffset = offset; nameLength = length; }
                else if (tag == "head") headOffset = offset;
            }
            if (nameOffset < 0 || headOffset < 0 || nameLength < 6) return false;
            byte[] headTable = ReadAt(fs, headOffset, 46);
            int macStyle = BinaryPrimitives.ReadUInt16BigEndian(headTable.AsSpan(44));
            style = ((macStyle & 1) != 0 ? 1 : 0) | ((macStyle & 2) != 0 ? 2 : 0);

            byte[] name = ReadAt(fs, nameOffset, nameLength);
            int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
            int strings = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
            string? windows = null, mac = null;
            for (int i = 0; i < count && 6 + i * 12 + 12 <= name.Length; i++)
            {
                int r = 6 + i * 12;
                int platform = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(r));
                int encoding = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(r + 2));
                int language = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(r + 4));
                int nameId = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(r + 6));
                int length = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(r + 8));
                int offset = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(r + 10));
                if (nameId != 1 || strings + offset + length > name.Length) continue;
                if (platform == 3 && (encoding == 1 || encoding == 0) && (windows is null || language == 0x409))
                    windows = Encoding.BigEndianUnicode.GetString(name, strings + offset, length);
                else if (platform == 1 && encoding == 0 && mac is null)
                    mac = Encoding.Latin1.GetString(name, strings + offset, length);
            }
            family = (windows ?? mac ?? "").Trim();
            return family.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] ReadAt(FileStream fs, long offset, int length)
    {
        if (length < 0 || offset + length > fs.Length) throw new EndOfStreamException();
        var buffer = new byte[length];
        fs.Position = offset;
        fs.ReadExactly(buffer);
        return buffer;
    }
}