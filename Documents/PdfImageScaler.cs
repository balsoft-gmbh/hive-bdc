using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Scales down lossless (Flate) images that are placed at a higher resolution than the page
/// needs, and re-encodes every lossless image with PNG predictors when that is smaller.
/// The effective resolution comes from the transformation matrix of each placement on the
/// pages and in the form XObjects they draw; the largest placement decides. Following
/// Ghostscript's pdfwrite, an image is scaled only above 1.5 x the target resolution, by area
/// averaging (no ringing). JPEG, JPEG 2000, JBIG2 and CCITT images keep their bytes, as do
/// 1-bit, indexed, CMYK and 16-bit images, images with a Decode array, a colour key mask, an
/// explicit mask or a soft mask with Matte, and images also drawn from a pattern, an
/// annotation or a Type3 glyph, whose size on the page is not known.
/// </summary>
public static class PdfImageScaler
{
    public sealed record Result(int Scaled, int Reencoded);

    private sealed class Placement
    {
        public double MinPpi = double.MaxValue;
        public bool Unknown;
    }

    public static Result Run(PdfDocument doc, int targetPpi, double threshold, CancellationToken cancel)
    {
        var placements = Placements(doc, out bool readable);
        var softMasks = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var obj in doc.Internals.GetAllObjects())
            if (obj is PdfDictionary d && d.Elements.GetReference("/SMask")?.Value is { } m) softMasks.Add(m);
        int scaled = 0, reencoded = 0;
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            cancel.ThrowIfCancellationRequested();
            if (obj is not PdfDictionary image || image.Stream is null || image.Elements.GetName("/Subtype") != "/Image") continue;
            if (softMasks.Contains(image)) continue;
            if (Pixels.Read(image) is not { } pixels) continue;

            Pixels? mask = null;
            if (image.Elements.GetReference("/SMask")?.Value is PdfDictionary smask)
            {
                if (smask.Elements.ContainsKey("/Matte") || Pixels.Read(smask) is not { Components: 1 } m) continue;
                mask = m;
            }

            double factor = 1;
            if (readable && placements.TryGetValue(image, out var place) && !place.Unknown && place.MinPpi < double.MaxValue
                && place.MinPpi > threshold * targetPpi)
                factor = targetPpi / place.MinPpi;

            if (factor < 1)
            {
                int width = Math.Max(1, (int)Math.Round(pixels.Width * factor));
                int height = Math.Max(1, (int)Math.Round(pixels.Height * factor));
                var small = pixels.Resize(width, height);
                Pixels? smallMask = mask?.Resize(Math.Max(1, (int)Math.Round(mask.Width * factor)), Math.Max(1, (int)Math.Round(mask.Height * factor)));
                byte[] encoded = small.Encode();
                byte[]? encodedMask = smallMask?.Encode();
                long before = image.Stream.Value.Length + (mask is null ? 0 : ((PdfDictionary)image.Elements.GetReference("/SMask")!.Value).Stream.Value.Length);
                long after = encoded.Length + (encodedMask?.Length ?? 0);
                if (after < before)
                {
                    small.WriteTo(image, encoded);
                    if (smallMask is not null) smallMask.WriteTo((PdfDictionary)image.Elements.GetReference("/SMask")!.Value, encodedMask!);
                    scaled++;
                    continue;
                }
            }

