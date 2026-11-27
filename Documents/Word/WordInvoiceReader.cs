using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace Balsoft.Hive.BDC.Documents.Word;

/// <summary>One content control as read from the document.</summary>
/// <param name="Tag">The control's Tag (a BT number such as "BT-44", or a JSON key such as "eInvoiceType").</param>
/// <param name="Title">The control's title as the template author named it.</param>
/// <param name="Text">The visible text (placeholder text included).</param>
/// <param name="IsPlaceholder">True while the control still shows its placeholder; its value counts as empty.</param>
/// <param name="ListValue">The value behind the selected drop-down entry, when the control is a drop-down.</param>
/// <param name="IsoDate">The date a date picker stores (yyyy-MM-dd), when the control is a date picker.</param>
public sealed record WordField(string Tag, string Title, string Text, bool IsPlaceholder, string? ListValue, string? IsoDate)
{
    /// <summary>What the field contributes: the drop-down value, else the visible text; empty for a placeholder.</summary>
    public string Value => IsPlaceholder ? "" : (ListValue ?? Text).Trim();
}

/// <summary>Totals computed from the lines next to the totals printed in the document (null = not printed).</summary>
public sealed record WordTotals(decimal Net, decimal Tax, decimal Gross, decimal? PrintedNet, decimal? PrintedTax, decimal? PrintedGross);

/// <summary>What <see cref="WordInvoiceReader"/> found in a .docx.</summary>
public sealed class WordInvoice
{
    /// <summary>The document as the JSON an ERP export writes (same keys), ready for the e-invoice pipeline.</summary>
    public JsonObject Json { get; } = new();

    public List<WordField> Fields { get; } = new();
    public List<List<WordField>> Lines { get; } = new();

    /// <summary>Reasons the document cannot be turned into an e-invoice. Empty = ready.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Things worth knowing that do not stop the document.</summary>
    public List<string> Notes { get; } = new();

    public WordTotals? Totals { get; set; }

    /// <summary>"zugferd" (also for zugferd-xrechnung) or "xrechnung" (the default when the document names none).</summary>
    public string Format { get; set; } = "xrechnung";

    /// <summary>True when the XRechnung rules apply: the XML output and a ZUGFeRD-XRechnung PDF.</summary>
    public bool XRechnungRules => Format == "xrechnung"
        || ((Json[WordInvoiceReader.FormatTag] as JsonValue)?.GetValue<string>() ?? "").Contains("xrechnung", StringComparison.OrdinalIgnoreCase);

    /// <summary>The output as the history names it: XRechnung, ZUGFeRD (EN 16931) or ZUGFeRD (XRechnung).</summary>
    public string FormatName => Format != "zugferd" ? "XRechnung" : XRechnungRules ? "ZUGFeRD (XRechnung)" : "ZUGFeRD (EN 16931)";

    public bool HasFields => Fields.Count > 0 || Lines.Count > 0;
}

/// <summary>
/// Reads a filled Hive BDC Word invoice: every value sits in a content control whose Tag is its EN 16931 BT number
/// (or a JSON key of the ERP export), the invoice lines sit in a repeating section tagged "lines". The result is the
/// same JSON the ERP export writes, so the e-invoice pipeline does not know the difference.
/// Nothing is found by position or by label text.
/// </summary>
public static class WordInvoiceReader
{
    public const string LinesTag = "lines";
    public const string FormatTag = "eInvoiceType";

