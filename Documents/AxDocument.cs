using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// DTO for the JSON the ERP drops next to each document. Every type shares the
/// ENVELOPE (documentType, documentNumber, party, recipient addresses, mail subject/body,
/// environment); the invoice-specific fields below it are read only by the e-invoice
/// handler. Numeric fields may arrive as JSON numbers OR as strings (AX num2str) using
/// '.' (or sometimes ',') as the decimal separator, so all amounts/quantities are parsed
/// tolerantly. Extra ERP fields (netAmount, lineNetAmount, lineId, vatCategory, ...) are
/// ignored here but stay available to the SharePoint column templates.
/// </summary>
public sealed class AxDocument
{
    // ── envelope (every document type) ────────────────────────────────────

    /// <summary>Which configured document type this is, e.g. "SalesInvoice". Blank = SalesInvoice (pre-2.0 export).</summary>
    public string DocumentType { get; set; } = "";

    /// <summary>The ERP's number for the document (invoice no., order no., ...). Blank = <see cref="InvoiceNumber"/>.</summary>
    public string DocumentNumber { get; set; } = "";

    /// <summary>"Customer" or "Vendor". Only consulted when a never-seen documentType is registered.</summary>
    public string Party { get; set; } = "";

    /// <summary>AX company (dataAreaId). Selects the company entry when a folder serves several companies.</summary>
    public string DataAreaId { get; set; } = "";

    /// <summary>Output format of the e-invoice handler: "xrechnung" (XML) or "zugferd" (PDF/A-3). Other handlers ignore it.</summary>
    public string EInvoiceType { get; set; } = "";

    // ── e-invoice identification (Aktivbank Zentralregulierung and similar) ───
    // All optional; a plain invoice without them is unchanged.

    /// <summary>UNTDID 1001 type code: 380 invoice, 381 credit note, 384 correction. Blank = 380, or 381 when the line total is negative.</summary>
    public string InvoiceTypeCode { get; set; } = "";

    /// <summary>Aktivbank ZR-Kennzeichen (Verband/Kampagne), e.g. "42" or "0042"; zero-padded to 4 digits into BT-12 ContractReferencedDocument.</summary>
    public string ZrKennzeichen { get; set; } = "";

    /// <summary>Seller identifier at the settlement house (Vertragslieferantennummer, e.g. 99001) -> BT-29 SellerTradeParty/ID.</summary>
    public string SenderId { get; set; } = "";

    /// <summary>Seller GLN (13 digits) -> BT-29-0 SellerTradeParty/GlobalID scheme 0088.</summary>
    public string SenderGln { get; set; } = "";

    /// <summary>Buyer identifier (Anschlusshausnummer, e.g. 99002) -> BT-46 BuyerTradeParty/ID.</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>Buyer GLN (13 digits) -> BT-46-0 BuyerTradeParty/GlobalID scheme 0088. Anything that is not 13 digits is ignored with a warning.</summary>
    public string RecipientGln { get; set; } = "";

    // ── invoice payload (e-invoice handler) ───────────────────────────────

