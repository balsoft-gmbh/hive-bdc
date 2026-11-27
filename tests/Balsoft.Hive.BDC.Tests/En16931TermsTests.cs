using System.Text;
using System.Xml.Linq;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.Reading;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// 1.15.0: every EN 16931 business term reaches the e-invoice from the ERP JSON. The id list is the term column of the
/// official Factur-X 1.09.2 sheet "EN16931" (ids only; the package text is not redistributed).
/// </summary>
public class En16931TermsTests
{
    private static readonly string[] SheetIds =
    {
        "BT-1", "BT-2", "BT-2-0", "BT-3", "BT-5", "BT-6", "BT-7", "BT-7-0", "BT-8", "BT-9", "BT-9-0", "BT-10",
        "BT-11", "BT-11-0", "BT-12", "BT-13", "BT-14", "BT-15", "BT-16", "BT-17", "BT-17-0", "BT-18", "BT-18-0", "BT-18-1",
        "BT-19", "BT-20", "BT-21", "BT-22", "BT-23", "BT-24", "BT-25", "BT-26", "BT-26-0", "BT-27", "BT-28", "BT-29",
        "BT-29-0", "BT-29-1", "BT-30", "BT-30-1", "BT-31", "BT-31-0", "BT-32", "BT-32-0", "BT-33", "BT-34", "BT-34-1", "BT-35",
        "BT-36", "BT-37", "BT-38", "BT-39", "BT-40", "BT-41", "BT-41-0", "BT-42", "BT-43", "BT-44", "BT-45", "BT-46",
        "BT-46-0", "BT-46-1", "BT-47", "BT-47-1", "BT-48", "BT-48-0", "BT-49", "BT-49-1", "BT-50", "BT-51", "BT-52", "BT-53",
        "BT-54", "BT-55", "BT-56", "BT-56-0", "BT-57", "BT-58", "BT-59", "BT-60", "BT-60-0", "BT-60-1", "BT-61", "BT-61-1",
        "BT-62", "BT-63", "BT-63-0", "BT-64", "BT-65", "BT-66", "BT-67", "BT-68", "BT-69", "BT-70", "BT-71", "BT-71-0",
        "BT-71-1", "BT-72", "BT-72-0", "BT-73", "BT-73-0", "BT-74", "BT-74-0", "BT-75", "BT-76", "BT-77", "BT-78", "BT-79",
        "BT-80", "BT-81", "BT-82", "BT-83", "BT-84", "BT-84-0", "BT-85", "BT-86", "BT-87", "BT-88", "BT-89", "BT-90",
        "BT-91", "BT-92", "BT-93", "BT-94", "BT-95", "BT-95-0", "BT-96", "BT-97", "BT-98", "BT-99", "BT-100", "BT-101",
        "BT-102", "BT-102-0", "BT-103", "BT-104", "BT-105", "BT-106", "BT-107", "BT-108", "BT-109", "BT-110", "BT-110-0", "BT-111",
        "BT-111-0", "BT-112", "BT-113", "BT-114", "BT-115", "BT-116", "BT-117", "BT-118", "BT-118-0", "BT-119", "BT-120", "BT-121",
        "BT-122", "BT-122-0", "BT-123", "BT-124", "BT-125", "BT-125-1", "BT-125-2", "BT-126", "BT-127", "BT-128", "BT-128-0", "BT-128-1",
        "BT-129", "BT-130", "BT-131", "BT-132", "BT-133", "BT-134", "BT-134-0", "BT-135", "BT-135-0", "BT-136", "BT-137", "BT-138",
        "BT-139", "BT-140", "BT-141", "BT-142", "BT-143", "BT-144", "BT-145", "BT-146", "BT-147", "BT-147-01", "BT-147-02", "BT-148",
        "BT-149", "BT-149-1", "BT-150", "BT-150-1", "BT-151", "BT-151-0", "BT-152", "BT-153", "BT-154", "BT-155", "BT-156", "BT-157",
        "BT-157-1", "BT-158", "BT-158-1", "BT-158-2", "BT-159", "BT-160", "BT-161", "BT-162", "BT-163", "BT-164", "BT-165"
    };

