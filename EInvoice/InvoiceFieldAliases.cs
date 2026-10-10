using System.Text.Json;

namespace Balsoft.Hive.BDC.EInvoice;

/// <summary>
/// The EN 16931 business terms an ERP export can fill without a code change in Hive BDC.
/// The ERP decides the JSON key (AX's "Kundenrechnungszuordnung" maps a field to any name,
/// e.g. CustomerRef -> "purchase order reference"), so every term is recognised under the
/// usual spellings: English, camelCase, with spaces, German, and the BT number itself.
/// Matching ignores case, spaces, '-', '_' and '.', so "purchase order reference",
/// "purchaseOrderReference", "Purchase_Order_Reference" and "BT-13" are the same key.
/// A term is written to the XML only when its value is not empty.
/// </summary>
public static class InvoiceFieldAliases
{
    public sealed record Term(string Bt, string Label);

    public sealed record Found(Term Term, string Value, string SourceKey);

    // ── document level ──────────────────────────────────────────────────────
    public static readonly Term BuyerReference = new("BT-10", "Buyer reference");
    public static readonly Term ProjectReference = new("BT-11", "Project reference");
    public static readonly Term ContractReference = new("BT-12", "Contract reference");
    public static readonly Term PurchaseOrderReference = new("BT-13", "Purchase order reference");
    public static readonly Term SalesOrderReference = new("BT-14", "Sales order reference");
    public static readonly Term ReceivingAdviceReference = new("BT-15", "Receiving advice reference");
    public static readonly Term DespatchAdviceReference = new("BT-16", "Despatch advice reference");
    public static readonly Term TenderOrLotReference = new("BT-17", "Tender or lot reference");
    public static readonly Term BuyerAccountingReference = new("BT-19", "Buyer accounting reference");
    public static readonly Term PrecedingInvoiceNumber = new("BT-25", "Preceding invoice reference");
    public static readonly Term PrecedingInvoiceDate = new("BT-26", "Preceding invoice issue date");
    public static readonly Term DeliveryDate = new("BT-72", "Actual delivery date");
    public static readonly Term RemittanceInformation = new("BT-83", "Remittance information");
    public static readonly Term AccountName = new("BT-85", "Payment account name");
    public static readonly Term Bic = new("BT-86", "Payment service provider identifier (BIC)");

    // ── line level ──────────────────────────────────────────────────────────
    public static readonly Term LineNote = new("BT-127", "Invoice line note");
    public static readonly Term OrderLineReference = new("BT-132", "Referenced purchase order line reference");
    public static readonly Term LineAccountingReference = new("BT-133", "Invoice line buyer accounting reference");
    public static readonly Term SellersItemId = new("BT-155", "Item seller's identifier");
    public static readonly Term BuyersItemId = new("BT-156", "Item buyer's identifier");
    public static readonly Term Gtin = new("BT-157", "Item standard identifier (GTIN)");
    public static readonly Term OriginCountry = new("BT-159", "Item country of origin");