    /// <summary>Pre-2.0 name of the document number; still honored so an old AX export keeps working.</summary>
    public string InvoiceNumber { get; set; } = "";
    public string IssueDate { get; set; } = "";
    public string DueDate { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public string Reference { get; set; } = "";

    public string SenderName { get; set; } = "";
    public string SenderStreet { get; set; } = "";
    public string SenderPostcode { get; set; } = "";
    public string SenderCity { get; set; } = "";
    public string SenderCountry { get; set; } = "";
    public string SenderTaxId { get; set; } = "";
    public string SenderVatId { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderPhone { get; set; } = "";

    public string RecipientName { get; set; } = "";
    public string RecipientStreet { get; set; } = "";
    public string RecipientPostcode { get; set; } = "";
    public string RecipientCity { get; set; } = "";
    public string RecipientCountry { get; set; } = "";
    public string RecipientVatId { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public string RecipientEmailCc { get; set; } = "";

    /// <summary>Subject of the mail, from the Lasernet destination. May contain %1 for the document number.</summary>
    public string EmailSubject { get; set; } = "";

    /// <summary>Plain-text body of the mail, from the Lasernet destination.</summary>
    public string EmailBody { get; set; } = "";

    /// <summary>
    /// AX environment this document was exported from ("Live" / "Test" / "Dev"), stamped by
    /// BDCEnvironmentHelper. Blank on an AX that predates it, which counts as NOT production.
    /// </summary>
    public string Environment { get; set; } = "";

    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal VatRate { get; set; }
    public string VatNote { get; set; } = "";

    public string BankIban { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>
    /// The legal information company law requires on business letters (legal form, seat, register
    /// court and number, managing directors), usually the invoice footer. Written as an invoice
    /// note with subject code REG (BT-22 with BT-21).
    /// </summary>
    public string LegalNotice { get; set; } = "";

    public string ServicePeriodFrom { get; set; } = "";
    public string ServicePeriodTo { get; set; } = "";

    public List<AxLineItem> LineItems { get; set; } = new();

    /// <summary>
    /// Every top-level key this class does not bind (e.g. "purchase order reference",
    /// "buyerReference", "bankSwift"). The e-invoice mapper picks EN 16931 terms out of it
    /// (<see cref="Balsoft.Hive.BDC.EInvoice.InvoiceFieldAliases"/>), so a field the ERP
    /// adds to its export reaches the XML without a Hive BDC change.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();

    // ── payment terms (e-invoice handler) ─────────────────────────────────
    // The net due date is DueDate above. Cash discount (Skonto) terms come either as a list
    // (cashDiscounts: [{date, percent, amount, days}]) for chained discounts, or as the
    // single flat fields below. Both are mapped into BT-20 next to the net term.

    /// <summary>Optional human text for the net term, e.g. "Zahlbar ohne Abzug bis 22.07.2026". Blank = generated from dueDate.</summary>
    public string PaymentTermsText { get; set; } = "";

    /// <summary>Single cash discount: last day the discount applies (ISO or dd.MM.yyyy).</summary>
    public string CashDiscountDate { get; set; } = "";

    /// <summary>Single cash discount: percentage, e.g. "2" or "2.00".</summary>
    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal CashDiscountPercent { get; set; }

    /// <summary>Single cash discount: the discount amount in invoice currency, e.g. "77.30".</summary>
    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal CashDiscountAmount { get; set; }

    /// <summary>Single cash discount: days after the issue date; 0 = derive from CashDiscountDate.</summary>
    [JsonConverter(typeof(TolerantInt32Converter))]
    public int CashDiscountDays { get; set; }

    /// <summary>Chained cash discounts (e.g. 3 % within 10 days, 2 % within 20 days). Takes precedence over the flat fields when present.</summary>
    public List<AxCashDiscount> CashDiscounts { get; set; } = new();

    /// <summary>The discounts to emit: the list, else the flat fields when they carry anything.</summary>
    public IReadOnlyList<AxCashDiscount> EffectiveCashDiscounts()
    {
        if (CashDiscounts is { Count: > 0 }) return CashDiscounts;
        bool any = !string.IsNullOrWhiteSpace(CashDiscountDate) || CashDiscountPercent > 0m || CashDiscountAmount > 0m || CashDiscountDays > 0;
        return any
            ? new[] { new AxCashDiscount { Date = CashDiscountDate, Percent = CashDiscountPercent, Amount = CashDiscountAmount, Days = CashDiscountDays } }
            : Array.Empty<AxCashDiscount>();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parser settings for the dropped file (comments and trailing commas tolerated, like the binder).</summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AxDocument Parse(string json)
        => JsonSerializer.Deserialize<AxDocument>(json, SerializerOptions)
           ?? throw new InvalidDataException("JSON deserialized to null.");

    /// <summary>Bind from an already-parsed document, so one parse serves the DTO and the column templates.</summary>
    public static AxDocument FromElement(JsonElement root)
        => root.Deserialize<AxDocument>(SerializerOptions)
           ?? throw new InvalidDataException("JSON deserialized to null.");

    /// <summary>documentNumber, else the pre-2.0 invoiceNumber, else <paramref name="fallback"/> (the file-name id).</summary>
    public string ResolveNumber(string fallback)
        => !string.IsNullOrWhiteSpace(DocumentNumber) ? DocumentNumber.Trim()
         : !string.IsNullOrWhiteSpace(InvoiceNumber) ? InvoiceNumber.Trim()
         : fallback;

    public bool IsZugferd =>
        EInvoiceType.Contains("zugferd", StringComparison.OrdinalIgnoreCase);

    public bool IsXRechnung =>
        EInvoiceType.Contains("xrechnung", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One cash discount (Skonto) term as the ERP exports it.</summary>
public sealed class AxCashDiscount
{
    /// <summary>Last day the discount applies (ISO yyyy-MM-dd or dd.MM.yyyy).</summary>
    public string Date { get; set; } = "";

    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal Percent { get; set; }

    /// <summary>Discount amount in invoice currency (informational for the receiver; the percentage is what the XML rule carries).</summary>
    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal Amount { get; set; }

    /// <summary>Days after the issue date; 0 = derive from <see cref="Date"/>.</summary>
    [JsonConverter(typeof(TolerantInt32Converter))]
    public int Days { get; set; }
}

public sealed class AxLineItem
{
    public string Description { get; set; } = "";

    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal Quantity { get; set; }

    public string Unit { get; set; } = "";

    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal UnitPrice { get; set; }

    /// <summary>Line VAT rate in percent. Absent (-1) = the header vatRate. Lets one invoice mix 19 % and 7 % lines.</summary>
    [JsonConverter(typeof(TolerantDecimalConverter))]
    public decimal VatRate { get; set; } = -1m;

    /// <summary>Line VAT category (UNTDID 5305: S, Z, E, AE, K, G, O, ...). Blank = S, or E when the rate is 0.</summary>
    public string VatCategory { get; set; } = "";

    public bool HasOwnVatRate => VatRate >= 0m;

    /// <summary>Every line key not bound above (itemId, gtin, order line reference, ...), read by the e-invoice mapper.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>Parses an integer from a JSON number or a string ("25", "25.00"); AX exports numbers as text.</summary>
public sealed class TolerantInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt32(out int i) ? i : (int)Math.Round(reader.GetDecimal(), MidpointRounding.AwayFromZero);
        if (reader.TokenType == JsonTokenType.String)
            return (int)Math.Round(TolerantDecimalConverter.ParseDecimal(reader.GetString()), MidpointRounding.AwayFromZero);
        if (reader.TokenType == JsonTokenType.Null)
            return 0;
        throw new JsonException($"Cannot convert {reader.TokenType} to an integer.");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>Parses a decimal from a JSON number or a string ('.' or ',' decimal).</summary>
public sealed class TolerantDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetDecimal();

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return ParseDecimal(s);
        }

        if (reader.TokenType == JsonTokenType.Null)
            return 0m;

        throw new JsonException($"Cannot convert {reader.TokenType} to decimal.");
    }

    public static decimal ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        s = s.Trim();
        // Try invariant ('.' decimal) first; then swap ',' for '.' as a fallback.
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        var swapped = s.Replace(".", "").Replace(',', '.');
        if (decimal.TryParse(swapped, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            return d;
        throw new JsonException($"Cannot parse decimal from '{s}'.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
