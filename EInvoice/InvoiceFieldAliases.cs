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
    public static readonly Term SellerContactName = new("BT-41", "Seller contact point");
    public static readonly Term BuyerElectronicAddress = new("BT-49", "Buyer electronic address");
    // No EN 16931 term: the CII project name next to BT-11 (SpecifiedProcuringProject/Name).
    public static readonly Term ProjectName = new("BT-11 name", "Project name");
    public static readonly Term ContractReference = new("BT-12", "Contract reference");
    public static readonly Term PurchaseOrderReference = new("BT-13", "Purchase order reference");
    public static readonly Term SalesOrderReference = new("BT-14", "Sales order reference");
    public static readonly Term ReceivingAdviceReference = new("BT-15", "Receiving advice reference");
    public static readonly Term DespatchAdviceReference = new("BT-16", "Despatch advice reference");
    public static readonly Term TenderOrLotReference = new("BT-17", "Tender or lot reference");
    public static readonly Term BuyerAccountingReference = new("BT-19", "Buyer accounting reference");
    public static readonly Term PrecedingInvoiceNumber = new("BT-25", "Preceding invoice reference");
    public static readonly Term PrecedingInvoiceDate = new("BT-26", "Preceding invoice issue date");
    public static readonly Term DocumentChargeAmount = new("BT-99", "Document level charge amount");
    public static readonly Term DocumentChargeReason = new("BT-104", "Document level charge reason");
    public static readonly Term PrepaidAmount = new("BT-113", "Paid amount");
    public static readonly Term ShipToName = new("BT-70", "Deliver to party name");
    public static readonly Term DeliveryDate = new("BT-72", "Actual delivery date");
    public static readonly Term ShipToStreet = new("BT-75", "Deliver to address line 1");
    public static readonly Term ShipToCity = new("BT-77", "Deliver to city");
    public static readonly Term ShipToPostcode = new("BT-78", "Deliver to post code");
    public static readonly Term ShipToCountry = new("BT-80", "Deliver to country code");
    public static readonly Term RemittanceInformation = new("BT-83", "Remittance information");
    public static readonly Term AccountIdentifier = new("BT-84", "Payment account identifier");
    public static readonly Term AccountName = new("BT-85", "Payment account name");
    public static readonly Term Bic = new("BT-86", "Payment service provider identifier (BIC)");

    // ── line level ──────────────────────────────────────────────────────────
    public static readonly Term LineNote = new("BT-127", "Invoice line note");
    public static readonly Term LineAllowanceAmount = new("BT-136", "Invoice line allowance amount");
    public static readonly Term LineAllowanceReason = new("BT-139", "Invoice line allowance reason");
    public static readonly Term LineChargeAmount = new("BT-141", "Invoice line charge amount");
    public static readonly Term LineChargeReason = new("BT-144", "Invoice line charge reason");
    public static readonly Term PricePerBaseQuantity = new("BT-146", "Item net price per base quantity");
    public static readonly Term PriceBaseQuantity = new("BT-149", "Item price base quantity");
    public static readonly Term OrderLineReference = new("BT-132", "Referenced purchase order line reference");
    public static readonly Term LineAccountingReference = new("BT-133", "Invoice line buyer accounting reference");
    public static readonly Term ItemDescription = new("BT-154", "Item description");
    public static readonly Term SellersItemId = new("BT-155", "Item seller's identifier");
    public static readonly Term BuyersItemId = new("BT-156", "Item buyer's identifier");
    public static readonly Term Gtin = new("BT-157", "Item standard identifier (GTIN)");
    public static readonly Term OriginCountry = new("BT-159", "Item country of origin");

    // ── document level, 1.15.0: the rest of EN 16931 ─────────────────────────
    public static readonly Term TaxCurrency = new("BT-6", "VAT accounting currency code");
    public static readonly Term TaxPointDate = new("BT-7", "Value added tax point date");
    public static readonly Term TaxPointDateCode = new("BT-8", "Value added tax point date code");
    public static readonly Term InvoicedObject = new("BT-18", "Invoiced object identifier");
    public static readonly Term InvoicedObjectScheme = new("BT-18-1", "Invoiced object identifier scheme");
    public static readonly Term NoteSubjectCode = new("BT-21", "Invoice note subject code");
    public static readonly Term InvoiceNote = new("BT-22", "Invoice note");
    // No EN 16931 term of their own: written together as one BT-22 note.
    public static readonly Term DeliveryTermsText = new("BT-22 delivery terms", "Delivery terms (invoice note)");
    public static readonly Term DeliveryTermsCode = new("BT-22 delivery terms code", "Delivery terms code (invoice note)");
    public static readonly Term DeliveryMode = new("BT-22 delivery mode", "Shipping method (invoice note)");
    public static readonly Term SellerTradingName = new("BT-28", "Seller trading name");
    public static readonly Term SellerIdScheme = new("BT-29-1", "Seller identifier scheme");
    public static readonly Term SellerLegalId = new("BT-30", "Seller legal registration identifier");
    public static readonly Term SellerLegalIdScheme = new("BT-30-1", "Seller legal registration identifier scheme");
    public static readonly Term SellerAdditionalLegalInfo = new("BT-33", "Seller additional legal information");
    public static readonly Term SellerElectronicAddress = new("BT-34", "Seller electronic address");
    public static readonly Term SellerElectronicAddressScheme = new("BT-34-1", "Seller electronic address scheme");
    public static readonly Term SellerAddressLine2 = new("BT-36", "Seller address line 2");
    public static readonly Term SellerAddressLine3 = new("BT-162", "Seller address line 3");
    public static readonly Term SellerSubdivision = new("BT-39", "Seller country subdivision");
    public static readonly Term SellerContactDepartment = new("BT-41-0", "Seller contact department");
    public static readonly Term SellerContactPhone = new("BT-42", "Seller contact telephone number");
    public static readonly Term SellerContactEmail = new("BT-43", "Seller contact email address");
    public static readonly Term BuyerTradingName = new("BT-45", "Buyer trading name");
    public static readonly Term BuyerIdScheme = new("BT-46-1", "Buyer identifier scheme");
    public static readonly Term BuyerLegalId = new("BT-47", "Buyer legal registration identifier");
    public static readonly Term BuyerLegalIdScheme = new("BT-47-1", "Buyer legal registration identifier scheme");
    public static readonly Term BuyerElectronicAddressScheme = new("BT-49-1", "Buyer electronic address scheme");
    public static readonly Term BuyerAddressLine2 = new("BT-51", "Buyer address line 2");
    public static readonly Term BuyerAddressLine3 = new("BT-163", "Buyer address line 3");
    public static readonly Term BuyerSubdivision = new("BT-54", "Buyer country subdivision");
    public static readonly Term BuyerContactName = new("BT-56", "Buyer contact point");
    public static readonly Term BuyerContactDepartment = new("BT-56-0", "Buyer contact department");
    public static readonly Term BuyerContactPhone = new("BT-57", "Buyer contact telephone number");
    public static readonly Term BuyerContactEmail = new("BT-58", "Buyer contact email address");
    public static readonly Term PayeeName = new("BT-59", "Payee name");
    public static readonly Term PayeeId = new("BT-60", "Payee identifier");
    public static readonly Term PayeeIdScheme = new("BT-60-1", "Payee identifier scheme");
    public static readonly Term PayeeLegalId = new("BT-61", "Payee legal registration identifier");
    public static readonly Term PayeeLegalIdScheme = new("BT-61-1", "Payee legal registration identifier scheme");
    public static readonly Term TaxRepName = new("BT-62", "Seller tax representative name");
    public static readonly Term TaxRepVatId = new("BT-63", "Seller tax representative VAT identifier");
    public static readonly Term TaxRepAddressLine1 = new("BT-64", "Tax representative address line 1");
    public static readonly Term TaxRepAddressLine2 = new("BT-65", "Tax representative address line 2");
    public static readonly Term TaxRepAddressLine3 = new("BT-164", "Tax representative address line 3");
    public static readonly Term TaxRepCity = new("BT-66", "Tax representative city");
    public static readonly Term TaxRepPostcode = new("BT-67", "Tax representative post code");
    public static readonly Term TaxRepSubdivision = new("BT-68", "Tax representative country subdivision");
    public static readonly Term TaxRepCountry = new("BT-69", "Tax representative country code");
    public static readonly Term ShipToLocationId = new("BT-71", "Deliver to location identifier");
    public static readonly Term ShipToLocationScheme = new("BT-71-1", "Deliver to location identifier scheme");
    public static readonly Term ShipToGln = new("BT-71-0", "Deliver to location global identifier (GLN)");
    public static readonly Term InvoicingPeriodStart = new("BT-73", "Invoicing period start date");
    public static readonly Term InvoicingPeriodEnd = new("BT-74", "Invoicing period end date");
    public static readonly Term ShipToAddressLine2 = new("BT-76", "Deliver to address line 2");
    public static readonly Term ShipToAddressLine3 = new("BT-165", "Deliver to address line 3");
    public static readonly Term ShipToSubdivision = new("BT-79", "Deliver to country subdivision");
    public static readonly Term PaymentMeansCode = new("BT-81", "Payment means type code");
    public static readonly Term PaymentMeansText = new("BT-82", "Payment means text");
    public static readonly Term CardNumber = new("BT-87", "Payment card primary account number");
    public static readonly Term CardHolder = new("BT-88", "Payment card holder name");
    public static readonly Term MandateReference = new("BT-89", "Mandate reference identifier");
    public static readonly Term CreditorId = new("BT-90", "Bank assigned creditor identifier");
    public static readonly Term DebitedAccount = new("BT-91", "Debited account identifier");
    public static readonly Term DocumentAllowanceAmount = new("BT-92", "Document level allowance amount");
    public static readonly Term DocumentAllowanceBase = new("BT-93", "Document level allowance base amount");
    public static readonly Term DocumentAllowancePercentage = new("BT-94", "Document level allowance percentage");
    public static readonly Term DocumentAllowanceVatCategory = new("BT-95", "Document level allowance VAT category code");
    public static readonly Term DocumentAllowanceVatRate = new("BT-96", "Document level allowance VAT rate");
    public static readonly Term DocumentAllowanceReason = new("BT-97", "Document level allowance reason");
    public static readonly Term DocumentAllowanceReasonCode = new("BT-98", "Document level allowance reason code");
    public static readonly Term DocumentChargeBase = new("BT-100", "Document level charge base amount");
    public static readonly Term DocumentChargePercentage = new("BT-101", "Document level charge percentage");
    public static readonly Term DocumentChargeVatCategory = new("BT-102", "Document level charge VAT category code");
    public static readonly Term DocumentChargeVatRate = new("BT-103", "Document level charge VAT rate");
    public static readonly Term DocumentChargeReasonCode = new("BT-105", "Document level charge reason code");
    public static readonly Term VatTotalInTaxCurrency = new("BT-111", "Invoice total VAT amount in accounting currency");
    public static readonly Term RoundingAmount = new("BT-114", "Rounding amount");
    public static readonly Term VatExemptionReasonCode = new("BT-121", "VAT exemption reason code");
    public static readonly Term SupportingDocumentReference = new("BT-122", "Supporting document reference");
    public static readonly Term SupportingDocumentDescription = new("BT-123", "Supporting document description");
    public static readonly Term SupportingDocumentLocation = new("BT-124", "External document location");
    public static readonly Term SupportingDocumentContent = new("BT-125", "Attached document");
    public static readonly Term SupportingDocumentMimeCode = new("BT-125-1", "Attached document MIME code");
    public static readonly Term SupportingDocumentFileName = new("BT-125-2", "Attached document file name");

    // ── line level, 1.15.0 ──────────────────────────────────────────────────
    public static readonly Term LineObjectId = new("BT-128", "Invoice line object identifier");
    public static readonly Term LineObjectIdScheme = new("BT-128-1", "Invoice line object identifier scheme");
    public static readonly Term LinePeriodStart = new("BT-134", "Invoice line period start date");
    public static readonly Term LinePeriodEnd = new("BT-135", "Invoice line period end date");
    public static readonly Term LineAllowanceBase = new("BT-137", "Invoice line allowance base amount");
    public static readonly Term LineAllowancePercentage = new("BT-138", "Invoice line allowance percentage");
    public static readonly Term LineAllowanceReasonCode = new("BT-140", "Invoice line allowance reason code");
    public static readonly Term LineChargeBase = new("BT-142", "Invoice line charge base amount");
    public static readonly Term LineChargePercentage = new("BT-143", "Invoice line charge percentage");
    public static readonly Term LineChargeReasonCode = new("BT-145", "Invoice line charge reason code");
    public static readonly Term PriceDiscount = new("BT-147", "Item price discount");
    public static readonly Term GrossPrice = new("BT-148", "Item gross price");
    public static readonly Term PriceBaseQuantityUnit = new("BT-150", "Item price base quantity unit of measure code");
    public static readonly Term GtinScheme = new("BT-157-1", "Item standard identifier scheme");
    public static readonly Term ClassificationCode = new("BT-158", "Item classification identifier");
    public static readonly Term ClassificationScheme = new("BT-158-1", "Item classification scheme");
    public static readonly Term ClassificationVersion = new("BT-158-2", "Item classification scheme version");
    // Shortcuts: a customs tariff number is BT-158 with scheme HS, a batch is a BG-32 attribute named "Charge".
    public static readonly Term TariffCode = new("BT-158 HS", "Customs tariff number (classification HS)");
    public static readonly Term AttributeName = new("BT-160", "Item attribute name");
    public static readonly Term AttributeValue = new("BT-161", "Item attribute value");
    public static readonly Term BatchNumber = new("BT-160 Charge", "Batch number (item attribute Charge)");
    public static readonly Term AttributesText = new("BG-32 text", "Item attributes, one 'name: value' per line");

    // ── entries of a JSON list ──────────────────────────────────────────────
    public static readonly Term EntryAmount = new("amount", "Allowance or charge amount (sign decides)");
    public static readonly Term EntryIsCharge = new("isCharge", "Allowance or charge indicator");

    private static readonly string[] AccountNameKeys = { "bankAccountHolder", "accountName", "kontoinhaber", "BT-85" };
    private static readonly string[] BicKeys = { "bankSwift", "bic", "swift", "swiftCode", "BT-86" };

    private static readonly Dictionary<string, Term> Header = Build(new()
    {
        [BuyerReference] = new[] { "buyerReference", "leitwegId", "leitweg", "käuferreferenz", "kaeuferreferenz", "BT-10" },
        [ProjectReference] = new[] { "projectReference", "projectNumber", "projektnummer", "projektreferenz", "BT-11" },
        [ProjectName] = new[] { "projectName", "projektname", "projektbezeichnung" },
        [BuyerElectronicAddress] = new[] { "buyerElectronicAddress", "buyerEndpoint", "eRechnungsadresse", "BT-49" },
        [SellerContactName] = new[] { "sellerContactName", "senderContactName", "contactPerson", "ansprechpartner", "BT-41" },
        [DocumentChargeAmount] = new[] { "chargeTotalAmount", "headerChargeAmount", "documentChargeAmount", "markupTotalAmount", "zuschlaege", "zuschläge", "BT-99" },
        [DocumentChargeReason] = new[] { "chargeReason", "headerChargeReason", "documentChargeReason", "BT-104" },
        [PrepaidAmount] = new[] { "prepaidAmount", "paidAmount", "totalPrepaidAmount", "anzahlungen", "geleisteteZahlungen", "BT-113" },
        [ContractReference] = new[] { "contractReference", "contractNumber", "vertragsnummer", "BT-12" },
        [PurchaseOrderReference] = new[] { "purchase order reference", "purchaseOrderReference", "purchaseOrderNumber", "purchaseOrderId", "purchOrderReference", "purchOrderRef", "purchOrderFormNum", "buyerOrderReference", "customerOrderNumber", "customerPurchaseOrder", "customerRequisition", "bestellnummer", "kundenbestellnummer", "BT-13" },
        [SalesOrderReference] = new[] { "orderReference", "salesOrderReference", "salesOrderNumber", "salesId", "auftragsnummer", "BT-14" },
        [ReceivingAdviceReference] = new[] { "receivingAdviceReference", "goodsReceiptNumber", "wareneingangsnummer", "BT-15" },
        [DespatchAdviceReference] = new[] { "despatchAdviceReference", "dispatchAdviceReference", "deliveryNoteNumber", "packingSlipNumber", "packingSlipId", "lieferscheinnummer", "BT-16" },
        [TenderOrLotReference] = new[] { "tenderReference", "lotReference", "tenderOrLotReference", "vergabenummer", "BT-17" },
        [BuyerAccountingReference] = new[] { "buyerAccountingReference", "costCenter", "kostenstelle", "BT-19" },
        [PrecedingInvoiceNumber] = new[] { "precedingInvoiceReference", "precedingInvoiceNumber", "originalInvoiceNumber", "correctedInvoiceNumber", "ursprungsrechnung", "BT-25" },
        [PrecedingInvoiceDate] = new[] { "precedingInvoiceDate", "originalInvoiceDate", "BT-26" },
        [ShipToName] = new[] { "deliveryName", "shipToName", "deliverToName", "lieferanschriftName", "BT-70" },
        [DeliveryDate] = new[] { "deliveryDate", "actualDeliveryDate", "lieferdatum", "leistungsdatum", "BT-72" },
        [ShipToStreet] = new[] { "deliveryStreet", "shipToStreet", "deliverToStreet", "lieferanschriftStrasse", "BT-75" },
        [ShipToCity] = new[] { "deliveryCity", "shipToCity", "deliverToCity", "lieferanschriftOrt", "BT-77" },
        [ShipToPostcode] = new[] { "deliveryPostcode", "deliveryZipCode", "deliveryZip", "shipToPostcode", "lieferanschriftPlz", "BT-78" },
        [ShipToCountry] = new[] { "deliveryCountry", "shipToCountry", "deliverToCountry", "lieferanschriftLand", "BT-80" },
        [RemittanceInformation] = new[] { "remittanceInformation", "paymentReference", "verwendungszweck", "BT-83" },
        [AccountName] = AccountNameKeys,
        [Bic] = BicKeys,
        [TaxCurrency] = new[] { "taxCurrency", "taxCurrencyCode", "vatAccountingCurrency", "vatCurrency", "BT-6" },
        [TaxPointDate] = new[] { "taxPointDate", "vatPointDate", "BT-7" },
        [TaxPointDateCode] = new[] { "taxPointDateCode", "vatPointDateCode", "BT-8" },
        [InvoicedObject] = new[] { "invoicedObjectIdentifier", "invoicedObjectId", "invoicedObject", "BT-18" },
        [InvoicedObjectScheme] = new[] { "invoicedObjectScheme", "invoicedObjectIdentifierScheme", "BT-18-1" },
        [NoteSubjectCode] = new[] { "notesSubjectCode", "noteSubjectCode", "BT-21" },
        [InvoiceNote] = new[] { "invoiceNote", "BT-22" },
        [DeliveryTermsText] = new[] { "deliveryTerms", "deliveryTermsText", "termsOfDelivery", "lieferbedingung", "lieferbedingungen" },
        [DeliveryTermsCode] = new[] { "deliveryTermsCode", "incoterms", "incoterm" },
        [DeliveryMode] = new[] { "deliveryMode", "shippingMethod", "modeOfDelivery", "versandart" },
        [SellerTradingName] = new[] { "senderTradingName", "sellerTradingName", "BT-28" },
        [SellerIdScheme] = new[] { "senderIdScheme", "sellerIdentifierScheme", "BT-29-1" },
        [SellerLegalId] = new[] { "senderLegalRegistrationId", "senderRegistrationNumber", "sellerLegalRegistrationIdentifier", "handelsregisternummer", "BT-30" },
        [SellerLegalIdScheme] = new[] { "senderLegalRegistrationScheme", "sellerLegalRegistrationScheme", "BT-30-1" },
        [SellerAdditionalLegalInfo] = new[] { "senderAdditionalLegalInformation", "sellerAdditionalLegalInformation", "BT-33" },
        [SellerElectronicAddress] = new[] { "senderElectronicAddress", "sellerElectronicAddress", "sellerEndpoint", "BT-34" },
        [SellerElectronicAddressScheme] = new[] { "senderElectronicAddressScheme", "sellerElectronicAddressScheme", "BT-34-1" },
        [SellerAddressLine2] = new[] { "senderStreet2", "senderAddressLine2", "sellerAddressLine2", "BT-36" },
        [SellerAddressLine3] = new[] { "senderStreet3", "senderAddressLine3", "sellerAddressLine3", "BT-162" },
        [SellerSubdivision] = new[] { "senderCountrySubdivision", "senderState", "senderRegion", "sellerCountrySubdivision", "BT-39" },
        [SellerContactDepartment] = new[] { "senderContactDepartment", "sellerContactDepartment", "BT-41-0" },
        [SellerContactPhone] = new[] { "senderContactPhone", "sellerContactPhone", "BT-42" },
        [SellerContactEmail] = new[] { "senderContactEmail", "sellerContactEmail", "BT-43" },
        [BuyerTradingName] = new[] { "recipientTradingName", "buyerTradingName", "BT-45" },
        [BuyerIdScheme] = new[] { "recipientIdScheme", "buyerIdentifierScheme", "BT-46-1" },
        [BuyerLegalId] = new[] { "recipientLegalRegistrationId", "recipientRegistrationNumber", "buyerLegalRegistrationIdentifier", "BT-47" },
        [BuyerLegalIdScheme] = new[] { "recipientLegalRegistrationScheme", "buyerLegalRegistrationScheme", "BT-47-1" },
        [BuyerElectronicAddressScheme] = new[] { "buyerElectronicAddressScheme", "recipientElectronicAddressScheme", "BT-49-1" },
        [BuyerAddressLine2] = new[] { "recipientStreet2", "recipientAddressLine2", "buyerAddressLine2", "BT-51" },
        [BuyerAddressLine3] = new[] { "recipientStreet3", "recipientAddressLine3", "buyerAddressLine3", "BT-163" },
        [BuyerSubdivision] = new[] { "recipientCountrySubdivision", "recipientState", "recipientRegion", "buyerCountrySubdivision", "BT-54" },
        [BuyerContactName] = new[] { "recipientContactName", "buyerContactName", "buyerContactPoint", "BT-56" },
        [BuyerContactDepartment] = new[] { "recipientContactDepartment", "buyerContactDepartment", "BT-56-0" },
        [BuyerContactPhone] = new[] { "recipientContactPhone", "recipientPhone", "buyerContactPhone", "BT-57" },
        [BuyerContactEmail] = new[] { "recipientContactEmail", "buyerContactEmail", "BT-58" },
        [PayeeName] = new[] { "payeeName", "zahlungsempfaenger", "zahlungsempfänger", "BT-59" },
        [PayeeId] = new[] { "payeeIdentifier", "payeeId", "BT-60" },
        [PayeeIdScheme] = new[] { "payeeIdentifierScheme", "payeeIdScheme", "BT-60-1" },
        [PayeeLegalId] = new[] { "payeeLegalRegistrationId", "payeeLegalRegistrationIdentifier", "BT-61" },
        [PayeeLegalIdScheme] = new[] { "payeeLegalRegistrationScheme", "BT-61-1" },
        [TaxRepName] = new[] { "taxRepresentativeName", "fiskalvertreter", "BT-62" },
        [TaxRepVatId] = new[] { "taxRepresentativeVatId", "BT-63" },
        [TaxRepAddressLine1] = new[] { "taxRepresentativeStreet", "taxRepresentativeAddressLine1", "BT-64" },
        [TaxRepAddressLine2] = new[] { "taxRepresentativeStreet2", "taxRepresentativeAddressLine2", "BT-65" },
        [TaxRepAddressLine3] = new[] { "taxRepresentativeStreet3", "taxRepresentativeAddressLine3", "BT-164" },
        [TaxRepCity] = new[] { "taxRepresentativeCity", "BT-66" },
        [TaxRepPostcode] = new[] { "taxRepresentativePostcode", "taxRepresentativeZipCode", "BT-67" },
        [TaxRepSubdivision] = new[] { "taxRepresentativeCountrySubdivision", "taxRepresentativeState", "BT-68" },
        [TaxRepCountry] = new[] { "taxRepresentativeCountry", "BT-69" },
        [ShipToLocationId] = new[] { "deliveryLocationId", "deliveryLocationIdentifier", "shipToLocationId", "BT-71" },
        [ShipToLocationScheme] = new[] { "deliveryLocationScheme", "deliveryLocationIdentifierScheme", "BT-71-1" },
        [ShipToGln] = new[] { "deliveryGln", "shipToGln", "deliverToGln", "BT-71-0" },
        [InvoicingPeriodStart] = new[] { "invoicingPeriodStart", "invoicingPeriodFrom", "billingPeriodStart", "abrechnungszeitraumVon", "BT-73" },
        [InvoicingPeriodEnd] = new[] { "invoicingPeriodEnd", "invoicingPeriodTo", "billingPeriodEnd", "abrechnungszeitraumBis", "BT-74" },
        [ShipToAddressLine2] = new[] { "deliveryStreet2", "deliveryAddressLine2", "shipToAddressLine2", "BT-76" },
        [ShipToAddressLine3] = new[] { "deliveryStreet3", "deliveryAddressLine3", "shipToAddressLine3", "BT-165" },
        [ShipToSubdivision] = new[] { "deliveryCountrySubdivision", "deliveryState", "deliveryRegion", "shipToCountrySubdivision", "BT-79" },
        [PaymentMeansCode] = new[] { "paymentMeansCode", "paymentMeansTypeCode", "BT-81" },
        [PaymentMeansText] = new[] { "paymentMeansText", "paymentMethod", "zahlungsart", "BT-82" },
        [CardNumber] = new[] { "paymentCardNumber", "cardNumber", "cardPrimaryAccountNumber", "BT-87" },
        [CardHolder] = new[] { "paymentCardHolder", "cardHolderName", "BT-88" },
        [MandateReference] = new[] { "mandateReference", "sepaMandateReference", "mandatsreferenz", "BT-89" },
        [CreditorId] = new[] { "creditorIdentifier", "creditorId", "sepaCreditorId", "glaeubigerId", "gläubigerId", "BT-90" },
        [DebitedAccount] = new[] { "debitedAccount", "debitedAccountIdentifier", "debitedIban", "BT-91" },
        [DocumentAllowanceAmount] = new[] { "documentAllowanceAmount", "headerAllowanceAmount", "documentDiscountAmount", "headerDiscountAmount", "BT-92" },
        [DocumentAllowanceBase] = new[] { "documentAllowanceBaseAmount", "headerDiscountBaseAmount", "BT-93" },
        [DocumentAllowancePercentage] = new[] { "documentAllowancePercentage", "documentDiscountPercent", "headerDiscountPercent", "BT-94" },
        [DocumentAllowanceVatCategory] = new[] { "documentAllowanceVatCategory", "BT-95" },
        [DocumentAllowanceVatRate] = new[] { "documentAllowanceVatRate", "BT-96" },
        [DocumentAllowanceReason] = new[] { "documentAllowanceReason", "documentDiscountReason", "headerDiscountReason", "BT-97" },
        [DocumentAllowanceReasonCode] = new[] { "documentAllowanceReasonCode", "headerDiscountReasonCode", "BT-98" },
        [DocumentChargeBase] = new[] { "documentChargeBaseAmount", "headerChargeBaseAmount", "BT-100" },
        [DocumentChargePercentage] = new[] { "documentChargePercentage", "headerChargePercent", "BT-101" },
        [DocumentChargeVatCategory] = new[] { "documentChargeVatCategory", "BT-102" },
        [DocumentChargeVatRate] = new[] { "documentChargeVatRate", "BT-103" },
        [DocumentChargeReasonCode] = new[] { "documentChargeReasonCode", "chargeReasonCode", "headerChargeReasonCode", "BT-105" },
        [VatTotalInTaxCurrency] = new[] { "taxAmountInTaxCurrency", "vatTotalInTaxCurrency", "vatTotalInAccountingCurrency", "BT-111" },
        [RoundingAmount] = new[] { "roundingAmount", "rundungsbetrag", "BT-114" },
        [VatExemptionReasonCode] = new[] { "vatExemptionReasonCode", "vatexCode", "BT-121" },
        [SupportingDocumentReference] = new[] { "supportingDocumentReference", "attachmentReference", "BT-122" },
        [SupportingDocumentDescription] = new[] { "supportingDocumentDescription", "attachmentDescription", "BT-123" },
        [SupportingDocumentLocation] = new[] { "supportingDocumentUrl", "externalDocumentLocation", "attachmentUrl", "BT-124" },
        [SupportingDocumentContent] = new[] { "attachmentContent", "attachmentBase64", "BT-125" },
        [SupportingDocumentMimeCode] = new[] { "attachmentMimeCode", "attachmentMimeType", "BT-125-1" },
        [SupportingDocumentFileName] = new[] { "attachmentFileName", "BT-125-2" },
    });

    /// <summary>A repeating group sent as a JSON list of objects, each entry read with its own spellings.</summary>
    public sealed class Group
    {
        internal Group(string id, string[] listKeys, Dictionary<string, Term> entry)
        {
            Id = id;
            ListKeys = listKeys.Select(Normalize).ToHashSet();
            Entry = entry;
        }

        public string Id { get; }
        internal HashSet<string> ListKeys { get; }
        internal Dictionary<string, Term> Entry { get; }

        public bool IsListKey(string key) => ListKeys.Contains(Normalize(key));
    }

    public static readonly Group Notes = new("BG-1", new[] { "invoiceNotes", "notesList", "BG-1" }, Build(new()
    {
        [InvoiceNote] = new[] { "text", "content", "note", "BT-22" },
        [NoteSubjectCode] = new[] { "subjectCode", "BT-21" },
    }));

    public static readonly Group PrecedingInvoices = new("BG-3", new[] { "precedingInvoices", "precedingInvoiceReferences", "BG-3" }, Build(new()
    {
        [PrecedingInvoiceNumber] = new[] { "number", "invoiceNumber", "reference", "BT-25" },
        [PrecedingInvoiceDate] = new[] { "issueDate", "date", "BT-26" },
    }));

    public static readonly Group AllowancesCharges = new("BG-20/BG-21", new[] { "allowancesCharges", "allowanceCharges", "documentAllowancesCharges", "BG-20", "BG-21" }, Build(new()
    {
        [EntryAmount] = new[] { "amount" },
        [EntryIsCharge] = new[] { "isCharge", "chargeIndicator" },
        [DocumentAllowanceAmount] = new[] { "allowanceAmount", "discountAmount", "BT-92" },
        [DocumentChargeAmount] = new[] { "chargeAmount", "BT-99" },
        [DocumentAllowanceBase] = new[] { "baseAmount", "BT-93", "BT-100" },
        [DocumentAllowancePercentage] = new[] { "percentage", "percent", "BT-94", "BT-101" },
        [DocumentAllowanceVatCategory] = new[] { "vatCategory", "BT-95", "BT-102" },
        [DocumentAllowanceVatRate] = new[] { "vatRate", "BT-96", "BT-103" },
        [DocumentAllowanceReason] = new[] { "reason", "BT-97", "BT-104" },
        [DocumentAllowanceReasonCode] = new[] { "reasonCode", "BT-98", "BT-105" },
    }));

    public static readonly Group SupportingDocuments = new("BG-24", new[] { "supportingDocuments", "attachments", "additionalDocuments", "BG-24" }, Build(new()
    {
        [SupportingDocumentReference] = new[] { "reference", "id", "documentReference", "BT-122" },
        [SupportingDocumentDescription] = new[] { "description", "BT-123" },
        [SupportingDocumentLocation] = new[] { "url", "uri", "externalLocation", "BT-124" },
        [SupportingDocumentContent] = new[] { "content", "base64", "contentBase64", "BT-125" },
        [SupportingDocumentMimeCode] = new[] { "mimeCode", "mimeType", "contentType", "BT-125-1" },
        [SupportingDocumentFileName] = new[] { "fileName", "file", "BT-125-2" },
    }));

    public static readonly Group ItemAttributes = new("BG-32", new[] { "itemAttributes", "productCharacteristics", "merkmale", "BG-32" }, Build(new()
    {
        [AttributeName] = new[] { "name", "description", "BT-160" },
        [AttributeValue] = new[] { "value", "BT-161" },
    }));

    public static readonly Group ItemClassifications = new("BT-158 list", new[] { "itemClassifications", "classifications" }, Build(new()
    {
        [ClassificationCode] = new[] { "code", "classCode", "BT-158" },
        [ClassificationScheme] = new[] { "scheme", "schemeId", "listId", "BT-158-1" },
        [ClassificationVersion] = new[] { "schemeVersion", "version", "listVersionId", "BT-158-2" },
    }));

    /// <summary>The terms of one entry of a <see cref="Group"/> list, and the keys that matched no term.</summary>
    public static (Dictionary<string, Found> Found, List<string> Unused) ReadEntry(Group group, IReadOnlyDictionary<string, JsonElement> entry)
        => Read(entry, group.Entry);

    /// <summary>
    /// The entries of every list key of <paramref name="group"/> in <paramref name="extra"/>, in JSON order. A value that is not a
    /// list of objects is reported through <paramref name="warn"/> and skipped.
    /// </summary>
    public static List<(string Key, int Position, Dictionary<string, JsonElement> Entry)> ListEntries(Group group,
        IReadOnlyDictionary<string, JsonElement>? extra, Action<string>? warn)
    {
        var entries = new List<(string, int, Dictionary<string, JsonElement>)>();
        if (extra is null) return entries;
        foreach (var (key, value) in extra)
        {
            if (!group.IsListKey(key) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (value.ValueKind != JsonValueKind.Array)
            {
                if (value.ValueKind != JsonValueKind.String) warn?.Invoke($"'{key}' is not a list ({group.Id}); ignored.");
                continue;
            }
            int position = 0;
            foreach (var item in value.EnumerateArray())
            {
                position++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    warn?.Invoke($"'{key}' entry {position} is not an object ({group.Id}); skipped.");
                    continue;
                }
                var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject()) fields[property.Name] = property.Value.Clone();
                entries.Add((key, position, fields));
            }
        }
        return entries;
    }

    private static readonly Dictionary<string, Term> Account = Build(new()
    {
        [AccountIdentifier] = new[] { "bankIban", "iban", "BT-84" },
        [AccountName] = AccountNameKeys,
        [Bic] = BicKeys,
    });

    private static readonly Dictionary<string, Term> Line = Build(new()
    {
        [LineNote] = new[] { "lineNote", "note", "notiz", "BT-127" },
        [LineAllowanceAmount] = new[] { "lineDiscountAmount", "lineAllowanceAmount", "positionsrabatt", "BT-136" },
        [LineAllowanceReason] = new[] { "lineDiscountReason", "lineAllowanceReason", "BT-139" },
        [LineChargeAmount] = new[] { "lineChargeAmount", "lineSurchargeAmount", "positionszuschlag", "BT-141" },
        [LineChargeReason] = new[] { "lineChargeReason", "lineSurchargeReason", "BT-144" },
        [PricePerBaseQuantity] = new[] { "priceUnitPrice", "pricePerPriceUnit", "pricePerBaseQuantity", "preisProPreiseinheit", "BT-146" },
        [PriceBaseQuantity] = new[] { "priceUnit", "priceBaseQuantity", "basisQuantity", "preiseinheit", "BT-149" },
        [OrderLineReference] = new[] { "orderLineReference", "purchaseOrderLine", "purchaseOrderLineReference", "bestellposition", "BT-132" },
        [LineAccountingReference] = new[] { "lineAccountingReference", "buyerAccountingReference", "costCenter", "kostenstelle", "BT-133" },
        [SellersItemId] = new[] { "itemId", "itemNumber", "sellerItemId", "sellersItemId", "sellersItemIdentifier", "artikelnummer", "BT-155" },
        [BuyersItemId] = new[] { "buyersItemIdentifier", "buyerItemId", "customerItemId", "externalItemId", "kundenartikelnummer", "BT-156" },
        [Gtin] = new[] { "gtin", "ean", "eanCode", "barcode", "BT-157" },
        [OriginCountry] = new[] { "originCountry", "countryOfOrigin", "ursprungsland", "BT-159" },
        [LineObjectId] = new[] { "lineObjectIdentifier", "objectIdentifier", "BT-128" },
        [LineObjectIdScheme] = new[] { "lineObjectIdentifierScheme", "objectIdentifierScheme", "BT-128-1" },
        [LinePeriodStart] = new[] { "linePeriodStart", "lineServicePeriodFrom", "linePeriodFrom", "BT-134" },
        [LinePeriodEnd] = new[] { "linePeriodEnd", "lineServicePeriodTo", "linePeriodTo", "BT-135" },
        [LineAllowanceBase] = new[] { "lineAllowanceBaseAmount", "lineDiscountBaseAmount", "BT-137" },
        [LineAllowancePercentage] = new[] { "lineAllowancePercentage", "lineDiscountPercent", "lineDiscountPercentage", "BT-138" },
        [LineAllowanceReasonCode] = new[] { "lineAllowanceReasonCode", "lineDiscountReasonCode", "BT-140" },
        [LineChargeBase] = new[] { "lineChargeBaseAmount", "lineSurchargeBaseAmount", "BT-142" },
        [LineChargePercentage] = new[] { "lineChargePercentage", "lineChargePercent", "lineSurchargePercent", "BT-143" },
        [LineChargeReasonCode] = new[] { "lineChargeReasonCode", "lineSurchargeReasonCode", "BT-145" },
        [PriceDiscount] = new[] { "itemPriceDiscount", "priceDiscount", "BT-147" },
        [GrossPrice] = new[] { "itemGrossPrice", "grossPrice", "grossUnitPrice", "bruttopreis", "BT-148" },
        [PriceBaseQuantityUnit] = new[] { "priceBaseQuantityUnit", "priceUnitCode", "BT-150" },
        [GtinScheme] = new[] { "itemStandardIdentifierScheme", "gtinScheme", "BT-157-1" },
        [ItemDescription] = new[] { "itemDescription", "longDescription", "positionsbeschreibung", "itemNote", "itemText", "BT-154" },
        [ClassificationCode] = new[] { "itemClassificationCode", "classificationCode", "BT-158" },
        [ClassificationScheme] = new[] { "itemClassificationScheme", "classificationScheme", "BT-158-1" },
        [ClassificationVersion] = new[] { "itemClassificationSchemeVersion", "classificationSchemeVersion", "BT-158-2" },
        [TariffCode] = new[] { "tariffCode", "hsCode", "customsTariffNumber", "commodityCode", "zolltarifnummer", "warennummer" },
        [AttributeName] = new[] { "itemAttributeName", "BT-160" },
        [AttributeValue] = new[] { "itemAttributeValue", "BT-161" },
        [BatchNumber] = new[] { "batchNumber", "batchId", "lotNumber", "chargennummer" },
        [AttributesText] = new[] { "itemAttributes", "productCharacteristics", "merkmale", "BG-32" },
    });

    // Spellings that beat every other key of their term, in this order, whatever the JSON key order.
    // AX 2012 sends its customer purchase order field as purchOrderReference, and a customer field
    // mapping can add purchaseOrderReference next to it; AX writes the keys alphabetically.
    private static readonly Dictionary<string, string[]> Precedence = new()
    {
        [PurchaseOrderReference.Bt] = new[] { "purchOrderReference", "purchaseOrderReference" },
    };

    private static int Rank(Term term, string normalizedKey)
    {
        if (!Precedence.TryGetValue(term.Bt, out var keys)) return int.MaxValue;
        int i = Array.FindIndex(keys, k => Normalize(k) == normalizedKey);
        return i < 0 ? int.MaxValue : i;
    }

    private static Dictionary<string, Term> Build(Dictionary<Term, string[]> spellings)
    {
        var map = new Dictionary<string, Term>(StringComparer.Ordinal);
        foreach (var (term, keys) in spellings)
            foreach (var k in keys)
            {
                string normalized = Normalize(k);
                if (map.TryGetValue(normalized, out var other) && other != term)
                    throw new InvalidOperationException($"JSON key '{k}' is spelled for both {other.Bt} and {term.Bt}.");
                map[normalized] = term;
            }
        return map;
    }

    /// <summary>Every term id a JSON key can fill, at any level and in any list entry.</summary>
    public static IReadOnlySet<string> AllTermIds { get; } = new[] { Header, Line, Account }
        .Concat(new[] { Notes, PrecedingInvoices, AllowancesCharges, SupportingDocuments, ItemAttributes, ItemClassifications }.Select(g => g.Entry))
        .SelectMany(t => t.Values).Select(t => t.Bt).ToHashSet(StringComparer.Ordinal);

    /// <summary>The comparable form of a JSON key: lower case, letters and digits only.</summary>
    public static string Normalize(string key)
        => new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The document level terms present in <paramref name="extra"/>, and the keys that matched no term.</summary>
    public static (Dictionary<string, Found> Found, List<string> Unused) ReadHeader(IReadOnlyDictionary<string, JsonElement>? extra)
        => Read(extra, Header);

    /// <summary>The line level terms present in <paramref name="extra"/>, and the keys that matched no term.</summary>
    public static (Dictionary<string, Found> Found, List<string> Unused) ReadLine(IReadOnlyDictionary<string, JsonElement>? extra)
        => Read(extra, Line);

    /// <summary>The BT-84, BT-85 and BT-86 terms of one entry of the account list, and the keys that matched no term.</summary>
    public static (Dictionary<string, Found> Found, List<string> Unused) ReadAccount(IReadOnlyDictionary<string, JsonElement>? extra)
        => Read(extra, Account);

    private static (Dictionary<string, Found>, List<string>) Read(IReadOnlyDictionary<string, JsonElement>? extra, Dictionary<string, Term> table)
    {
        var found = new Dictionary<string, Found>(StringComparer.Ordinal);
        var unused = new List<string>();
        if (extra is null) return (found, unused);
        foreach (var (key, element) in extra)
        {
            string? value = Text(element);
            string normalized = Normalize(key);
            if (!table.TryGetValue(normalized, out var term))
            {
                unused.Add(key);
                continue;
            }
            // Empty values are not written; the first non-empty spelling wins unless a later one
            // ranks higher in Precedence.
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (found.TryGetValue(term.Bt, out var earlier)
                && Rank(term, Normalize(earlier.SourceKey)) <= Rank(term, normalized)) continue;
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