    private static readonly Dictionary<string, Term> Header = Build(new()
    {
        [BuyerReference] = new[] { "buyerReference", "leitwegId", "leitweg", "käuferreferenz", "kaeuferreferenz", "BT-10" },
        [ProjectReference] = new[] { "projectReference", "projectNumber", "projektnummer", "projektreferenz", "BT-11" },
        [ContractReference] = new[] { "contractReference", "contractNumber", "vertragsnummer", "BT-12" },
        [PurchaseOrderReference] = new[] { "purchase order reference", "purchaseOrderReference", "buyerOrderReference", "customerOrderNumber", "customerPurchaseOrder", "bestellnummer", "kundenbestellnummer", "BT-13" },
        [SalesOrderReference] = new[] { "orderReference", "salesOrderReference", "salesOrderNumber", "salesId", "auftragsnummer", "BT-14" },
        [ReceivingAdviceReference] = new[] { "receivingAdviceReference", "goodsReceiptNumber", "wareneingangsnummer", "BT-15" },
        [DespatchAdviceReference] = new[] { "despatchAdviceReference", "dispatchAdviceReference", "deliveryNoteNumber", "packingSlipNumber", "packingSlipId", "lieferscheinnummer", "BT-16" },
        [TenderOrLotReference] = new[] { "tenderReference", "lotReference", "tenderOrLotReference", "vergabenummer", "BT-17" },
        [BuyerAccountingReference] = new[] { "buyerAccountingReference", "costCenter", "kostenstelle", "BT-19" },
        [PrecedingInvoiceNumber] = new[] { "precedingInvoiceReference", "precedingInvoiceNumber", "originalInvoiceNumber", "correctedInvoiceNumber", "ursprungsrechnung", "BT-25" },
        [PrecedingInvoiceDate] = new[] { "precedingInvoiceDate", "originalInvoiceDate", "BT-26" },
        [DeliveryDate] = new[] { "deliveryDate", "actualDeliveryDate", "lieferdatum", "leistungsdatum", "BT-72" },
        [RemittanceInformation] = new[] { "remittanceInformation", "paymentReference", "verwendungszweck", "BT-83" },
        [AccountName] = new[] { "bankAccountHolder", "accountName", "kontoinhaber", "BT-85" },
        [Bic] = new[] { "bankSwift", "bic", "swift", "swiftCode", "BT-86" },
    });

    private static readonly Dictionary<string, Term> Line = Build(new()
    {
        [LineNote] = new[] { "lineNote", "note", "notiz", "BT-127" },
        [OrderLineReference] = new[] { "orderLineReference", "purchaseOrderLine", "purchaseOrderLineReference", "bestellposition", "BT-132" },
        [LineAccountingReference] = new[] { "lineAccountingReference", "buyerAccountingReference", "costCenter", "kostenstelle", "BT-133" },
        [SellersItemId] = new[] { "itemId", "itemNumber", "sellersItemIdentifier", "artikelnummer", "BT-155" },
        [BuyersItemId] = new[] { "buyersItemIdentifier", "buyerItemId", "customerItemId", "externalItemId", "kundenartikelnummer", "BT-156" },
        [Gtin] = new[] { "gtin", "ean", "eanCode", "barcode", "BT-157" },
        [OriginCountry] = new[] { "originCountry", "countryOfOrigin", "ursprungsland", "BT-159" },
    });

    private static Dictionary<string, Term> Build(Dictionary<Term, string[]> spellings)
    {
        var map = new Dictionary<string, Term>(StringComparer.Ordinal);
        foreach (var (term, keys) in spellings)
            foreach (var k in keys)
                map[Normalize(k)] = term;
        return map;
    }

    /// <summary>The comparable form of a JSON key: lower case, letters and digits only.</summary>
    public static string Normalize(string key)
        => new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The document level terms present in <paramref name="extra"/>, and the keys that matched no term.</summary>
    public static (Dictionary<string, Found> Found, List<string> Unused) ReadHeader(IReadOnlyDictionary<string, JsonElement>? extra)
        => Read(extra, Header);

    /// <summary>The line level terms present in <paramref name="extra"/>, and the keys that matched no term.</summary>
    public static (Dictionary<string, Found> Found, List<string> Unused) ReadLine(IReadOnlyDictionary<string, JsonElement>? extra)
        => Read(extra, Line);

    private static (Dictionary<string, Found>, List<string>) Read(IReadOnlyDictionary<string, JsonElement>? extra, Dictionary<string, Term> table)
    {
        var found = new Dictionary<string, Found>(StringComparer.Ordinal);
        var unused = new List<string>();
        if (extra is null) return (found, unused);
        foreach (var (key, element) in extra)
        {
            string? value = Text(element);
            if (!table.TryGetValue(Normalize(key), out var term))
            {
                unused.Add(key);
                continue;
            }
            // Empty values are not written; the first non-empty spelling wins.
            if (string.IsNullOrWhiteSpace(value) || found.ContainsKey(term.Bt)) continue;
            found[term.Bt] = new Found(term, value.Trim(), key);
        }
        return (found, unused);
    }

    private static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        _ => null,
    };
}
