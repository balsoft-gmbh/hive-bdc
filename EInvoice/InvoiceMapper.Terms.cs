using System.Globalization;
using System.Text.Json;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.CodeLists;
using Term = Balsoft.Hive.BDC.EInvoice.InvoiceFieldAliases.Term;
using Found = Balsoft.Hive.BDC.EInvoice.InvoiceFieldAliases.Found;

namespace Balsoft.Hive.BDC.EInvoice;

/// <summary>
/// The EN 16931 terms beyond the core invoice: parties' extra fields, payee, tax representative, card and direct debit,
/// document allowances and charges, supporting documents, item classifications and attributes, and the repeating groups
/// an ERP sends as JSON lists. A group whose mandatory member is missing is left out with a warning, so the XML still validates.
/// </summary>
public static partial class InvoiceMapper
{
    public const string BatchAttributeName = "Charge";
    public const string HsScheme = "HS";
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".csv"] = "text/csv",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
    };

    private static void AddNotes(Invoice inv, AxDocument data, Func<Term, string?> pick, Action<string>? warn, List<string> unusedHeader)
    {
        var extra = data.Extra;
        // BT-21 belongs to the "notes" text; without one it goes with the BT-22 key.
        if (pick(InvoiceFieldAliases.InvoiceNote) is { } note)
            inv.Notes.Add(new InvoiceNote(note, string.IsNullOrWhiteSpace(data.Notes) ? pick(InvoiceFieldAliases.NoteSubjectCode) : null));

        foreach (var (key, position, entry) in Entries(InvoiceFieldAliases.Notes, extra, warn, unusedHeader))
        {
            var (found, unused) = InvoiceFieldAliases.ReadEntry(InvoiceFieldAliases.Notes, entry);
            ReportUnused(key, unused, unusedHeader);
            if (Value(found, InvoiceFieldAliases.InvoiceNote) is { } text)
                inv.Notes.Add(new InvoiceNote(text, Value(found, InvoiceFieldAliases.NoteSubjectCode)));
            else
                warn?.Invoke($"'{key}' entry {position} has no text (BT-22); skipped.");
        }

        string? terms = pick(InvoiceFieldAliases.DeliveryTermsText) ?? pick(InvoiceFieldAliases.DeliveryTermsCode);
        string? mode = pick(InvoiceFieldAliases.DeliveryMode);
        var parts = new List<string>();
        if (terms is not null) parts.Add("Delivery terms: " + terms);
        if (mode is not null) parts.Add("Shipping method: " + mode);
        if (parts.Count > 0) inv.Notes.Add(new InvoiceNote(string.Join("; ", parts)));
    }

    private static void AddDocumentTerms(Invoice inv, Func<Term, string?> pick, Action<string>? warn)
    {
        string? taxCurrency = pick(InvoiceFieldAliases.TaxCurrency)?.ToUpperInvariant();
        decimal? vatInTaxCurrency = OptionalDecimal(pick(InvoiceFieldAliases.VatTotalInTaxCurrency), "VAT total in accounting currency", warn);
        if (taxCurrency is not null && !CodeList.IsCurrencyCode(taxCurrency))
        {
            warn?.Invoke($"VAT accounting currency '{taxCurrency}' is not an ISO 4217 code; BT-6 and BT-111 not written.");
            taxCurrency = null;
        }
        if (taxCurrency is not null && vatInTaxCurrency is null)
            warn?.Invoke("VAT accounting currency (BT-6) needs the VAT total in that currency (BT-111); both not written.");
        else if (taxCurrency is null && vatInTaxCurrency is not null)
            warn?.Invoke("VAT total in accounting currency (BT-111) needs the currency code (BT-6); not written.");
        else if (taxCurrency is not null)
        {
            inv.TaxCurrency = taxCurrency;
            inv.VatTotalInTaxCurrency = vatInTaxCurrency;
        }

        string? taxPointText = pick(InvoiceFieldAliases.TaxPointDate);
        inv.TaxPointDate = DateOrWarn(taxPointText, "VAT point date (BT-7)", warn);
        string? taxPointCode = pick(InvoiceFieldAliases.TaxPointDateCode);
        if (taxPointCode is not null && inv.TaxPointDate is not null)
            warn?.Invoke("VAT point date (BT-7) and its code (BT-8) exclude each other; the date is written.");
        else inv.TaxPointDateCode = taxPointCode;

        if (pick(InvoiceFieldAliases.InvoicedObject) is { } invoicedObject)
            inv.InvoicedObjectIdentifier = new SchemedIdentifier(invoicedObject, pick(InvoiceFieldAliases.InvoicedObjectScheme));

        var start = DateOrWarn(pick(InvoiceFieldAliases.InvoicingPeriodStart), "invoicing period start (BT-73)", warn);
        var end = DateOrWarn(pick(InvoiceFieldAliases.InvoicingPeriodEnd), "invoicing period end (BT-74)", warn);
        if (start is not null || end is not null) inv.InvoicingPeriod = new Period(start, end);

        if (OptionalDecimal(pick(InvoiceFieldAliases.RoundingAmount), "rounding amount", warn) is { } rounding && rounding != 0m)
            inv.RoundingAmount = rounding;
    }

    private static void CompletePartyTerms(Party party, Func<Term, string?> pick, bool seller, Action<string>? warn)
    {
        party.TradingName = pick(seller ? InvoiceFieldAliases.SellerTradingName : InvoiceFieldAliases.BuyerTradingName);
        if (pick(seller ? InvoiceFieldAliases.SellerLegalId : InvoiceFieldAliases.BuyerLegalId) is { } legal)
            party.LegalRegistrationIdentifier = new SchemedIdentifier(legal, pick(seller ? InvoiceFieldAliases.SellerLegalIdScheme : InvoiceFieldAliases.BuyerLegalIdScheme));
        party.Address.Line2 = pick(seller ? InvoiceFieldAliases.SellerAddressLine2 : InvoiceFieldAliases.BuyerAddressLine2);
        party.Address.Line3 = pick(seller ? InvoiceFieldAliases.SellerAddressLine3 : InvoiceFieldAliases.BuyerAddressLine3);
        party.Address.CountrySubdivision = pick(seller ? InvoiceFieldAliases.SellerSubdivision : InvoiceFieldAliases.BuyerSubdivision);

        if (seller)
        {
            // XRechnung (CII-SR-465, CII-SR-466 as errors): a contact has a person name or a department, not both. A department
            // replaces the company name the contact falls back to, an explicit contact name wins over it.
            if (pick(InvoiceFieldAliases.SellerContactDepartment) is { } sellerDepartment && party.Contact is not null)
            {
                if (pick(InvoiceFieldAliases.SellerContactName) is null) (party.Contact.Name, party.Contact.Department) = (null, sellerDepartment);
                else warn?.Invoke("seller contact department (BT-41-0) left out: XRechnung allows a contact name or a department, not both (CII-SR-465).");
            }
            party.AdditionalLegalInformation = pick(InvoiceFieldAliases.SellerAdditionalLegalInfo);
            string? scheme = pick(InvoiceFieldAliases.SellerElectronicAddressScheme);
            if (pick(InvoiceFieldAliases.SellerElectronicAddress) is { } address)
                party.ElectronicAddress = new SchemedIdentifier(address, scheme ?? (address.Contains('@') ? ElectronicAddressScheme.Email : null));
            else if (scheme is not null && party.ElectronicAddress is not null)
                party.ElectronicAddress.Scheme = scheme;
            return;
        }

        if (pick(InvoiceFieldAliases.BuyerElectronicAddressScheme) is { } buyerScheme && party.ElectronicAddress is not null)
            party.ElectronicAddress.Scheme = buyerScheme;
        string? name = pick(InvoiceFieldAliases.BuyerContactName);
        string? department = pick(InvoiceFieldAliases.BuyerContactDepartment);
        string? phone = pick(InvoiceFieldAliases.BuyerContactPhone);
        string? email = pick(InvoiceFieldAliases.BuyerContactEmail);
        if (name is not null && department is not null)
        {
            warn?.Invoke("buyer contact department (BT-56-0) left out: XRechnung allows a contact name or a department, not both (CII-SR-466).");
            department = null;
        }
        if (name is not null || department is not null || phone is not null || email is not null)
            party.Contact = new Contact { Name = name, Department = department, Phone = phone, Email = email };
    }

    private static void AddPrecedingInvoices(Invoice inv, IReadOnlyDictionary<string, JsonElement> extra, Action<string>? warn, List<string> unusedHeader)
    {
        foreach (var (key, position, entry) in Entries(InvoiceFieldAliases.PrecedingInvoices, extra, warn, unusedHeader))
        {
            var (found, unused) = InvoiceFieldAliases.ReadEntry(InvoiceFieldAliases.PrecedingInvoices, entry);
            ReportUnused(key, unused, unusedHeader);
            if (Value(found, InvoiceFieldAliases.PrecedingInvoiceNumber) is not { } number)
            {
                warn?.Invoke($"'{key}' entry {position} has no invoice number (BT-25); skipped.");
                continue;
            }
            if (inv.PrecedingInvoices.Any(p => p.Number.Equals(number, StringComparison.OrdinalIgnoreCase))) continue;
            inv.PrecedingInvoices.Add(new PrecedingInvoiceReference
            {
                Number = number,
                IssueDate = DateOrWarn(Value(found, InvoiceFieldAliases.PrecedingInvoiceDate), $"'{key}' entry {position} date (BT-26)", warn),
            });
        }
    }

    private static void AddPayeeAndTaxRepresentative(Invoice inv, Func<Term, string?> pick, Action<string>? warn)
    {
        string? payeeName = pick(InvoiceFieldAliases.PayeeName);
        string? payeeId = pick(InvoiceFieldAliases.PayeeId);
        string? payeeLegal = pick(InvoiceFieldAliases.PayeeLegalId);
        if (payeeName is not null)
        {
            inv.Payee = new Payee
            {
                Name = payeeName,
                Identifier = payeeId is null ? null : new SchemedIdentifier(payeeId, pick(InvoiceFieldAliases.PayeeIdScheme)),
                LegalRegistrationIdentifier = payeeLegal is null ? null : new SchemedIdentifier(payeeLegal, pick(InvoiceFieldAliases.PayeeLegalIdScheme)),
            };
        }
        else if (payeeId is not null || payeeLegal is not null)
            warn?.Invoke("payee identifier given without the payee name (BT-59); payee not written.");

        string? repName = pick(InvoiceFieldAliases.TaxRepName);
        string? repVat = pick(InvoiceFieldAliases.TaxRepVatId);
        string? repCountry = pick(InvoiceFieldAliases.TaxRepCountry);
        string? repLine1 = pick(InvoiceFieldAliases.TaxRepAddressLine1);
        string? repCity = pick(InvoiceFieldAliases.TaxRepCity);
        if (repName is null && repVat is null && repCountry is null && repLine1 is null && repCity is null) return;
        var missing = new List<string>();
        if (repName is null) missing.Add("name (BT-62)");
        if (repVat is null) missing.Add("VAT identifier (BT-63)");
        if (repCountry is null) missing.Add("country (BT-69)");
        if (missing.Count > 0)
        {
            warn?.Invoke("seller tax representative lacks " + string.Join(", ", missing) + "; not written.");
            return;
        }
        inv.SellerTaxRepresentative = new TaxRepresentative
        {
            Name = repName!,
            VatIdentifier = repVat!.Replace(" ", ""),
            Address = new PostalAddress
            {
                Line1 = repLine1,
                Line2 = pick(InvoiceFieldAliases.TaxRepAddressLine2),
                Line3 = pick(InvoiceFieldAliases.TaxRepAddressLine3),
                City = repCity,
                PostCode = pick(InvoiceFieldAliases.TaxRepPostcode),
                CountrySubdivision = pick(InvoiceFieldAliases.TaxRepSubdivision),
                CountryCode = ResolveCountry(repCountry!),
            },
        };
    }

    private static SchemedIdentifier? DeliveryLocation(Func<Term, string?> pick, Action<string>? warn)
    {
        if (pick(InvoiceFieldAliases.ShipToLocationId) is { } id)
            return new SchemedIdentifier(id, pick(InvoiceFieldAliases.ShipToLocationScheme));
        return Gln(pick(InvoiceFieldAliases.ShipToGln), "deliveryGln", warn);
    }

    private static void AddCardAndDirectDebit(Invoice inv, Func<Term, string?> pick, Action<string>? warn)
    {
        string? code = pick(InvoiceFieldAliases.PaymentMeansCode);
        if (code is not null && !(code.Length is > 0 and <= 3 && code.All(char.IsDigit)))
        {
            warn?.Invoke($"payment means code '{code}' is not a UNTDID 4461 code; the derived code is kept.");
            code = null;
        }

        string? cardNumber = pick(InvoiceFieldAliases.CardNumber);
        string? cardHolder = pick(InvoiceFieldAliases.CardHolder);
        PaymentCard? card = null;
        if (cardNumber is not null) card = new PaymentCard { PrimaryAccountNumber = cardNumber, HolderName = cardHolder };
        else if (cardHolder is not null) warn?.Invoke("card holder given without the card number (BT-87); card not written.");

        string? mandate = pick(InvoiceFieldAliases.MandateReference);
        string? creditor = pick(InvoiceFieldAliases.CreditorId);
        string? debited = pick(InvoiceFieldAliases.DebitedAccount);
        DirectDebit? debit = null;
        if (mandate is not null || creditor is not null || debited is not null)
        {
            var missing = new List<string>();
            if (mandate is null) missing.Add("mandate reference (BT-89)");
            if (creditor is null) missing.Add("creditor identifier (BT-90)");
            if (debited is null) missing.Add("debited account (BT-91)");
            if (missing.Count > 0) warn?.Invoke("direct debit lacks " + string.Join(", ", missing) + "; not written.");
            else debit = new DirectDebit { MandateReference = mandate, CreditorIdentifier = creditor, DebitedAccountIdentifier = Compact(debited) };
        }

        string? text = pick(InvoiceFieldAliases.PaymentMeansText);
        if (inv.PaymentInstructions is null)
        {
            string? derived = code ?? (debit is not null ? PaymentMeansCode.SepaDirectDebit : card is not null ? PaymentMeansCode.BankCard : null);
            if (derived is null)
            {
                if (text is not null) warn?.Invoke("payment means text (BT-82) without a payment means; not written.");
                return;
            }
            inv.PaymentInstructions = new PaymentInstructions
            {
                MeansCode = derived,
                MeansText = text,
                RemittanceInformation = pick(InvoiceFieldAliases.RemittanceInformation),
            };
        }
        else if (code is not null) inv.PaymentInstructions.MeansCode = code;
        inv.PaymentInstructions.Card = card;
        inv.PaymentInstructions.DirectDebit = debit;
    }

    private static decimal AmountOrPercentOfBase(decimal amount, decimal baseAmount, decimal percent)
        => amount != 0m || baseAmount == 0m || percent == 0m ? amount : Round2(baseAmount * percent / 100m);

    /// <summary>VAT of a document level allowance or charge: its own category and rate when given, else the header's.</summary>
    private static (VatCategory Category, decimal? Rate) DocumentVat(string? categoryText, string? rateText, AxDocument data, Action<string>? warn)
    {
        decimal rate = OptionalDecimal(rateText, "allowance or charge VAT rate", warn) ?? data.VatRate;
        var category = ResolveCategory(categoryText ?? data.VatCategory, rate, warn);
        return (category, category == VatCategory.NotSubjectToVat ? null : rate);
    }

    private static void AddDocumentAllowance(Invoice inv, Func<Term, string?> pick, AxDocument data, bool absolute, Action<string>? warn)
    {
        decimal baseAmount = Decimal(pick(InvoiceFieldAliases.DocumentAllowanceBase), "header discount base amount", warn);
        decimal percent = Decimal(pick(InvoiceFieldAliases.DocumentAllowancePercentage), "header discount percentage", warn);
        decimal amount = AmountOrPercentOfBase(Decimal(pick(InvoiceFieldAliases.DocumentAllowanceAmount), "header discount", warn), baseAmount, percent);
        if (amount == 0m) return;
        var (category, rate) = DocumentVat(pick(InvoiceFieldAliases.DocumentAllowanceVatCategory), pick(InvoiceFieldAliases.DocumentAllowanceVatRate), data, warn);
        inv.AllowancesAndCharges.Add(new AllowanceCharge
        {
            IsCharge = !absolute && amount < 0m,
            Amount = Math.Abs(amount),
            BaseAmount = baseAmount != 0m ? Math.Abs(baseAmount) : null,
            Percentage = percent != 0m ? Math.Abs(percent) : null,
            Reason = pick(InvoiceFieldAliases.DocumentAllowanceReason) ?? (pick(InvoiceFieldAliases.DocumentAllowanceReasonCode) is null ? "Rabatt" : null),
            ReasonCode = pick(InvoiceFieldAliases.DocumentAllowanceReasonCode),
            VatCategory = category,
            VatRate = rate,
        });
    }

    private static void AddListedAllowancesCharges(Invoice inv, AxDocument data, bool absolute, Action<string>? warn, List<string> unusedHeader)
    {
        var group = InvoiceFieldAliases.AllowancesCharges;
        foreach (var (key, position, entry) in Entries(group, data.Extra, warn, unusedHeader))
        {
            var (found, unused) = InvoiceFieldAliases.ReadEntry(group, entry);
            ReportUnused(key, unused, unusedHeader);
            string where = $"'{key}' entry {position}";
            decimal baseAmount = Decimal(Value(found, InvoiceFieldAliases.DocumentAllowanceBase), where + " base amount", warn);
            decimal percent = Decimal(Value(found, InvoiceFieldAliases.DocumentAllowancePercentage), where + " percentage", warn);

            // The sign of "amount" decides unless the entry says what it is: isCharge, or an allowanceAmount / chargeAmount key.
            bool? isCharge = Value(found, InvoiceFieldAliases.EntryIsCharge) is { } flag
                ? flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1" : null;
            decimal amount;
            if (Value(found, InvoiceFieldAliases.DocumentChargeAmount) is { } chargeText)
            {
                amount = Decimal(chargeText, where + " charge amount", warn);
                isCharge ??= true;
            }
            else if (Value(found, InvoiceFieldAliases.DocumentAllowanceAmount) is { } allowanceText)
            {
                amount = Decimal(allowanceText, where + " allowance amount", warn);
                isCharge ??= false;
            }
            else amount = Decimal(Value(found, InvoiceFieldAliases.EntryAmount), where + " amount", warn);
            amount = AmountOrPercentOfBase(amount, baseAmount, percent);
            if (amount == 0m)
            {
                warn?.Invoke($"{where} has no amount and no base amount with percentage; skipped.");
                continue;
            }
            // Like chargeTotalAmount: a document with absolute values keeps the sign in BT-3, on a plain invoice a
            // negative charge is an allowance and the other way round.
            bool charge = absolute ? isCharge ?? true : isCharge is { } given ? (amount < 0m ? !given : given) : amount > 0m;

            string? reason = Value(found, InvoiceFieldAliases.DocumentAllowanceReason);
            string? reasonCode = Value(found, InvoiceFieldAliases.DocumentAllowanceReasonCode);
            var (category, rate) = DocumentVat(Value(found, InvoiceFieldAliases.DocumentAllowanceVatCategory),
                Value(found, InvoiceFieldAliases.DocumentAllowanceVatRate), data, warn);
            inv.AllowancesAndCharges.Add(new AllowanceCharge
            {
                IsCharge = charge,
                Amount = Math.Abs(amount),
                BaseAmount = baseAmount != 0m ? Math.Abs(baseAmount) : null,
                Percentage = percent != 0m ? Math.Abs(percent) : null,
                Reason = reason ?? (reasonCode is null ? (charge ? "Zuschlag" : "Rabatt") : null),
                ReasonCode = reasonCode,
                VatCategory = category,
                VatRate = rate,
            });
        }
    }

    private static void AddSupportingDocuments(Invoice inv, IReadOnlyDictionary<string, JsonElement> extra, Func<Term, string?> pick,
        string? folder, Action<string>? warn, List<string> unusedHeader)
    {
        var flat = new Dictionary<string, Found>(StringComparer.Ordinal);
        foreach (var term in new[]
                 {
                     InvoiceFieldAliases.SupportingDocumentReference, InvoiceFieldAliases.SupportingDocumentDescription,
                     InvoiceFieldAliases.SupportingDocumentLocation, InvoiceFieldAliases.SupportingDocumentContent,
                     InvoiceFieldAliases.SupportingDocumentMimeCode, InvoiceFieldAliases.SupportingDocumentFileName,
                 })
            if (pick(term) is { } value) flat[term.Bt] = new Found(term, value, term.Bt);
        if (flat.Count > 0) AddSupportingDocument(inv, flat, "supporting document", folder, warn);

        foreach (var (key, position, entry) in Entries(InvoiceFieldAliases.SupportingDocuments, extra, warn, unusedHeader))
        {
            var (found, unused) = InvoiceFieldAliases.ReadEntry(InvoiceFieldAliases.SupportingDocuments, entry);
            ReportUnused(key, unused, unusedHeader);
            AddSupportingDocument(inv, found, $"'{key}' entry {position}", folder, warn);
        }
    }

    private static void AddSupportingDocument(Invoice inv, Dictionary<string, Found> found, string where, string? folder, Action<string>? warn)
    {
        string? fileName = Value(found, InvoiceFieldAliases.SupportingDocumentFileName);
        string? reference = Value(found, InvoiceFieldAliases.SupportingDocumentReference) ?? (fileName is null ? null : Path.GetFileNameWithoutExtension(fileName));
        if (reference is null)
        {
            warn?.Invoke($"{where} has no reference (BT-122); skipped.");
            return;
        }
        var document = new SupportingDocument
        {
            Reference = reference,
            Description = Value(found, InvoiceFieldAliases.SupportingDocumentDescription),
            ExternalLocation = Value(found, InvoiceFieldAliases.SupportingDocumentLocation),
        };
        inv.SupportingDocuments.Add(document);

        string? base64 = Value(found, InvoiceFieldAliases.SupportingDocumentContent);
        if (base64 is null && fileName is null) return;
        string? mime = Value(found, InvoiceFieldAliases.SupportingDocumentMimeCode)
            ?? (fileName is not null && MimeByExtension.TryGetValue(Path.GetExtension(fileName), out var byExtension) ? byExtension : null);
        if (mime is null || !CodeList.IsMimeCode(mime))
        {
            warn?.Invoke($"{where}: attachment type '{mime ?? Path.GetExtension(fileName ?? "")}' is not allowed (pdf, png, jpeg, csv, xlsx, ods); only the reference is written.");
            return;
        }

        byte[]? content = base64 is not null ? FromBase64(base64, where, warn) : ReadAttachment(fileName!, folder, where, warn);
        if (content is null) return;
        if (content.Length > MaxAttachmentBytes)
        {
            warn?.Invoke($"{where}: attachment has {content.Length} bytes, more than {MaxAttachmentBytes}; only the reference is written.");
            return;
        }
        document.Content = content;
        document.MimeCode = mime;
        document.FileName = fileName ?? reference + MimeByExtension.FirstOrDefault(p => p.Value == mime).Key;
    }

    private static byte[]? FromBase64(string value, string where, Action<string>? warn)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException)
        {
            warn?.Invoke($"{where}: attachment content is not base64; only the reference is written.");
            return null;
        }
    }

    /// <summary>A file next to the JSON, by bare name: no folder part, no "..".</summary>
    private static byte[]? ReadAttachment(string fileName, string? folder, string where, Action<string>? warn)
    {
        if (fileName != Path.GetFileName(fileName) || fileName.Contains("..") || fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            warn?.Invoke($"{where}: attachment '{fileName}' must be a file name in the JSON folder, without a path; only the reference is written.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(folder))
        {
            warn?.Invoke($"{where}: attachment '{fileName}' cannot be read, the JSON has no folder; only the reference is written.");
            return null;
        }
        var file = new FileInfo(Path.Combine(folder, fileName));
        if (!file.Exists)
        {
            warn?.Invoke($"{where}: attachment '{fileName}' is not in the JSON folder; only the reference is written.");
            return null;
        }
        if (file.Length > MaxAttachmentBytes)
        {
            warn?.Invoke($"{where}: attachment '{fileName}' has {file.Length} bytes, more than {MaxAttachmentBytes}; only the reference is written.");
            return null;
        }
        return File.ReadAllBytes(file.FullName);
    }

    private static void CompleteLineTerms(InvoiceLine line, IReadOnlyDictionary<string, JsonElement> extra, Func<Term, string?> l,
        Action<string>? warn, ISet<string> unusedLine)
    {
        string name = string.IsNullOrWhiteSpace(line.Item.Name) ? "line" : $"line '{line.Item.Name}'";
        if (l(InvoiceFieldAliases.LineObjectId) is { } objectId)
            line.ObjectIdentifier = new SchemedIdentifier(objectId, l(InvoiceFieldAliases.LineObjectIdScheme));

        var start = DateOrWarn(l(InvoiceFieldAliases.LinePeriodStart), name + " period start (BT-134)", warn);
        var end = DateOrWarn(l(InvoiceFieldAliases.LinePeriodEnd), name + " period end (BT-135)", warn);
        if (start is not null || end is not null) line.Period = new Period(start ?? line.Period?.Start, end ?? line.Period?.End);

        if (OptionalDecimal(l(InvoiceFieldAliases.GrossPrice), name + " gross price", warn) is { } gross) line.GrossPrice = Math.Abs(gross);
        if (OptionalDecimal(l(InvoiceFieldAliases.PriceDiscount), name + " price discount", warn) is { } discount && discount != 0m)
        {
            if (line.GrossPrice is null) warn?.Invoke($"{name}: price discount (BT-147) needs the gross price (BT-148); not written.");
            else line.PriceDiscount = Math.Abs(discount);
        }
        if (l(InvoiceFieldAliases.PriceBaseQuantityUnit) is { } baseUnit) line.PriceBaseQuantityUnitCode = ResolveUnit(baseUnit, warn);

        // BT-158: the generic code with its scheme, the customs tariff number as HS, then the listed classifications.
        if (l(InvoiceFieldAliases.ClassificationCode) is { } classCode)
            AddClassification(line, classCode, l(InvoiceFieldAliases.ClassificationScheme), l(InvoiceFieldAliases.ClassificationVersion), name, warn);
        if (l(InvoiceFieldAliases.TariffCode) is { } tariff)
            AddClassification(line, new string(tariff.Where(c => !char.IsWhiteSpace(c)).ToArray()), HsScheme, null, name, warn);
        foreach (var (key, position, entry) in Entries(InvoiceFieldAliases.ItemClassifications, extra, warn, unusedLine))
        {
            var (found, unused) = InvoiceFieldAliases.ReadEntry(InvoiceFieldAliases.ItemClassifications, entry);
            ReportUnused(key, unused, unusedLine);
            if (Value(found, InvoiceFieldAliases.ClassificationCode) is { } code)
                AddClassification(line, code, Value(found, InvoiceFieldAliases.ClassificationScheme), Value(found, InvoiceFieldAliases.ClassificationVersion), name, warn);
            else warn?.Invoke($"{name}: '{key}' entry {position} has no code (BT-158); skipped.");
        }

        // BG-32: "name: value" lines, the batch number as "Charge", a single name/value pair, then the listed attributes.
        if (l(InvoiceFieldAliases.AttributesText) is { } attributeLines)
            foreach (var raw in attributeLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = raw.IndexOf(':');
                string attributeName = colon > 0 ? raw[..colon].Trim() : "";
                string attributeValue = colon > 0 ? raw[(colon + 1)..].Trim() : "";
                if (attributeName.Length == 0 || attributeValue.Length == 0)
                    warn?.Invoke($"{name}: item attribute '{raw}' is not 'name: value'; skipped.");
                else AddAttribute(line, attributeName, attributeValue);
            }
        if (l(InvoiceFieldAliases.BatchNumber) is { } batch) AddAttribute(line, BatchAttributeName, batch);
        string? singleName = l(InvoiceFieldAliases.AttributeName);
        string? singleValue = l(InvoiceFieldAliases.AttributeValue);
        if (singleName is not null && singleValue is not null) AddAttribute(line, singleName, singleValue);
        else if (singleName is not null || singleValue is not null)
            warn?.Invoke($"{name}: item attribute needs both name (BT-160) and value (BT-161); skipped.");
        foreach (var (key, position, entry) in Entries(InvoiceFieldAliases.ItemAttributes, extra, warn, unusedLine))
        {
            var (found, unused) = InvoiceFieldAliases.ReadEntry(InvoiceFieldAliases.ItemAttributes, entry);
            ReportUnused(key, unused, unusedLine);
            string? attributeName = Value(found, InvoiceFieldAliases.AttributeName);
            string? attributeValue = Value(found, InvoiceFieldAliases.AttributeValue);
            if (attributeName is null || attributeValue is null)
                warn?.Invoke($"{name}: '{key}' entry {position} needs both name (BT-160) and value (BT-161); skipped.");
            else AddAttribute(line, attributeName, attributeValue);
        }
    }

    private static void AddClassification(InvoiceLine line, string code, string? scheme, string? version, string name, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            warn?.Invoke($"{name}: item classification '{code}' has no scheme (BT-158-1, e.g. HS); skipped.");
            return;
        }
        if (line.Item.Classifications.Any(c => c.Code == code && c.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))) return;
        line.Item.Classifications.Add(new ItemClassification { Code = code, Scheme = scheme.Trim().ToUpperInvariant(), SchemeVersion = version });
    }

    private static void AddAttribute(InvoiceLine line, string name, string value)
    {
        if (line.Item.Attributes.Any(a => a.Name == name && a.Value == value)) return;
        line.Item.Attributes.Add(new ItemAttribute { Name = name, Value = value });
    }

    /// <summary>The entries of a group's lists; the list keys leave the "not used" report, entries that are not objects are warned about.</summary>
    private static List<(string Key, int Position, Dictionary<string, JsonElement> Entry)> Entries(InvoiceFieldAliases.Group group,
        IReadOnlyDictionary<string, JsonElement>? extra, Action<string>? warn, ICollection<string> unused)
    {
        if (extra is not null)
            foreach (var key in extra.Keys.Where(group.IsListKey).ToList()) unused.Remove(key);
        return InvoiceFieldAliases.ListEntries(group, extra, warn);
    }

    private static void ReportUnused(string key, IEnumerable<string> unused, ICollection<string> into)
    {
        foreach (var k in unused) into.Add(key + "." + k);
    }

    private static string? Value(Dictionary<string, Found> found, Term term) => found.TryGetValue(term.Bt, out var f) ? f.Value : null;

    private static decimal? OptionalDecimal(string? s, string what, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TolerantDecimalConverter.TryParseNumber(s, out var d)) return d;
        warn?.Invoke($"{what} '{s.Trim()}' is not a number; ignored.");
        return null;
    }

    private static DateTime? DateOrWarn(string? s, string what, Action<string>? warn)
    {
        if (s is null) return null;
        var date = ParseDate(s);
        if (date is null) warn?.Invoke($"{what} '{s}' is not a recognizable date; not written.");
        return date;
    }
}