            byte[] same = pixels.Encode();
            if (same.Length < image.Stream.Value.Length)
            {
                pixels.WriteTo(image, same);
                reencoded++;
            }
        }
        return new Result(scaled, reencoded);
    }

    // ── where images are drawn ────────────────────────────────────────────────

    private static Dictionary<PdfDictionary, Placement> Placements(PdfDocument doc, out bool readable)
    {
        var result = new Dictionary<PdfDictionary, Placement>(ReferenceEqualityComparer.Instance);
        var walked = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        readable = true;
        try
        {
            foreach (var page in doc.Pages)
            {
                var resources = PageResources(page);
                if (resources is not null) walked.Add(resources);
                Walk(PageContent(page), resources, Matrix.Identity, result, walked, 0);
            }
        }
        catch (Exception ex) when (ex is ContentReaderException or InvalidOperationException or FormatException or ArgumentException or IndexOutOfRangeException)
        {
            readable = false;
            return result;
        }

        // Images reachable from resources no page walk reached (patterns, annotation appearances,
        // Type3 glyphs, unused forms) may be drawn at any size: keep them as they are.
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dict || Resolve(dict.Elements["/Resources"]) is not PdfDictionary res || walked.Contains(res)) continue;
            foreach (var xo in Values(res.Elements.GetDictionary("/XObject")))
                if (xo.Elements.GetName("/Subtype") == "/Image") Get(result, xo).Unknown = true;
        }
        return result;
    }

    private static void Walk(byte[] content, PdfDictionary? resources, Matrix start, Dictionary<PdfDictionary, Placement> result,
        HashSet<PdfDictionary> walked, int depth)
    {
        if (depth > 16) throw new InvalidOperationException("Form XObjects nested deeper than 16 levels.");
        var xobjects = resources?.Elements.GetDictionary("/XObject");
        var stack = new Stack<Matrix>();
        var ctm = start;
        foreach (var item in ContentReader.ReadContent(content))
        {
            if (item is not COperator op) continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q: stack.Push(ctm); break;
                case OpCodeName.Q: if (stack.Count > 0) ctm = stack.Pop(); break;
                case OpCodeName.cm:
                    if (op.Operands.Count == 6) ctm = Matrix.From(op.Operands).Times(ctm);
                    break;
                case OpCodeName.Do:
                    if (op.Operands.Count != 1 || op.Operands[0] is not CName name || xobjects is null) break;
                    if (Resolve(xobjects.Elements[name.Name]) is not PdfDictionary xo) break;
                    if (xo.Elements.GetName("/Subtype") == "/Image")
                    {
                        var p = Get(result, xo);
                        double widthPt = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), heightPt = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
                        int w = xo.Elements.GetInteger("/Width"), h = xo.Elements.GetInteger("/Height");
                        if (widthPt < 1e-6 || heightPt < 1e-6) break;
                        p.MinPpi = Math.Min(p.MinPpi, Math.Min(w / (widthPt / 72), h / (heightPt / 72)));
                    }
                    else if (xo.Elements.GetName("/Subtype") == "/Form" && xo.Stream is not null)
                    {
                        var own = Resolve(xo.Elements["/Resources"]) as PdfDictionary ?? resources;
                        if (own is not null) walked.Add(own);
                        var matrix = xo.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m ? Matrix.From(m) : Matrix.Identity;
                        Walk(xo.Stream.UnfilteredValue, own, matrix.Times(ctm), result, walked, depth + 1);
                    }
                    break;
            }
        }
    }

    private static Placement Get(Dictionary<PdfDictionary, Placement> result, PdfDictionary image)
    {
        if (!result.TryGetValue(image, out var p)) result[image] = p = new Placement();
        return p;
    }

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        public Matrix Times(Matrix o) => new(
            A * o.A + B * o.C, A * o.B + B * o.D,
            C * o.A + D * o.C, C * o.B + D * o.D,
            E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);

        public static Matrix From(CSequence operands)
        {
            double V(int i) => operands[i] switch { CInteger n => n.Value, CReal r => r.Value, _ => throw new FormatException("Matrix operand is no number.") };
            return new(V(0), V(1), V(2), V(3), V(4), V(5));
        }

        public static Matrix From(PdfArray array)
        {
            double V(int i) => array.Elements[i] switch { PdfInteger n => n.Value, PdfReal r => r.Value, _ => throw new FormatException("Matrix entry is no number.") };
            return new(V(0), V(1), V(2), V(3), V(4), V(5));
        }
    }

    internal static PdfDictionary? PageResources(PdfPage page)
    {
        PdfDictionary? node = page;
        for (int i = 0; node is not null && i < 32; i++)
        {
            if (Resolve(node.Elements["/Resources"]) is PdfDictionary r) return r;
            node = Resolve(node.Elements["/Parent"]) as PdfDictionary;
        }
        return null;
    }

    internal static byte[] PageContent(PdfPage page)
    {
        var contents = Resolve(page.Elements["/Contents"]);
        using var all = new MemoryStream();
        IEnumerable<PdfItem> parts = contents is PdfArray a ? a.Elements : contents is null ? Array.Empty<PdfItem>() : new[] { contents };
        foreach (var part in parts)
        {
            if (Resolve(part) is not PdfDictionary d || d.Stream is null) continue;
            all.Write(d.Stream.UnfilteredValue);
            all.WriteByte((byte)'\n');
        }
        return all.ToArray();
    }

    private static IEnumerable<PdfDictionary> Values(PdfDictionary? dict)
    {
        if (dict is null) yield break;
        foreach (var key in dict.Elements.Keys.ToList())
            if (Resolve(dict.Elements[key]) is PdfDictionary d) yield return d;
    }

    internal static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    // ── pixels ────────────────────────────────────────────────────────────────

    /// <summary>The decoded samples of an 8-bit gray or RGB Flate image.</summary>
    internal sealed class Pixels
    {
        private Pixels(int width, int height, int components, byte[] data)
        {
            Width = width; Height = height; Components = components; Data = data;
        }

        public int Width { get; }
        public int Height { get; }
        public int Components { get; }
        public byte[] Data { get; }

        public static Pixels? Read(PdfDictionary image)
        {
            var e = image.Elements;
            if (e.GetBoolean("/ImageMask") || e.ContainsKey("/Decode") || e.ContainsKey("/Alternates") || e.GetInteger("/BitsPerComponent") != 8) return null;
            if (Resolve(e["/Mask"]) is not null) return null;
            int components = ComponentCount(Resolve(e["/ColorSpace"]));
            if (components == 0) return null;
            var filter = Resolve(e["/Filter"]);
            string filterName = filter is PdfName n ? n.Value : filter is PdfArray { Elements.Count: 1 } fa && Resolve(fa.Elements[0]) is PdfName one ? one.Value : filter is null ? "" : "?";
            if (filterName is not ("/FlateDecode" or "/Fl" or "")) return null;
            int width = e.GetInteger("/Width"), height = e.GetInteger("/Height");
            // 40 million samples is a 3,650 x 3,650 RGB image; larger ones stay as they are (memory per document).
            long samples = (long)width * height * components;
            if (width <= 0 || height <= 0 || samples > 40_000_000) return null;

            byte[] raw = image.Stream.Value;
            byte[] data;
            try { data = filterName.Length == 0 ? raw : Inflate(raw, samples + height + 1024); }
            catch (InvalidDataException) { return null; }

            var parms = Resolve(e["/DecodeParms"]) is PdfArray pa ? Resolve(pa.Elements.Count > 0 ? pa.Elements[0] : null) as PdfDictionary : Resolve(e["/DecodeParms"]) as PdfDictionary;
            int predictor = parms?.Elements.GetInteger("/Predictor") ?? 0;
            if (predictor > 1)
            {
                if (filterName.Length == 0) return null;
                int colors = parms!.Elements.ContainsKey("/Colors") ? parms.Elements.GetInteger("/Colors") : 1;
                int bpc = parms.Elements.ContainsKey("/BitsPerComponent") ? parms.Elements.GetInteger("/BitsPerComponent") : 8;
                int columns = parms.Elements.ContainsKey("/Columns") ? parms.Elements.GetInteger("/Columns") : 1;
                if (colors != components || bpc != 8 || columns != width) return null;
                data = predictor == 2 ? UndoTiff(data, width, height, components) : predictor >= 10 ? UndoPng(data, width, height, components) : null!;
                if (data is null) return null;
            }
            if (data.Length < width * height * components) return null;
            if (data.Length > width * height * components) Array.Resize(ref data, width * height * components);
            return new Pixels(width, height, components, data);
        }

        private static int ComponentCount(PdfItem? colorSpace)
        {
            if (colorSpace is PdfName n) return n.Value switch { "/DeviceGray" or "/G" => 1, "/DeviceRGB" or "/RGB" => 3, _ => 0 };
            if (colorSpace is PdfArray { Elements.Count: 2 } a && Resolve(a.Elements[0]) is PdfName { Value: "/ICCBased" }
                && Resolve(a.Elements[1]) is PdfDictionary icc)
            {
                int count = icc.Elements.GetInteger("/N");
                return count is 1 or 3 ? count : 0;
            }
            return 0;
        }

        /// <summary>Area averaging: every output pixel is the mean of the source area it covers.</summary>
        public Pixels Resize(int width, int height)
        {
            int c = Components;
            var rows = new double[Height * width * c];
            double sx = (double)Width / width;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double from = x * sx, to = from + sx;
                    for (int k = 0; k < c; k++)
                    {
                        double sum = 0;
                        for (int s = (int)from; s < Math.Min(Width, (int)Math.Ceiling(to)); s++)
                        {
                            double cover = Math.Min(to, s + 1) - Math.Max(from, s);
                            if (cover > 0) sum += cover * Data[(y * Width + s) * c + k];
                        }
                        rows[(y * width + x) * c + k] = sum / sx;
                    }
                }
            }
            var output = new byte[width * height * c];
            double sy = (double)Height / height;
            for (int y = 0; y < height; y++)
            {
                double from = y * sy, to = from + sy;
                for (int x = 0; x < width; x++)
                {
                    for (int k = 0; k < c; k++)
                    {
                        double sum = 0;
                        for (int s = (int)from; s < Math.Min(Height, (int)Math.Ceiling(to)); s++)
                        {
                            double cover = Math.Min(to, s + 1) - Math.Max(from, s);
                            if (cover > 0) sum += cover * rows[(s * width + x) * c + k];
                        }
                        output[(y * width + x) * c + k] = (byte)Math.Clamp((int)Math.Round(sum / sy), 0, 255);
                    }
                }
            }
            return new Pixels(width, height, c, output);
        }

        /// <summary>Flate with PNG predictors, the filter per row picked by the smallest sum of absolute differences.</summary>
        public byte[] Encode()
        {
            int stride = Width * Components, bpp = Components;
            var filtered = new byte[(stride + 1) * Height];
            var candidate = new byte[stride];
            var best = new byte[stride];
            for (int y = 0; y < Height; y++)
            {
                int row = y * stride, prev = (y - 1) * stride;
                long bestScore = long.MaxValue;
                byte bestType = 0;
                for (byte type = 0; type <= 4; type++)
                {
                    long score = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? Data[row + i - bpp] : 0;
                        int b = y > 0 ? Data[prev + i] : 0;
                        int cc = y > 0 && i >= bpp ? Data[prev + i - bpp] : 0;
                        int predicted = type switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, cc) };
                        byte v = (byte)(Data[row + i] - predicted);
                        candidate[i] = v;
                        score += v < 128 ? v : 256 - v;
                    }
                    if (score < bestScore) { bestScore = score; bestType = type; Buffer.BlockCopy(candidate, 0, best, 0, stride); }
                }
                filtered[y * (stride + 1)] = bestType;
                Buffer.BlockCopy(best, 0, filtered, y * (stride + 1) + 1, stride);
            }
            using var output = new MemoryStream();
            using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(filtered);
            return output.ToArray();
        }

        public void WriteTo(PdfDictionary image, byte[] encoded)
        {
            image.Stream.Value = encoded;
            image.Elements["/Width"] = new PdfInteger(Width);
            image.Elements["/Height"] = new PdfInteger(Height);
            image.Elements["/Filter"] = new PdfName("/FlateDecode");
            var parms = new PdfDictionary();
            parms.Elements["/Predictor"] = new PdfInteger(15);
            parms.Elements["/Colors"] = new PdfInteger(Components);
            parms.Elements["/BitsPerComponent"] = new PdfInteger(8);
            parms.Elements["/Columns"] = new PdfInteger(Width);
            image.Elements["/DecodeParms"] = parms;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static byte[]? UndoPng(byte[] data, int width, int height, int bpp)
        {
            int stride = width * bpp;
            if (data.Length < (stride + 1) * height) return null;
            var output = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int type = data[y * (stride + 1)], src = y * (stride + 1) + 1, row = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? output[row + i - bpp] : 0;
                    int b = y > 0 ? output[row - stride + i] : 0;
                    int c = y > 0 && i >= bpp ? output[row - stride + i - bpp] : 0;
                    int predicted = type switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => -1 };
                    if (predicted < 0) return null;
                    output[row + i] = (byte)(data[src + i] + predicted);
                }
            }
            return output;
        }

        private static byte[]? UndoTiff(byte[] data, int width, int height, int bpp)
        {
            int stride = width * bpp;
            if (data.Length < stride * height) return null;
            var output = (byte[])data.Clone();
            for (int y = 0; y < height; y++)
                for (int i = bpp; i < stride; i++)
                    output[y * stride + i] = (byte)(output[y * stride + i] + output[y * stride + i - bpp]);
            return output;
        }
    }

    /// <summary>Flate-decoded bytes; a stream that inflates beyond <paramref name="maxLength"/> is refused (a zip bomb).</summary>
    internal static byte[] Inflate(byte[] data, long maxLength)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxLength) throw new InvalidDataException($"The stream inflates beyond {maxLength:N0} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }
}