    // Terms the mapper fills from a fixed JSON key, computes, or that the library writes next to another term.
    private static readonly Dictionary<string, string> Derived = new()
    {
        ["BT-1"] = "documentNumber / invoiceNumber", ["BT-2"] = "issueDate", ["BT-3"] = "invoiceTypeCode", ["BT-5"] = "currency",
        ["BT-9"] = "dueDate", ["BT-11-0"] = "projectName (term 'BT-11 name')", ["BT-20"] = "paymentTermsText and the cash discounts",
        ["BT-23"] = "constant Peppol billing process", ["BT-24"] = "written by the library per profile",
        ["BT-27"] = "senderName", ["BT-29"] = "senderId", ["BT-29-0"] = "senderGln", ["BT-31"] = "senderVatId", ["BT-32"] = "senderTaxId",
        ["BT-35"] = "senderStreet", ["BT-37"] = "senderCity", ["BT-38"] = "senderPostcode", ["BT-40"] = "senderCountry",
        ["BT-44"] = "recipientName", ["BT-46"] = "recipientId", ["BT-46-0"] = "recipientGln", ["BT-48"] = "recipientVatId",
        ["BT-50"] = "recipientStreet", ["BT-52"] = "recipientCity", ["BT-53"] = "recipientPostcode", ["BT-55"] = "recipientCountry",
        ["BT-60-0"] = "payeeIdentifier with payeeIdentifierScheme 0088", ["BT-84-0"] = "an account that is not an IBAN (payment means 30)",
        ["BT-106"] = "computed", ["BT-107"] = "computed", ["BT-108"] = "computed", ["BT-109"] = "computed", ["BT-110"] = "computed",
        ["BT-112"] = "computed", ["BT-115"] = "computed", ["BT-116"] = "computed", ["BT-117"] = "computed",
        ["BT-118"] = "vatCategory per line and header", ["BT-119"] = "vatRate per line and header", ["BT-120"] = "vatNote",
        ["BT-126"] = "numbered by the library", ["BT-129"] = "quantity", ["BT-130"] = "unit", ["BT-131"] = "computed",
        ["BT-151"] = "line vatCategory", ["BT-152"] = "line vatRate", ["BT-153"] = "description",
        ["BT-2-0"] = "date format, library", ["BT-7-0"] = "date format, library", ["BT-9-0"] = "date format, library",
        ["BT-26-0"] = "date format, library", ["BT-72-0"] = "date format, library", ["BT-73-0"] = "date format, library",
        ["BT-74-0"] = "date format, library", ["BT-134-0"] = "date format, library", ["BT-135-0"] = "date format, library",
        ["BT-31-0"] = "tax scheme VA, library", ["BT-32-0"] = "tax scheme FC, library", ["BT-48-0"] = "tax scheme VA, library",
        ["BT-63-0"] = "tax scheme VA, library", ["BT-95-0"] = "tax type VAT, library", ["BT-102-0"] = "tax type VAT, library",
        ["BT-118-0"] = "tax type VAT, library", ["BT-151-0"] = "tax type VAT, library",
        ["BT-110-0"] = "currency attribute, library", ["BT-111-0"] = "currency attribute, library",
        ["BT-17-0"] = "reference type code 50, library", ["BT-18-0"] = "reference type code 130, library",
        ["BT-122-0"] = "reference type code 916, library", ["BT-128-0"] = "reference type code 130, library",
        ["BT-147-01"] = "gross price allowance structure, library", ["BT-147-02"] = "gross price allowance indicator, library",
        ["BT-149-1"] = "gross price base quantity, library from BT-149", ["BT-150-1"] = "gross price base unit, library from BT-150",
    };

    [Fact]
    public void EveryTermOfTheOfficialSheetIsReadOrDerived()
    {
        var missing = SheetIds.Where(id => !InvoiceFieldAliases.AllTermIds.Contains(id) && !Derived.ContainsKey(id)).ToList();
        Assert.True(missing.Count == 0, "Not read and not derived: " + string.Join(", ", missing));
        Assert.Equal(SheetIds.Length, SheetIds.Distinct().Count());
    }

    [Fact]
    public void ADerivedTermIsNotAlsoReadFromItsOwnKey()
    {
        var both = Derived.Keys.Where(InvoiceFieldAliases.AllTermIds.Contains).ToList();
        Assert.True(both.Count == 0, "Derived and read: " + string.Join(", ", both));
    }

    // Old spellings keep their meaning: a new term must never take a key an export already uses.
    [Theory]
    [InlineData("note", "BT-127")]
    [InlineData("lineNote", "BT-127")]
    [InlineData("itemDescription", "BT-154")]
    [InlineData("priceUnit", "BT-149")]
    [InlineData("chargeReason", "BT-104")]
    public void ExistingLineAndHeaderKeysKeepTheirTerm(string key, string bt)
    {
        var json = $$"""{ "{{key}}": "X" }""";
        var extra = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
        var line = InvoiceFieldAliases.ReadLine(extra).Found;
        var header = InvoiceFieldAliases.ReadHeader(extra).Found;
        Assert.True(line.ContainsKey(bt) || header.ContainsKey(bt), $"{key} no longer maps to {bt}");
    }

    // ── the ERP export that started it: tariff code, attributes, batch, delivery terms, contact mail ──

