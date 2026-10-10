using System.Globalization;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.CodeLists;

namespace Balsoft.Hive.BDC.EInvoice;

/// <summary>
/// Builds a Balsoft.Hive.EInvoice <see cref="Invoice"/> (EN 16931 model) from the AX invoice JSON.
/// Amounts are recomputed from line items (quantity * unitPrice) and the VAT rate per line
/// (header rate as the default); the AX-provided totals are intentionally ignored. Credit
/// notes carry positive amounts with the sign in the type code, and the seller/buyer
/// identifiers plus the ZR contract reference that a settlement house (Aktivbank) requires
/// are written when the export supplies them.
/// </summary>
public static class InvoiceMapper
{
    public const string BusinessProcess = Invoice.PeppolBillingProcess;

    // mirror of UNIT_CODE_MAP in mapper.ts (+ a few lenient aliases for D365 units)
    private static readonly Dictionary<string, string> UnitMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qty"] = UnitCode.One,
        ["hour"] = UnitCode.Hour,
        ["day"] = UnitCode.Day,
        ["piece"] = UnitCode.Piece,
        ["kg"] = UnitCode.Kilogram,
        ["m"] = UnitCode.Metre,
        ["l"] = UnitCode.Litre,
        // lenient extras for common D365 SalesUnit values
        ["stk"] = UnitCode.Piece,
        ["stück"] = UnitCode.Piece,
        ["pcs"] = UnitCode.Piece,
        ["ea"] = UnitCode.Piece,
        ["std"] = UnitCode.Hour,
        ["h"] = UnitCode.Hour,
        ["tag"] = UnitCode.Day,
        ["kgm"] = UnitCode.Kilogram,
        ["ltr"] = UnitCode.Litre,
        ["mtr"] = UnitCode.Metre,
    };

    public static decimal Round2(decimal n) => Math.Round(n, 2, MidpointRounding.AwayFromZero);

    /// <summary>Header keys Hive BDC reads elsewhere or recomputes; not reported as "not used in the e-invoice".</summary>
    private static readonly HashSet<string> NotForTheXml = new[]
    {
        "amountDue", "grossAmount", "netAmount", "taxAmount",          // recomputed from the lines
        "lineGrossAmount", "lineNetAmount", "lineTaxAmount", "lineId", // line totals recomputed, ids numbered 1..n
        "recipientEmailBcc", "aosInstance", "documentNumber", "party",
    }.Select(InvoiceFieldAliases.Normalize).ToHashSet();

    /// <param name="info">Receives one line per EN 16931 term taken from an extra JSON key, and the keys left out of the XML.</param>
    public static Invoice Map(AxDocument data, Action<string>? warn = null, Action<string>? info = null)
    {
        var (extra, unusedHeader) = InvoiceFieldAliases.ReadHeader(data.Extra);
        string? Pick(InvoiceFieldAliases.Term term)
        {
            if (!extra.TryGetValue(term.Bt, out var f)) return null;
            info?.Invoke($"e-invoice {term.Bt} {term.Label} = '{f.Value}' (from JSON '{f.SourceKey}')");
            return f.Value;
        }

        string sellerCountry = ResolveCountry(data.SenderCountry);
        string buyerCountry = ResolveCountry(data.RecipientCountry);
        var issueDate = ParseDate(data.IssueDate) ?? DateTime.Today;

        // ── Document type + sign ───────────────────────────────────────────
        // EN 16931 carries the sign in BT-3, not in the amounts: a credit note is 381 with
        // POSITIVE amounts (Aktivbank rejects negative values outright). AX exports returns
        // with negative quantities, so the lines are taken as absolute values once the type
        // is known. Without a type code, a negative line total means credit note.
        decimal rawTotal = data.LineItems.Sum(i => i.Quantity * i.UnitPrice);
        var type = ResolveInvoiceType(data.InvoiceTypeCode, rawTotal, warn);
        bool absolute = type != InvoiceTypeCode.CommercialInvoice || rawTotal < 0m;

        var inv = new Invoice
        {
            Number = data.ResolveNumber(""),   // the caller has already warned when neither number is present
            IssueDate = issueDate,
            TypeCode = type,
            Currency = ResolveCurrency(data.Currency),
            BusinessProcess = BusinessProcess,
            DueDate = ParseDate(data.DueDate),
        };

        if (!string.IsNullOrWhiteSpace(data.Notes))
            inv.Notes.Add(new InvoiceNote(data.Notes.Trim()));

        // ── Seller ──────────────────────────────────────────────────────────
        // BT-29 identifier and BT-29-0 GLN are what a settlement house (Aktivbank
        // Zentralregulierung) uses to recognise the supplier; both optional.
        inv.Seller = new Party
        {
            Name = data.SenderName,
            Address = new PostalAddress
            {
                Line1 = NullIfEmpty(data.SenderStreet),
                PostCode = NullIfEmpty(data.SenderPostcode),
                City = NullIfEmpty(data.SenderCity),
                CountryCode = sellerCountry,
            },
            VatIdentifier = string.IsNullOrWhiteSpace(data.SenderVatId) ? null : NormalizeVat(data.SenderVatId, sellerCountry),
            TaxRegistrationIdentifier = NullIfEmpty(data.SenderTaxId),
            Contact = new Contact
            {
                Name = NullIfEmpty(data.SenderName),
                Email = NullIfEmpty(data.SenderEmail),
                Phone = NullIfEmpty(data.SenderPhone),
            },
            ElectronicAddress = string.IsNullOrWhiteSpace(data.SenderEmail)
                ? null : new SchemedIdentifier(data.SenderEmail.Trim(), ElectronicAddressScheme.Email),
        };
        if (NullIfEmpty(data.SenderId) is { } senderId) inv.Seller.Identifiers.Add(new SchemedIdentifier(senderId.Trim()));
        if (Gln(data.SenderGln, "senderGln", warn) is { } senderGln) inv.Seller.Identifiers.Add(senderGln);

        // ── Buyer ───────────────────────────────────────────────────────────
        // BT-46 identifier (Anschlusshausnummer) or BT-46-0 GLN of the buying-group member.
        // EN 16931 allows one BT-46 (CII-SR-450 warns on both): the Anschlusshaus number wins,
        // it is what Aktivbank validates; the GLN is used only when there is no number.
        inv.Buyer = new Party
        {
            Name = data.RecipientName,
            Address = new PostalAddress
            {
                Line1 = NullIfEmpty(data.RecipientStreet),
                PostCode = NullIfEmpty(data.RecipientPostcode),
                City = NullIfEmpty(data.RecipientCity),
                CountryCode = buyerCountry,
            },
            VatIdentifier = NullIfEmpty(data.RecipientVatId)?.Trim(),
            ElectronicAddress = string.IsNullOrWhiteSpace(data.RecipientEmail)
                ? null : new SchemedIdentifier(data.RecipientEmail.Trim(), ElectronicAddressScheme.Email),
        };
        var recipientGln = Gln(data.RecipientGln, "recipientGln", warn);
        if (NullIfEmpty(data.RecipientId) is { } recipientId) inv.Buyer.Identifiers.Add(new SchemedIdentifier(recipientId.Trim()));
        else if (recipientGln is not null) inv.Buyer.Identifiers.Add(recipientGln);

        // BT-10 and BT-13: a dedicated key (buyerReference, "purchase order reference", ...) wins;
        // without one, the generic "reference" fills both, as it always did.
        string? reference = NullIfEmpty(data.Reference)?.Trim();
        inv.BuyerReference = Pick(InvoiceFieldAliases.BuyerReference) ?? reference;
        inv.PurchaseOrderReference = Pick(InvoiceFieldAliases.PurchaseOrderReference) ?? reference;
        inv.ProjectReference = Pick(InvoiceFieldAliases.ProjectReference);
        inv.SalesOrderReference = Pick(InvoiceFieldAliases.SalesOrderReference);
        inv.ReceivingAdviceReference = Pick(InvoiceFieldAliases.ReceivingAdviceReference);
        inv.DespatchAdviceReference = Pick(InvoiceFieldAliases.DespatchAdviceReference);
        inv.TenderOrLotReference = Pick(InvoiceFieldAliases.TenderOrLotReference);
        inv.BuyerAccountingReference = Pick(InvoiceFieldAliases.BuyerAccountingReference);
        if (Pick(InvoiceFieldAliases.PrecedingInvoiceNumber) is { } preceding)
            inv.PrecedingInvoices.Add(new PrecedingInvoiceReference
            {
                Number = preceding,
                IssueDate = ParseDate(Pick(InvoiceFieldAliases.PrecedingInvoiceDate)),
            });

        // BT-12 contract reference = the Aktivbank ZR-Kennzeichen (Verband/Kampagne), 4 digits
        // zero-padded. Padded here rather than in AX so "126" and "0126" both come out right.
        string zr = new string((data.ZrKennzeichen ?? "").Where(char.IsDigit).ToArray());
        if (zr.Length > 0)
        {
            if (zr.Length > 4) warn?.Invoke($"zrKennzeichen '{data.ZrKennzeichen}' has more than 4 digits; sent as-is.");
            inv.ContractReference = zr.Length < 4 ? zr.PadLeft(4, '0') : zr;
        }
        else
            inv.ContractReference = Pick(InvoiceFieldAliases.ContractReference);

        // ── Delivery & payment ──────────────────────────────────────────────
        string? deliveryText = Pick(InvoiceFieldAliases.DeliveryDate);
        var deliveryDate = ParseDate(deliveryText);
        if (deliveryText is not null && deliveryDate is null)
            warn?.Invoke($"delivery date '{deliveryText}' is not a recognizable date; the issue date is used.");
        inv.Delivery = new DeliveryInformation { ActualDeliveryDate = deliveryDate ?? issueDate };

        var iban = (data.BankIban ?? "").Replace(" ", "");
        if (!string.IsNullOrWhiteSpace(iban))
        {
            inv.PaymentInstructions = PaymentInstructions.SepaCreditTransfer(iban);
            var account = inv.PaymentInstructions.CreditTransfers[0];
            account.AccountName = Pick(InvoiceFieldAliases.AccountName);
            account.ServiceProviderIdentifier = Pick(InvoiceFieldAliases.Bic)?.Replace(" ", "");
            inv.PaymentInstructions.RemittanceInformation = Pick(InvoiceFieldAliases.RemittanceInformation);
        }

        AddPaymentTerms(inv, data, issueDate, warn);

        // ── Lines ───────────────────────────────────────────────────────────
        var periodStart = ParseDate(data.ServicePeriodFrom);
        var periodEnd = ParseDate(data.ServicePeriodTo);
        var unusedLine = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineTerms = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in data.LineItems)
        {
            decimal rate = item.HasOwnVatRate ? item.VatRate : data.VatRate;
            var category = ResolveCategory(item.VatCategory, rate, warn);
            var (lx, lunused) = InvoiceFieldAliases.ReadLine(item.Extra);
            foreach (var k in lunused) unusedLine.Add(k);
            string? L(InvoiceFieldAliases.Term t)
            {
                if (!lx.TryGetValue(t.Bt, out var f)) return null;
                lineTerms.Add($"{t.Bt} {t.Label} (from '{f.SourceKey}')");
                return f.Value;
            }
            var line = new InvoiceLine
            {
                Item = { Name = item.Description },
                // BT-146 (net price) must not be negative (BR-27): on a plain invoice a negative
                // unit price keeps its economic sign by moving it onto the quantity.
                Quantity = absolute ? Math.Abs(item.Quantity) : item.Quantity * (item.UnitPrice < 0m ? -1m : 1m),
                NetPrice = Math.Abs(item.UnitPrice),
                UnitCode = ResolveUnit(item.Unit, warn),
                VatCategory = category,
                VatRate = category == VatCategory.NotSubjectToVat ? null : rate,
                Period = periodStart is null && periodEnd is null ? null : new Period(periodStart, periodEnd),
                Note = L(InvoiceFieldAliases.LineNote),
                OrderLineReference = L(InvoiceFieldAliases.OrderLineReference),
                BuyerAccountingReference = L(InvoiceFieldAliases.LineAccountingReference),
            };
            line.Item.SellersIdentifier = L(InvoiceFieldAliases.SellersItemId);
            line.Item.BuyersIdentifier = L(InvoiceFieldAliases.BuyersItemId);
            if (L(InvoiceFieldAliases.Gtin) is { } gtin)
                line.Item.StandardIdentifier = new SchemedIdentifier(gtin, IdentifierScheme.Gtin);
            if (L(InvoiceFieldAliases.OriginCountry) is { } origin)
            {
                string iso = ResolveCountry(origin);
                if (iso == "DE" && !origin.Trim().Equals("DE", StringComparison.OrdinalIgnoreCase) && !CountryNameMap.ContainsKey(origin.Trim()))
                    warn?.Invoke($"line origin country '{origin}' is not a known country; not written.");
                else line.Item.OriginCountryCode = iso;
            }
            inv.AddLine(line);
        }
        if (lineTerms.Count > 0)
            info?.Invoke("e-invoice line fields from the JSON: " + string.Join("; ", lineTerms));

        var leftOut = unusedHeader.Concat(unusedLine.Select(k => "lineItems." + k))
            .Where(k => !NotForTheXml.Contains(InvoiceFieldAliases.Normalize(k.Replace("lineItems.", ""))))
            .ToList();
        if (leftOut.Count > 0)
            info?.Invoke("JSON fields not written to the e-invoice (no EN 16931 term for them): " + string.Join(", ", leftOut));

        // ── VAT exemption reasons ───────────────────────────────────────────
        // E, AE, K, G and O need a reason in the breakdown (BT-120); the export's vatNote, else a default.
        foreach (var category in inv.Lines.Select(l => l.VatCategory).Distinct().Where(c => c.RequiresExemptionReason()))
        {
            inv.VatExemptions.Add(new VatExemption
            {
                Category = category,
                Reason = string.IsNullOrWhiteSpace(data.VatNote) ? "Exempt from VAT" : data.VatNote.Trim(),
            });
        }

        return inv;
    }

    /// <summary>UNTDID 1001 codes the service accepts; anything else falls back to 380 with a warning.</summary>
    private static readonly HashSet<int> SupportedTypeCodes = new() { 380, 381, 384, 383, 389, 261, 326, 386, 83, 84 };

    private static InvoiceTypeCode ResolveInvoiceType(string? code, decimal rawTotal, Action<string>? warn)
    {
        string c = (code ?? "").Trim().TrimStart('0');
        if (c.Length > 0)
        {
            if (int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && SupportedTypeCodes.Contains(n))
                return (InvoiceTypeCode)n;
            warn?.Invoke($"invoiceTypeCode '{code}' is not a supported UNTDID 1001 code; using 380 (invoice).");
        }
        else if (rawTotal < 0m)
        {
            warn?.Invoke("no invoiceTypeCode and a negative total: treated as a credit note (381) with absolute amounts.");
            return InvoiceTypeCode.CreditNote;
        }
        return InvoiceTypeCode.CommercialInvoice;
    }

    /// <summary>UNTDID 5305 category from the export, else S, or E for a 0 % rate.</summary>
    private static VatCategory ResolveCategory(string? category, decimal rate, Action<string>? warn)
    {
        string c = (category ?? "").Trim();
        if (c.Length > 0)
        {
            if (CodeExtensions.TryParseVatCategory(c, out var parsed)) return parsed;
            warn?.Invoke($"vatCategory '{category}' is not a known UNTDID 5305 code; derived from the rate instead.");
        }
        return rate == 0m ? VatCategory.Exempt : VatCategory.StandardRate;
    }

    /// <summary>A GS1 GLN (scheme 0088) when the value is 13 digits; otherwise null with a warning.</summary>
    private static SchemedIdentifier? Gln(string? value, string field, Action<string>? warn)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length == 13 && v.All(char.IsDigit)) return new SchemedIdentifier(v, IdentifierScheme.Gln);
        warn?.Invoke($"{field} '{v}' is not a 13-digit GLN; not written.");
        return null;
    }

    private static string ResolveUnit(string unit, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(unit)) return UnitCode.One;
        if (UnitMap.TryGetValue(unit.Trim(), out var code)) return code;
        // accept a raw UN/ECE Rec 20 code if AX already provides one
        string upper = unit.Trim().ToUpperInvariant();
        if (CodeList.IsUnitCode(upper)) return upper;
        warn?.Invoke($"Unknown unit '{unit}' -> defaulting to C62.");
        return UnitCode.One;
    }

    /// <summary>`{country}{vatId without leading 2 alpha}` — mirrors mapper.ts.</summary>
    private static string NormalizeVat(string vatId, string country)
    {
        var trimmed = vatId.Trim();
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1]))
            trimmed = trimmed.Substring(2);
        return $"{country}{trimmed}";
    }

    /// <summary>ISO 3166-1 alpha-2 code for a code or an English/German country name; DE when unknown.</summary>
    public static string ResolveCountry(string country)
    {
        if (string.IsNullOrWhiteSpace(country)) return "DE";
        var c = country.Trim();
        if (c.Length == 2 && CodeList.IsCountryCode(c.ToUpperInvariant())) return c.ToUpperInvariant();
        return CountryNameMap.TryGetValue(c, out var iso) ? iso : "DE";
    }

    private static string ResolveCurrency(string currency)
    {
        string c = (currency ?? "").Trim().ToUpperInvariant();
        return CodeList.IsCurrencyCode(c) ? c : "EUR";
    }

    /// <summary>
    /// BT-20 payment terms: the net due date AND every cash discount (Skonto) the ERP offers.
    /// The readable lines ("Zahlbar ohne Abzug bis ...", "Bei Zahlung bis ... 2 % Skonto") go
    /// into the free text; each discount is also added as a <see cref="CashDiscount"/>, which
    /// the writer renders in the XRechnung syntax (#SKONTO#TAGE=n#PROZENT=p.pp#) so a
    /// receiver's software can apply it. Without this, the receiver saw only the net date while
    /// the PDF promised a discount (customer report, 2026-09-10).
    /// </summary>
    private static void AddPaymentTerms(Invoice inv, AxDocument data, DateTime issueDate, Action<string>? warn)
    {
        var dueDate = ParseDate(data.DueDate);
        var lines = new List<string>();

        string netText = !string.IsNullOrWhiteSpace(data.PaymentTermsText)
            ? data.PaymentTermsText.Trim()
            : dueDate.HasValue ? $"Zahlbar ohne Abzug bis {dueDate.Value:dd.MM.yyyy}" : "";
        // A non-empty description is required: an empty <ram:Description/> violates
        // PEPPOL-EN16931-R008 ("Document MUST not contain empty elements").
        if (dueDate.HasValue || netText.Length > 0)
            lines.Add(netText.Length > 0 ? netText : "Zahlbar ohne Abzug");

        foreach (var d in data.EffectiveCashDiscounts())
        {
            var date = ParseDate(d.Date);
            int? days = d.Days > 0 ? d.Days : date.HasValue ? Math.Max(0, (date.Value.Date - issueDate.Date).Days) : null;
            if (d.Percent <= 0m && d.Amount <= 0m)
            {
                warn?.Invoke($"cash discount until {d.Date} has neither a percentage nor an amount; skipped.");
                continue;
            }
            if (days is null)
            {
                warn?.Invoke($"cash discount ({d.Percent} %) has no date and no day count; skipped.");
                continue;
            }
            string amount = d.Amount > 0m ? $" ({d.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {inv.Currency})" : "";
            string percent = d.Percent.ToString("0.##", CultureInfo.InvariantCulture);
            lines.Add(date.HasValue
                ? $"Bei Zahlung bis {date.Value:dd.MM.yyyy} {percent} % Skonto{amount}"
                : $"Bei Zahlung innerhalb von {days} Tagen {percent} % Skonto{amount}");
            if (d.Percent > 0m)
                inv.CashDiscounts.Add(new CashDiscount { Days = days.Value, Percent = d.Percent });
        }

        inv.PaymentTerms = lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        foreach (var fmt in new[] { "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy", "yyyyMMdd" })
            if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.Date;
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // mirror of resolveCountryCode() in mapper.ts
    private static readonly Dictionary<string, string> CountryNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["germany"] = "DE", ["deutschland"] = "DE",
        ["turkey"] = "TR", ["türkiye"] = "TR",
        ["austria"] = "AT", ["österreich"] = "AT",
        ["switzerland"] = "CH", ["schweiz"] = "CH",
        ["france"] = "FR", ["frankreich"] = "FR",
        ["netherlands"] = "NL", ["niederlande"] = "NL",
        ["belgium"] = "BE", ["belgien"] = "BE",
        ["italy"] = "IT", ["italien"] = "IT",
        ["spain"] = "ES", ["spanien"] = "ES",
        ["portugal"] = "PT",
        ["poland"] = "PL", ["polen"] = "PL",
        ["czech republic"] = "CZ", ["tschechien"] = "CZ",
        ["denmark"] = "DK", ["dänemark"] = "DK",
        ["sweden"] = "SE", ["schweden"] = "SE",
        ["norway"] = "NO", ["norwegen"] = "NO",
        ["finland"] = "FI", ["finnland"] = "FI",
        ["ireland"] = "IE", ["irland"] = "IE",
        ["united kingdom"] = "GB", ["großbritannien"] = "GB",
        ["united states"] = "US",
        ["luxembourg"] = "LU", ["luxemburg"] = "LU",
        ["greece"] = "GR", ["griechenland"] = "GR",
        ["hungary"] = "HU", ["ungarn"] = "HU",
        ["romania"] = "RO", ["rumänien"] = "RO",
        ["bulgaria"] = "BG", ["bulgarien"] = "BG",
        ["croatia"] = "HR", ["kroatien"] = "HR",
        ["slovakia"] = "SK", ["slowakei"] = "SK",
        ["slovenia"] = "SI", ["slowenien"] = "SI",
        ["estonia"] = "EE", ["estland"] = "EE",
        ["latvia"] = "LV", ["lettland"] = "LV",
        ["lithuania"] = "LT", ["litauen"] = "LT",
        ["malta"] = "MT",
        ["cyprus"] = "CY", ["zypern"] = "CY",
    };
}