    // Core BT numbers that have a fixed key in the ERP JSON; any other BT tag is passed on under its own name and
    // resolved by the e-invoice alias table.
    private static readonly Dictionary<string, string> HeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BT-1"] = "documentNumber", ["BT-2"] = "issueDate", ["BT-3"] = "invoiceTypeCode", ["BT-5"] = "currency",
        ["BT-9"] = "dueDate", ["BT-22"] = "notes",
        ["BT-27"] = "senderName", ["BT-31"] = "senderVatId", ["BT-32"] = "senderTaxId", ["BT-35"] = "senderStreet",
        ["BT-37"] = "senderCity", ["BT-38"] = "senderPostcode", ["BT-40"] = "senderCountry", ["BT-42"] = "senderPhone",
        ["BT-43"] = "senderEmail",
        ["BT-44"] = "recipientName", ["BT-48"] = "recipientVatId", ["BT-50"] = "recipientStreet",
        ["BT-52"] = "recipientCity", ["BT-53"] = "recipientPostcode", ["BT-55"] = "recipientCountry",
        ["BT-73"] = "servicePeriodFrom", ["BT-74"] = "servicePeriodTo", ["BT-84"] = "bankIban",
        ["BT-118"] = "vatCategory", ["BT-119"] = "vatRate",
        // printed totals: checked against the lines, recomputed for the XML
        ["BT-106"] = "lineTotalAmount", ["BT-109"] = "netAmount", ["BT-110"] = "taxAmount",
        ["BT-112"] = "grossAmount", ["BT-113"] = "prepaidAmount", ["BT-115"] = "amountDue",
    };

    private static readonly Dictionary<string, string> LineKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BT-126"] = "lineId", ["BT-153"] = "description", ["BT-129"] = "quantity", ["BT-130"] = "unit",
        ["BT-146"] = "unitPrice", ["BT-131"] = "lineNetAmount", ["BT-151"] = "vatCategory", ["BT-152"] = "vatRate",
    };

    private static readonly HashSet<string> DateTags = new(StringComparer.OrdinalIgnoreCase)
        { "BT-2", "BT-9", "BT-26", "BT-72", "BT-73", "BT-74" };

    private static readonly HashSet<string> AmountTags = new(StringComparer.OrdinalIgnoreCase)
        { "BT-106", "BT-109", "BT-110", "BT-112", "BT-113", "BT-115", "BT-119", "BT-129", "BT-131", "BT-146", "BT-152" };

    // Several values allowed in the document, the first is used (a letterhead often lists more than one bank).
    private static readonly HashSet<string> FirstWins = new(StringComparer.OrdinalIgnoreCase) { "BT-84", "BT-86", "BT-85" };

    public static WordInvoice Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream);
    }

    public static WordInvoice Read(Stream stream)
    {
        var result = new WordInvoice();
        using var doc = WordprocessingDocument.Open(stream, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("The file has no main document part.");

        var roots = new List<OpenXmlElement>();
        roots.AddRange(main.HeaderParts.Select(h => h.Header).OfType<OpenXmlElement>());
        if (main.Document?.Body is { } body) roots.Add(body);
        roots.AddRange(main.FooterParts.Select(f => f.Footer).OfType<OpenXmlElement>());

        foreach (var root in roots)
            Collect(root, result);

        if (!result.HasFields)
        {
            result.Problems.Add("No Hive BDC fields found: the document has no content controls tagged with BT numbers.");
            return result;
        }

        Build(result);
        return result;
    }

    private static void Collect(OpenXmlElement root, WordInvoice result)
    {
        foreach (var sdt in root.Descendants<SdtElement>())
        {
            if (sdt.Ancestors<SdtElement>().Any(a => IsRepeatingSection(a))) continue;   // read with its line
            if (IsRepeatingSection(sdt))
            {
                foreach (var item in Content(sdt)?.Elements<SdtElement>() ?? Enumerable.Empty<SdtElement>())
                {
                    var line = item.Descendants<SdtElement>().Select(ToField).OfType<WordField>().ToList();
                    if (line.Count > 0) result.Lines.Add(line);
                }
                continue;
            }
            if (ToField(sdt) is { } field) result.Fields.Add(field);
        }
    }

    private static bool IsRepeatingSection(SdtElement sdt)
        => sdt.SdtProperties?.GetFirstChild<W15.SdtRepeatedSection>() is not null;

    private static OpenXmlElement? Content(SdtElement sdt)
        => sdt.ChildElements.FirstOrDefault(e => e.LocalName == "sdtContent");

    private static WordField? ToField(SdtElement sdt)
    {
        var pr = sdt.SdtProperties;
        string tag = pr?.GetFirstChild<Tag>()?.Val?.Value?.Trim() ?? "";
        if (tag.Length == 0 || pr is null) return null;
        string title = pr.GetFirstChild<SdtAlias>()?.Val?.Value ?? tag;
        string text = VisibleText(Content(sdt));
        bool placeholder = pr.GetFirstChild<ShowingPlaceholder>() is not null;

        string? listValue = null;
        var list = (OpenXmlElement?)pr.GetFirstChild<SdtContentDropDownList>() ?? pr.GetFirstChild<SdtContentComboBox>();
        if (list is not null)
        {
            var item = list.Elements<ListItem>().FirstOrDefault(li =>
                string.Equals((li.DisplayText?.Value ?? li.Value?.Value ?? "").Trim(), text.Trim(), StringComparison.Ordinal));
            listValue = item?.Value?.Value;
        }

        string? isoDate = null;
        if (pr.GetFirstChild<SdtContentDate>()?.FullDate?.Value is { } full && !placeholder)
            isoDate = full.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new WordField(tag, title, text, placeholder, listValue, isoDate);
    }

    /// <summary>Text as Word shows it: paragraphs on their own lines, tabs and line breaks kept, field codes left out.</summary>
    internal static string VisibleText(OpenXmlElement? content)
    {
        if (content is null) return "";
        var sb = new StringBuilder();
        bool firstParagraph = true;

        void Walk(OpenXmlElement e)
        {
            switch (e)
            {
                case Paragraph:
                    if (!firstParagraph) sb.Append('\n');
                    firstParagraph = false;
                    foreach (var c in e.ChildElements) Walk(c);
                    return;
                case Text t: sb.Append(t.Text); return;
                case TabChar: sb.Append('\t'); return;
                case Break or CarriageReturn: sb.Append('\n'); return;
                case NoBreakHyphen: sb.Append('-'); return;
                case DeletedText or FieldCode or DeletedFieldCode: return;
                case SdtProperties or ParagraphProperties or RunProperties: return;
            }
            foreach (var c in e.ChildElements) Walk(c);
        }

        foreach (var c in content.ChildElements) Walk(c);
        var lines = sb.ToString().Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    // ── fields to JSON ──────────────────────────────────────────────────────

    private static void Build(WordInvoice result)
    {
        var json = result.Json;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in result.Fields)
        {
            if (f.Value.Length == 0) continue;
            string key = HeaderKeys.TryGetValue(f.Tag, out var k) ? k : f.Tag;
            if (!seen.Add(f.Tag))
            {
                if (string.Equals(FirstValue(result, f.Tag), f.Value, StringComparison.Ordinal)) continue;   // e.g. seller name in header and footer
                if (FirstWins.Contains(f.Tag))
                {
                    string note = $"{f.Tag} appears more than once; the first value ({FirstValue(result, f.Tag)}) is used.";
                    if (!result.Notes.Contains(note)) result.Notes.Add(note);
                }
                else
                    result.Problems.Add($"{f.Tag} ({f.Title}) appears more than once with a value; keep one.");
                continue;
            }
            if (Convert(f, key, result) is { } node) json[key] = node;
        }

        // BT-49 is the buyer's e-invoice address; when it is a mail address it is also where the mail goes,
        // unless the template has its own recipientEmail field.
        if (json["BT-49"]?.GetValue<string>() is { } address && address.Contains('@') && json["recipientEmail"] is null)
            json["recipientEmail"] = address;

        if (json["invoiceNumber"] is null && json["documentNumber"] is JsonNode number)
            json["invoiceNumber"] = number.GetValue<string>();

        string format = (json[FormatTag]?.GetValue<string>() ?? "").ToLowerInvariant();
        result.Format = format.Contains("zugferd") ? "zugferd" : "xrechnung";
        if (format.Length == 0)
            result.Notes.Add("No E-Rechnung format in the document; XRechnung is produced.");

        var lines = new JsonArray();
        int n = 0;
        foreach (var line in result.Lines)
        {
            n++;
            var obj = new JsonObject();
            foreach (var f in line)
            {
                if (f.Value.Length == 0) continue;
                string key = LineKeys.TryGetValue(f.Tag, out var k) ? k : f.Tag;
                if (Convert(f, key, result, $"line {n}: ") is { } node) obj[key] = node;
            }
            if (obj.Count == 0) continue;   // an empty repeated item, e.g. added and never filled
            lines.Add(obj);
        }
        json["lineItems"] = lines;

        CheckRequired(result, lines);
        CheckTotals(result, lines);
    }

    private static string FirstValue(WordInvoice result, string tag)
        => result.Fields.First(f => string.Equals(f.Tag, tag, StringComparison.OrdinalIgnoreCase) && f.Value.Length > 0).Value;

    private static JsonNode? Convert(WordField f, string key, WordInvoice result, string where = "")
    {
        if (DateTags.Contains(f.Tag))
        {
            if (f.IsoDate is { } iso) return iso;
            if (TryParseDate(f.Value, out var d)) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            result.Problems.Add($"{where}{f.Tag} ({f.Title}): '{f.Value}' is not a date (use dd.MM.yyyy or the date picker).");
            return null;
        }
        if (AmountTags.Contains(f.Tag))
        {
            if (TryParseAmount(f.Value, out var amount)) return JsonValue.Create(amount);
            result.Problems.Add($"{where}{f.Tag} ({f.Title}): '{f.Value}' is not a number.");
            return null;
        }
        if (string.Equals(f.Tag, "BT-3", StringComparison.OrdinalIgnoreCase))
        {
            string code = (f.ListValue ?? f.Value).Trim();
            if (code.Length > 0 && code.All(char.IsDigit)) return code;
            result.Problems.Add($"BT-3 ({f.Title}): '{f.Value}' is not an invoice type code; choose an entry from the list.");
            return null;
        }
        if (string.Equals(f.Tag, FormatTag, StringComparison.OrdinalIgnoreCase))
        {
            string v = f.Value.ToLowerInvariant();
            if (v.Contains("zugferd")) return v.Contains("xrechnung") ? "zugferd-xrechnung" : "zugferd";
            if (v.Contains("xrechnung")) return "xrechnung";
            result.Problems.Add($"{f.Title}: '{f.Value}' is neither XRechnung nor ZUGFeRD.");
            return null;
        }
        if (string.Equals(f.Tag, "BT-40", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Tag, "BT-55", StringComparison.OrdinalIgnoreCase))
            return f.Value.Trim().ToUpperInvariant();
        return key == "notes" || f.Tag.Equals("BT-154", StringComparison.OrdinalIgnoreCase) ? f.Value : f.Value.Replace('\n', ' ').Trim();
    }

    private static void CheckRequired(WordInvoice result, JsonArray lines)
    {
        var json = result.Json;
        var missing = new List<string>();
        void Need(string key, string label) { if (json[key] is null) missing.Add(label); }

        Need("documentNumber", "BT-1 invoice number");
        Need("issueDate", "BT-2 invoice date");
        Need("recipientName", "BT-44 buyer name");
        if (result.XRechnungRules)
        {
            Need("BT-10", "BT-10 buyer reference (Leitweg-ID or order reference)");
            if (json["BT-49"] is null && json["recipientEmail"] is null) missing.Add("BT-49 buyer e-invoice address");
        }
        if (missing.Count > 0)
            result.Problems.Add("Required field(s) empty or still showing the placeholder: " + string.Join(", ", missing) + ".");

        if (lines.Count == 0)
            result.Problems.Add("The document has no invoice line (repeating section 'lines').");
        int n = 0;
        foreach (var line in lines.OfType<JsonObject>())
        {
            n++;
            var lack = new List<string>();
            if (line["description"] is null) lack.Add("BT-153 name");
            if (line["quantity"] is null) lack.Add("BT-129 quantity");
            if (line["unitPrice"] is null) lack.Add("BT-146 net price");
            if (line["vatRate"] is null && json["vatRate"] is null) lack.Add("BT-152 VAT rate");
            if (lack.Count > 0) result.Problems.Add($"Line {n}: {string.Join(", ", lack)} missing.");
        }
    }

    /// <summary>
    /// EN 16931 arithmetic for a plain invoice: line net = quantity x net price, rounded to the cent; VAT per rate on
    /// the summed line nets. Printed totals must match to the cent, or the XML would say something else than the paper.
    /// </summary>
    private static void CheckTotals(WordInvoice result, JsonArray lines)
    {
        var json = result.Json;
        decimal headerRate = Dec(json["vatRate"]) ?? -1m;
        var byRate = new Dictionary<decimal, decimal>();
        int n = 0;
        foreach (var line in lines.OfType<JsonObject>())
        {
            n++;
            if (Dec(line["quantity"]) is not { } qty || Dec(line["unitPrice"]) is not { } price) return;
            decimal net = Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);
            if (Dec(line["lineNetAmount"]) is { } printedLine && printedLine != net)
                result.Problems.Add($"Line {n}: printed line amount {Fmt(printedLine)} differs from quantity x price = {Fmt(net)}.");
            decimal rate = Dec(line["vatRate"]) ?? headerRate;
            if (rate < 0m) return;
            byRate[rate] = byRate.GetValueOrDefault(rate) + net;
        }

        decimal netTotal = byRate.Values.Sum();
        decimal tax = byRate.Sum(kv => Math.Round(kv.Value * kv.Key / 100m, 2, MidpointRounding.AwayFromZero));
        // With BT-113 (payments already made) the amount due BT-115 is the gross minus those payments, so it no longer
        // stands in for a missing gross amount.
        decimal? prepaid = Dec(json["prepaidAmount"]);
        decimal? printedDue = Dec(json["amountDue"]);
        var totals = new WordTotals(netTotal, tax, netTotal + tax,
            Dec(json["netAmount"]) ?? Dec(json["lineTotalAmount"]), Dec(json["taxAmount"]),
            Dec(json["grossAmount"]) ?? (prepaid is null ? printedDue : null));
        result.Totals = totals;

        void Compare(string label, decimal? printed, decimal computed)
        {
            if (printed is { } p && p != computed)
                result.Problems.Add($"{label}: printed {Fmt(p)}, the lines add up to {Fmt(computed)}.");
        }
        Compare("Net amount (BT-109)", totals.PrintedNet, totals.Net);
        Compare("VAT amount (BT-110)", totals.PrintedTax, totals.Tax);
        Compare("Gross amount (BT-112)", totals.PrintedGross, totals.Gross);
        if (prepaid is { } paid && printedDue is { } due && due != totals.Gross - paid)
            result.Problems.Add($"Amount due (BT-115): printed {Fmt(due)}, gross {Fmt(totals.Gross)} minus paid amount (BT-113) "
                                + $"{Fmt(paid)} is {Fmt(totals.Gross - paid)}.");
    }

    private static decimal? Dec(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    internal static string Fmt(decimal d) => d.ToString("#,##0.00", GermanNumbers);

    private static readonly NumberFormatInfo GermanNumbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    /// <summary>
    /// German amounts as people type them: "721.613,72 EUR", "19,0 %", "-200,00", "1.000". A comma is the decimal
    /// separator; a dot is a thousands separator, except a single dot not followed by exactly three digits ("2.5").
    /// </summary>
    internal static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string raw = text;
        foreach (var unit in new[] { "EUR", "Euro", "€", "%" })
            raw = raw.Replace(unit, " ", StringComparison.OrdinalIgnoreCase);
        var s = new StringBuilder();
        foreach (char c in raw)
            if (char.IsDigit(c) || c is ',' or '.' or '-' or '+') s.Append(c);
            else if (!char.IsWhiteSpace(c)) return false;
        string t = s.ToString();
        if (t.Length == 0) return false;
        bool negative = false;
        if (t.EndsWith('-')) { negative = true; t = t[..^1]; }
        if (t.StartsWith('-')) { negative = !negative; t = t[1..]; }
        else if (t.StartsWith('+')) t = t[1..];
        if (t.Contains('-') || t.Contains('+')) return false;

        if (t.Contains(','))
        {
            if (t.Count(c => c == ',') > 1) return false;
            t = t.Replace(".", "").Replace(',', '.');
        }
        else if (t.Count(c => c == '.') > 1 || (t.IndexOf('.') is int dot and > 0 && t.Length - dot - 1 == 3))
            t = t.Replace(".", "");

        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (negative) value = -value;
        return true;
    }

    private static readonly string[] DateFormats = { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "yyyy-MM-dd" };

    internal static bool TryParseDate(string? text, out DateTime date)
        => DateTime.TryParseExact((text ?? "").Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}