    private const string TariffExport = """
        {
          "documentType": "SalesInvoice", "eInvoiceType": "ZUGFeRD", "invoiceNumber": "RG-100001", "invoiceTypeCode": "380",
          "issueDate": "2026-11-25", "dueDate": "2027-03-25", "currency": "EUR", "vatRate": "0.00", "vatCategory": "K",
          "vatNote": "Steuerfreie innergem. Lieferung", "buyerReference": "4400000001", "reference": "4400000001",
          "senderName": "Muster Lieferant GmbH", "senderStreet": "Werkstr. 1\n", "senderPostcode": "10115", "senderCity": "Berlin",
          "senderCountry": "DE", "senderVatId": "DE123456789", "senderEmail": "info@example.com", "senderPhone": "030 000",
          "senderContactName": "Muster, Erika", "senderContactEmail": "erika.muster@example.com",
          "recipientName": "Kunde Sp. z o.o.", "recipientStreet": "UL. PRZYKLADOWA 5", "recipientPostcode": "00-001",
          "recipientCity": "WARSZAWA", "recipientCountry": "PL", "recipientVatId": "PL1234567890", "recipientEmail": "ap@example.org",
          "deliveryName": "Kunde Sp. z o.o.", "deliveryStreet": "Ul. Przykladowa 5\nWarehouse 2", "deliveryPostcode": "00-001",
          "deliveryCity": "WARSZAWA", "deliveryCountry": "PL", "deliveryMode": "UPS",
          "deliveryTerms": "DDP, delivered duty paid, incl. packaging", "deliveryTermsCode": "DDP",
          "bankIban": "DE89370400440532013000", "bankSwift": "COBADEFFXXX", "orderReference": "AF-000001",
          "lineItems": [ {
            "description": "Complete Nozzle", "quantity": "100.00", "unit": "Satz", "unitPrice": "1.5600", "vatRate": "0.00", "vatCategory": "K",
            "priceUnit": "1.00", "priceUnitPrice": "1.56", "sellerItemId": "9000000001", "originCountry": "DE",
            "itemNote": "Nozzle, complete, PEEK", "tariffCode": "3926 9097", "batchNumber": "PA-000001",
            "itemAttributes": "Material code: PEEK\nDrawing-No. / Index / Date: 000F0000 / 01 / 08.07.2014"
          } ]
        }
        """;

    [Fact]
    public void TariffCodeAttributesBatchDeliveryTermsAndContactMailReachTheXml()
    {
        var warnings = new List<string>();
        var inv = InvoiceMapper.Map(AxDocument.Parse(TariffExport), warnings.Add);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(inv, InvoiceProfile.EN16931)));
        XNamespace ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";

        var product = xml.Descendants(ram + "SpecifiedTradeProduct").Single();
        Assert.Equal("Nozzle, complete, PEEK", product.Element(ram + "Description")?.Value);
        var classCode = product.Element(ram + "DesignatedProductClassification")!.Element(ram + "ClassCode")!;
        Assert.Equal("39269097", classCode.Value);
        Assert.Equal("HS", classCode.Attribute("listID")?.Value);
        var attributes = product.Elements(ram + "ApplicableProductCharacteristic")
            .Select(c => (c.Element(ram + "Description")!.Value, c.Element(ram + "Value")!.Value)).ToList();
        Assert.Equal(new[]
        {
            ("Material code", "PEEK"),
            ("Drawing-No. / Index / Date", "000F0000 / 01 / 08.07.2014"),
            ("Charge", "PA-000001"),
        }, attributes);

        var notes = xml.Descendants(ram + "IncludedNote").Select(n => n.Element(ram + "Content")!.Value).ToList();
        Assert.Contains("Delivery terms: DDP, delivered duty paid, incl. packaging; Shipping method: UPS", notes);

        var sellerContact = xml.Descendants(ram + "SellerTradeParty").Single().Element(ram + "DefinedTradeContact")!;
        Assert.Equal("erika.muster@example.com", sellerContact.Element(ram + "EmailURIUniversalCommunication")!.Element(ram + "URIID")!.Value);
        Assert.Equal("Muster, Erika", sellerContact.Element(ram + "PersonName")!.Value);
        var sellerEndpoint = xml.Descendants(ram + "SellerTradeParty").Single().Element(ram + "URIUniversalCommunication")!;
        Assert.Equal("info@example.com", sellerEndpoint.Element(ram + "URIID")!.Value);

        var result = EInvoiceGenerator.Preflight(inv, InvoiceProfile.EN16931, warnings.Add);
        Assert.True(result.IsValid, string.Join("\n", result.Issues));
    }

    [Fact]
    public void WithoutAContactMailTheCompanyMailStaysTheContact()
    {
        var inv = InvoiceMapper.Map(AxDocument.Parse(TariffExport.Replace("\"senderContactEmail\": \"erika.muster@example.com\",", "")));
        Assert.Equal("info@example.com", inv.Seller.Contact!.Email);
    }

    // ── every term, written and read back ──

    private const string AllTerms = """
        {
          "documentType": "SalesInvoice", "eInvoiceType": "XRechnung", "invoiceNumber": "RE-2026-0001", "invoiceTypeCode": "380",
          "issueDate": "2026-11-20", "dueDate": "2026-12-20", "currency": "EUR", "vatRate": "19", "vatCategory": "S",
          "buyerReference": "04011000-12345-67", "senderName": "Muster Lieferant GmbH", "senderStreet": "Weg 1",
          "senderPostcode": "10115", "senderCity": "Berlin", "senderCountry": "DE", "senderVatId": "DE123456789",
          "senderTaxId": "11/111/11111", "senderEmail": "rechnung@example.com", "senderPhone": "+49 30 000000", "senderId": "S-1",
          "recipientName": "Kunde AG", "recipientStreet": "Platz 2", "recipientPostcode": "20095", "recipientCity": "Hamburg",
          "recipientCountry": "DE", "recipientVatId": "DE987654321", "recipientEmail": "ap@example.org", "recipientId": "K-1",
          "bankIban": "DE89370400440532013000", "bankSwift": "COBADEFFXXX", "bankAccountHolder": "Muster Lieferant GmbH",
          "notes": "Allgemeiner Hinweis", "notesSubjectCode": "AAI",
          "invoiceNotes": [ { "text": "Zweiter Hinweis", "subjectCode": "SUR" } ],
          "taxCurrency": "USD", "taxAmountInTaxCurrency": "60,52", "taxPointDate": "2026-11-15",
          "invoicedObjectIdentifier": "OBJ-7", "invoicedObjectScheme": "AAA",
          "invoicingPeriodStart": "2026-11-01", "invoicingPeriodEnd": "30.11.2026",
          "senderTradingName": "Muster", "senderIdScheme": "0088", "senderLegalRegistrationId": "HRB 12345",
          "senderLegalRegistrationScheme": "0002", "senderAdditionalLegalInformation": "Amtsgericht Berlin",
          "senderStreet2": "Haus B", "senderAddressLine3": "3. OG", "senderCountrySubdivision": "BE",
          "senderContactDepartment": "Buchhaltung", "senderContactPhone": "+49 30 111111", "senderContactEmail": "erika@example.com",
          "recipientTradingName": "Kunde", "recipientLegalRegistrationId": "HRB 999", "recipientStreet2": "Tor 3",
          "recipientAddressLine3": "Halle 4", "recipientCountrySubdivision": "HH", "recipientContactName": "Max Kunde",
          "recipientContactPhone": "+49 40 222222", "recipientContactEmail": "max@example.org",
          "payeeName": "Factoring GmbH", "payeeIdentifier": "P-9", "payeeLegalRegistrationId": "HRB 777",
          "taxRepresentativeName": "Fiskal GmbH", "taxRepresentativeVatId": "DE111111125", "taxRepresentativeStreet": "Steuerweg 1",
          "taxRepresentativeAddressLine2": "Postfach", "taxRepresentativeAddressLine3": "Eingang C", "taxRepresentativeCity": "Bonn",
          "taxRepresentativePostcode": "53111", "taxRepresentativeCountrySubdivision": "NW", "taxRepresentativeCountry": "DE",
          "deliveryName": "Lager", "deliveryStreet": "Lagerweg 1", "deliveryStreet2": "Rampe 5", "deliveryAddressLine3": "Tor 2",
          "deliveryCity": "Hamburg", "deliveryPostcode": "20097", "deliveryCountry": "DE", "deliveryCountrySubdivision": "HH",
          "deliveryLocationId": "LOC-1", "deliveryDate": "2026-11-18",
          "paymentMeansText": "Überweisung", "remittanceInformation": "RE-2026-0001",
          "documentAllowanceBaseAmount": "200", "documentAllowancePercentage": "5", "documentAllowanceReasonCode": "95",
          "allowancesCharges": [ { "chargeAmount": "7.50", "reason": "Verpackung", "reasonCode": "ABL" } ],
          "precedingInvoices": [ { "number": "RE-2026-0000", "issueDate": "2026-10-01" } ],
          "supportingDocuments": [ { "reference": "LS-1", "description": "Lieferschein", "url": "https://example.com/ls-1" },
                                   { "reference": "ANL-1", "content": "JVBERi0xLjQK", "mimeCode": "application/pdf", "fileName": "anl-1.pdf" } ],
          "lineItems": [ {
            "description": "Ventil", "quantity": "2", "unit": "piece", "unitPrice": "100", "vatRate": "19", "vatCategory": "S",
            "objectIdentifier": "SER-1", "objectIdentifierScheme": "AAA", "linePeriodStart": "2026-11-01", "linePeriodEnd": "2026-11-30",
            "lineDiscountBaseAmount": "200", "lineDiscountPercent": "10", "lineDiscountReasonCode": "95",
            "itemGrossPrice": "110", "itemPriceDiscount": "10", "priceBaseQuantityUnit": "piece",
            "itemDescription": "Edelstahlventil DN 50", "itemClassificationCode": "12345", "itemClassificationScheme": "TST",
            "itemClassificationSchemeVersion": "1", "itemClassifications": [ { "code": "84818000", "scheme": "HS" } ],
            "itemAttributes": [ { "name": "Farbe", "value": "Blau" } ], "itemAttributeName": "Werkstoff", "itemAttributeValue": "1.4404"
          } ]
        }
        """;

    [Fact]
    public void EveryTermIsWrittenAndReadBack()
    {
        var warnings = new List<string>();
        var info = new List<string>();
        var inv = InvoiceMapper.Map(AxDocument.Parse(AllTerms), warnings.Add, info.Add);
        Assert.Empty(warnings);
        Assert.DoesNotContain(info, i => i.StartsWith("JSON fields not written"));

        var back = CiiReader.Read(EInvoiceGenerator.BuildCiiXml(inv, InvoiceProfile.XRechnung)).Invoice;

        Assert.Contains(back.Notes, n => n.Text == "Allgemeiner Hinweis" && n.SubjectCode == "AAI");
        Assert.Contains(back.Notes, n => n.Text == "Zweiter Hinweis" && n.SubjectCode == "SUR");
        Assert.Equal("USD", back.TaxCurrency);
        Assert.Equal(60.52m, back.VatTotalInTaxCurrency);
        Assert.Equal(new DateTime(2026, 11, 15), back.TaxPointDate);
        Assert.Equal("OBJ-7", back.InvoicedObjectIdentifier?.Value);
        Assert.Equal("AAA", back.InvoicedObjectIdentifier?.Scheme);
        Assert.Equal(new DateTime(2026, 11, 1), back.InvoicingPeriod?.Start);
        Assert.Equal(new DateTime(2026, 11, 30), back.InvoicingPeriod?.End);

        Assert.Equal("Muster", back.Seller.TradingName);
        Assert.Equal("0088", back.Seller.Identifiers[0].Scheme);
        Assert.Equal("HRB 12345", back.Seller.LegalRegistrationIdentifier?.Value);
        Assert.Equal("0002", back.Seller.LegalRegistrationIdentifier?.Scheme);
        Assert.Equal("Amtsgericht Berlin", back.Seller.AdditionalLegalInformation);
        Assert.Equal(("Haus B", "3. OG", "BE"), (back.Seller.Address.Line2, back.Seller.Address.Line3, back.Seller.Address.CountrySubdivision));
        Assert.Equal(((string?)null, "Buchhaltung", "+49 30 111111", "erika@example.com"),
            (back.Seller.Contact?.Name, back.Seller.Contact?.Department, back.Seller.Contact?.Phone, back.Seller.Contact?.Email));

        Assert.Equal("Kunde", back.Buyer.TradingName);
        Assert.Equal("HRB 999", back.Buyer.LegalRegistrationIdentifier?.Value);
        Assert.Equal(("Tor 3", "Halle 4", "HH"), (back.Buyer.Address.Line2, back.Buyer.Address.Line3, back.Buyer.Address.CountrySubdivision));
        Assert.Equal(("Max Kunde", (string?)null, "+49 40 222222", "max@example.org"),
            (back.Buyer.Contact?.Name, back.Buyer.Contact?.Department, back.Buyer.Contact?.Phone, back.Buyer.Contact?.Email));

        Assert.Equal("Factoring GmbH", back.Payee?.Name);
        Assert.Equal("P-9", back.Payee?.Identifier?.Value);
        Assert.Equal("HRB 777", back.Payee?.LegalRegistrationIdentifier?.Value);
        var rep = back.SellerTaxRepresentative!;
        Assert.Equal(("Fiskal GmbH", "DE111111125"), (rep.Name, rep.VatIdentifier));
        Assert.Equal(("Steuerweg 1", "Postfach", "Eingang C", "Bonn", "53111", "NW", "DE"),
            (rep.Address.Line1, rep.Address.Line2, rep.Address.Line3, rep.Address.City, rep.Address.PostCode, rep.Address.CountrySubdivision, rep.Address.CountryCode));

        var delivery = back.Delivery!;
        Assert.Equal("LOC-1", delivery.LocationIdentifier?.Value);
        Assert.Equal(("Rampe 5", "Tor 2", "HH"), (delivery.Address?.Line2, delivery.Address?.Line3, delivery.Address?.CountrySubdivision));

        Assert.Equal("Überweisung", back.PaymentInstructions?.MeansText);
        var allowance = back.AllowancesAndCharges.Single(a => !a.IsCharge);
        Assert.Equal((10m, 200m, 5m, "95"), (allowance.Amount, allowance.BaseAmount, allowance.Percentage, allowance.ReasonCode));
        var charge = back.AllowancesAndCharges.Single(a => a.IsCharge);
        Assert.Equal((7.50m, "Verpackung", "ABL"), (charge.Amount, charge.Reason, charge.ReasonCode));
        Assert.Equal("RE-2026-0000", Assert.Single(back.PrecedingInvoices).Number);
        Assert.Equal(2, back.SupportingDocuments.Count);
        Assert.Equal("https://example.com/ls-1", back.SupportingDocuments[0].ExternalLocation);
        Assert.Equal(("application/pdf", "anl-1.pdf"), (back.SupportingDocuments[1].MimeCode, back.SupportingDocuments[1].FileName));
        Assert.Equal("%PDF-1.4\n", Encoding.ASCII.GetString(back.SupportingDocuments[1].Content!));

        var line = back.Lines.Single();
        Assert.Equal(("SER-1", "AAA"), (line.ObjectIdentifier?.Value, line.ObjectIdentifier?.Scheme));
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 30)), (line.Period?.Start, line.Period?.End));
        var lineAllowance = line.AllowancesAndCharges.Single();
        Assert.Equal((20m, 200m, 10m, "95"), (lineAllowance.Amount, lineAllowance.BaseAmount, lineAllowance.Percentage, lineAllowance.ReasonCode));
        Assert.Equal((110m, 10m), (line.GrossPrice, line.PriceDiscount));
        Assert.Equal("Edelstahlventil DN 50", line.Item.Description);
        Assert.Equal(new[] { ("12345", "TST", "1"), ("84818000", "HS", (string?)null) },
            line.Item.Classifications.Select(c => (c.Code, c.Scheme, c.SchemeVersion)).ToArray());
        Assert.Equal(new[] { ("Werkstoff", "1.4404"), ("Farbe", "Blau") }, line.Item.Attributes.Select(a => (a.Name, a.Value)).ToArray());

        var preflight = EInvoiceGenerator.Preflight(inv, InvoiceProfile.XRechnung, _ => { });
        Assert.True(preflight.IsValid, string.Join("\n", preflight.Issues));
        Assert.Empty(preflight.Warnings);
    }

    [Fact]
    public void CardAndDirectDebitAreWrittenWithTheirMeansCode()
    {
        var card = InvoiceMapper.Map(Doc(""", "paymentCardNumber": "1234", "paymentCardHolder": "E. Muster" """));
        Assert.Equal(("48", "1234", "E. Muster"), (card.PaymentInstructions!.MeansCode, card.PaymentInstructions.Card!.PrimaryAccountNumber, card.PaymentInstructions.Card.HolderName));

        var debit = InvoiceMapper.Map(Doc(""", "mandateReference": "M-1", "creditorIdentifier": "DE98ZZZ09999999999", "debitedIban": "DE89 3704 0044 0532 0130 00" """));
        var dd = debit.PaymentInstructions!;
        Assert.Equal(("59", "M-1", "DE98ZZZ09999999999", "DE89370400440532013000"), (dd.MeansCode, dd.DirectDebit!.MandateReference, dd.DirectDebit.CreditorIdentifier, dd.DirectDebit.DebitedAccountIdentifier));
        var back = CiiReader.Read(EInvoiceGenerator.BuildCiiXml(debit, InvoiceProfile.XRechnung)).Invoice.PaymentInstructions!;
        Assert.Equal(("59", "M-1"), (back.MeansCode, back.DirectDebit?.MandateReference));
    }

    private const string BankLine = "\"bankIban\": \"DE89370400440532013000\", \"bankSwift\": \"COBADEFFXXX\", \"bankAccountHolder\": \"Muster Lieferant GmbH\",";

    public static IEnumerable<object[]> KositSamples => new[] { "all-terms", "tariff-attributes", "card", "direct-debit" }.Select(n => new object[] { n });

    // The tariff sample is a ZUGFeRD export (Factur-X profile EN 16931), the others are XRechnung.
    internal static InvoiceProfile SampleProfile(string name) => name == "tariff-attributes" ? InvoiceProfile.EN16931 : InvoiceProfile.XRechnung;

    internal static Invoice Sample(string name) => InvoiceMapper.Map(AxDocument.Parse(name switch
    {
        "all-terms" => AllTerms,
        "tariff-attributes" => TariffExport,
        "card" => AllTerms.Replace(BankLine, "\"paymentCardNumber\": \"9876\", \"paymentCardHolder\": \"E. Muster\","),
        "direct-debit" => AllTerms.Replace(BankLine, "\"mandateReference\": \"M-1\", \"creditorIdentifier\": \"DE98ZZZ09999999999\", \"debitedIban\": \"DE89370400440532013000\",")
            .Replace("\"paymentMeansText\": \"Überweisung\"", "\"paymentMeansText\": \"Lastschrift\""),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    }));

    [Fact]
    public void TheCardAndDirectDebitSamplesReplaceTheBankAccount()
    {
        Assert.Contains(BankLine, AllTerms);
        Assert.NotNull(Sample("card").PaymentInstructions?.Card);
        Assert.Empty(Sample("card").PaymentInstructions!.CreditTransfers);
        Assert.NotNull(Sample("direct-debit").PaymentInstructions?.DirectDebit);
    }

    [KositTheory]
    [MemberData(nameof(KositSamples))]
    public void KositAcceptsEveryNewTerm(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "hive-bdc-terms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, name + ".xml");
            File.WriteAllBytes(file, EInvoiceGenerator.BuildCiiXml(Sample(name), SampleProfile(name)));
            var outcome = ExternalValidators.Kosit(ExternalValidators.Directory!, new[] { file })[name];
            Assert.True(outcome.Accepted, outcome.ToString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TheLogNeverShowsACardNumberADebitedAccountOrAnAttachment()
    {
        var info = new List<string>();
        InvoiceMapper.Map(Doc(""", "paymentCardNumber": "4111111111111111", "mandateReference": "M-1", "creditorIdentifier": "DE98ZZZ09999999999", "debitedIban": "DE89370400440532013000", "attachmentContent": "JVBERi0xLjQK", "attachmentReference": "ANL-1", "attachmentMimeCode": "application/pdf" """), null, info.Add);
        string log = string.Join("\n", info);
        Assert.DoesNotContain("4111111111111111", log);
        Assert.DoesNotContain("DE89370400440532013000", log);
        Assert.DoesNotContain("JVBERi0xLjQK", log);
        Assert.Contains("…1111", log);
        Assert.Contains("12 base64 characters", log);
    }

    // The complete example of the ERP guide must stay a valid invoice that says what the guide promises.
    [Fact]
    public void TheErpGuideExampleIsAValidInvoice()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "HiveBDC.sln"))) dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("HiveBDC.sln");
        string guide = File.ReadAllText(Path.Combine(dir, "docs", "erp-json-fields.md"));
        int start = guide.IndexOf("## Complete example", StringComparison.Ordinal);
        int open = guide.IndexOf("```json", start, StringComparison.Ordinal) + "```json".Length;
        string json = guide[open..guide.IndexOf("```", open, StringComparison.Ordinal)];

        var warnings = new List<string>();
        var info = new List<string>();
        var inv = InvoiceMapper.Map(AxDocument.Parse(json), warnings.Add, info.Add);
        Assert.Empty(warnings);
        Assert.DoesNotContain(info, i => i.StartsWith("JSON fields not written"));
        var doc = AxDocument.Parse(json);
        var result = EInvoiceGenerator.Preflight(inv, EInvoiceGenerator.ProfileFor(doc), _ => { });
        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        // The same export as XRechnung meets the XRechnung rules too, as the guide's required fields promise.
        var asXRechnung = InvoiceMapper.Map(AxDocument.Parse(json.Replace("\"eInvoiceType\": \"ZUGFeRD\"", "\"eInvoiceType\": \"XRechnung\"")));
        var xr = EInvoiceGenerator.Preflight(asXRechnung, InvoiceProfile.XRechnung, _ => { });
        Assert.True(xr.IsValid, string.Join("\n", xr.Issues));

        var item = inv.Lines.Single().Item;
        Assert.Equal("Nozzle, complete, PEEK", item.Description);
        Assert.Equal(("39269097", "HS"), (item.Classifications.Single().Code, item.Classifications.Single().Scheme));
        Assert.Equal(new[] { "Material code", "Drawing-No. / Index / Date", "Charge" }, item.Attributes.Select(a => a.Name).ToArray());
        Assert.Contains(inv.Notes, n => n.Text == "Delivery terms: DDP, delivered duty paid, incl. packaging; Shipping method: UPS");
        Assert.Equal("erika.muster@example.com", inv.Seller.Contact!.Email);
        Assert.Equal("SET", inv.Lines.Single().UnitCode);
    }

    // ── failure cases ──

    [Fact]
    public void AContactCarriesANameOrADepartmentNotBoth()
    {
        var warnings = new List<string>();
        var both = InvoiceMapper.Map(Doc(""", "senderContactName": "Erika", "senderContactDepartment": "Buchhaltung", "recipientContactName": "Max", "recipientContactDepartment": "Einkauf" """), warnings.Add);
        Assert.Equal(("Erika", (string?)null), (both.Seller.Contact!.Name, both.Seller.Contact.Department));
        Assert.Equal(("Max", (string?)null), (both.Buyer.Contact!.Name, both.Buyer.Contact.Department));
        Assert.Contains(warnings, w => w.Contains("CII-SR-465"));
        Assert.Contains(warnings, w => w.Contains("CII-SR-466"));

        var departmentOnly = InvoiceMapper.Map(Doc(""", "recipientContactDepartment": "Einkauf" """));
        Assert.Equal(((string?)null, "Einkauf"), (departmentOnly.Buyer.Contact!.Name, departmentOnly.Buyer.Contact.Department));
    }


    [Theory]
    [InlineData("Material code: PEEK\nno colon here\n: no name\nEmpty value:", 1, 3)]
    [InlineData("A: 1: 2", 1, 0)]
    public void AttributeLinesNeedANameAndAValue(string text, int written, int warned)
    {
        var warnings = new List<string>();
        var inv = InvoiceMapper.Map(Doc(lineExtra: $$""", "itemAttributes": {{System.Text.Json.JsonSerializer.Serialize(text)}} """), warnings.Add);
        Assert.Equal(written, inv.Lines[0].Item.Attributes.Count);
        Assert.Equal(warned, warnings.Count(w => w.Contains("is not 'name: value'")));
        if (text.StartsWith("A:")) Assert.Equal(("A", "1: 2"), (inv.Lines[0].Item.Attributes[0].Name, inv.Lines[0].Item.Attributes[0].Value));
    }

    [Theory]
    [InlineData(""", "taxRepresentativeName": "Fiskal GmbH", "taxRepresentativeCountry": "DE" """, "VAT identifier (BT-63)")]
    [InlineData(""", "payeeIdentifier": "P-1" """, "payee name (BT-59)")]
    [InlineData(""", "mandateReference": "M-1", "debitedIban": "DE89370400440532013000" """, "creditor identifier (BT-90)")]
    [InlineData(""", "paymentCardHolder": "E. Muster" """, "card number (BT-87)")]
    [InlineData(""", "taxCurrency": "USD" """, "BT-111")]
    [InlineData(""", "taxPointDate": "2026-11-01", "taxPointDateCode": "3" """, "exclude each other")]
    public void AGroupWithoutItsMandatoryTermIsLeftOutWithAWarning(string extra, string expected)
    {
        var warnings = new List<string>();
        var inv = InvoiceMapper.Map(Doc(extra), warnings.Add);
        Assert.Contains(warnings, w => w.Contains(expected));
        Assert.Null(inv.SellerTaxRepresentative);
        Assert.Null(inv.Payee);
        Assert.Null(inv.PaymentInstructions?.DirectDebit);
        Assert.Null(inv.PaymentInstructions?.Card);
        Assert.Null(inv.TaxCurrency);
        Assert.Null(inv.TaxPointDateCode);
    }

    [Fact]
    public void AClassificationWithoutSchemeIsSkipped()
    {
        var warnings = new List<string>();
        var inv = InvoiceMapper.Map(Doc(lineExtra: """, "itemClassificationCode": "123", "itemClassifications": [ { "code": "456" } ] """), warnings.Add);
        Assert.Empty(inv.Lines[0].Item.Classifications);
        Assert.Equal(2, warnings.Count(w => w.Contains("no scheme")));
    }

    [Fact]
    public void AListThatIsNotAListAndEntriesThatAreNotObjectsAreWarnedAbout()
    {
        var warnings = new List<string>();
        var info = new List<string>();
        var inv = InvoiceMapper.Map(Doc(""", "invoiceNotes": { "text": "x" }, "precedingInvoices": [ "RE-1", { "number": "RE-2", "foo": "bar" } ] """), warnings.Add, info.Add);
        Assert.Contains(warnings, w => w.Contains("'invoiceNotes' is not a list"));
        Assert.Contains(warnings, w => w.Contains("'precedingInvoices' entry 1 is not an object"));
        Assert.Equal("RE-2", Assert.Single(inv.PrecedingInvoices).Number);
        Assert.Contains(info, i => i.StartsWith("JSON fields not written") && i.Contains("precedingInvoices.foo"));
    }

    [Theory]
    [InlineData("100,00", 100.00)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("0.00", 0)]
    [InlineData("-12,5", -12.5)]
    [InlineData("156.00", 156)]
    public void NumbersInGermanAndInvariantNotation(string text, double expected)
    {
        Assert.True(TolerantDecimalConverter.TryParseNumber(text, out var value));
        Assert.Equal((decimal)expected, value);
        var inv = InvoiceMapper.Map(Doc($$""", "chargeTotalAmount": "{{text}}" """));
        decimal total = inv.AllowancesAndCharges.Sum(a => a.IsCharge ? a.Amount ?? 0m : -(a.Amount ?? 0m));
        Assert.Equal((decimal)expected, total);
    }

    [Theory]
    [InlineData("../secret.pdf", "without a path")]
    [InlineData("sub/anl.pdf", "without a path")]
    [InlineData("missing.pdf", "is not in the JSON folder")]
    [InlineData("anl.exe", "is not allowed")]
    [InlineData("big.pdf", "more than")]
    public void AnAttachmentFileMustBeASmallAllowedFileNextToTheJson(string fileName, string expected)
    {
        string folder = Path.Combine(Path.GetTempPath(), "hive-bdc-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "anl.exe"), new byte[] { 1 });
            using (var big = File.Create(Path.Combine(folder, "big.pdf"))) big.SetLength(InvoiceMapper.MaxAttachmentBytes + 1);
            var warnings = new List<string>();
            var inv = InvoiceMapper.Map(Doc($$""", "supportingDocuments": [ { "reference": "ANL-1", "fileName": "{{fileName}}" } ] """), warnings.Add, null, folder);
            var document = Assert.Single(inv.SupportingDocuments);
            Assert.Equal("ANL-1", document.Reference);
            Assert.Null(document.Content);
            Assert.Contains(warnings, w => w.Contains(expected));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnAttachmentFileNextToTheJsonIsEmbedded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hive-bdc-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "zeichnung.pdf"), Encoding.ASCII.GetBytes("%PDF-1.4\n"));
            var inv = InvoiceMapper.Map(Doc(""", "attachmentFileName": "zeichnung.pdf" """), null, null, folder);
            var document = Assert.Single(inv.SupportingDocuments);
            Assert.Equal(("zeichnung", "application/pdf", "zeichnung.pdf", 9), (document.Reference, document.MimeCode, document.FileName, document.Content!.Length));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static AxDocument Doc(string extra = "", string lineExtra = "") => AxDocument.Parse($$"""
        {
          "documentType": "SalesInvoice", "documentNumber": "2026000001", "eInvoiceType": "XRechnung",
          "issueDate": "2026-09-22", "dueDate": "2026-10-22", "currency": "EUR", "vatRate": "19",
          "senderName": "Muster Lieferant", "senderCountry": "DE", "senderVatId": "DE123456789",
          "recipientName": "Kunde", "recipientCountry": "DE",
          "lineItems": [ { "description": "A", "quantity": "2", "unit": "piece", "unitPrice": "150.00"{{lineExtra}} } ]{{extra}}
        }
        """);
}