using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class InvoiceMapperTests
{
    // Aktivbank (and EN 16931 practice) want the sign in BT-3 only: every non-380 document, and a
    // 380 whose lines add up negative, is written with absolute quantities and prices. A plain
    // invoice with a positive total keeps its offset lines.
    [Theory]
    [InlineData("381", "-1", "1", 120)]
    [InlineData("381", "1", "-1", 120)]
    [InlineData("384", "1", "-1", 120)]
    [InlineData("84", "1", "-1", 120)]
    [InlineData("380", "-1", "1", 120)]
    [InlineData("380", "1", "-1", 80)]
    public void NonInvoiceAndNegativeTotalDocumentsUseAbsoluteValues(string code, string firstQuantity, string secondQuantity, decimal expectedNet)
    {
        var doc = Invoice($""", "invoiceTypeCode": "{code}" """, $$"""
            [ { "description": "Main", "quantity": "{{firstQuantity}}", "unitPrice": "100" },
              { "description": "Offset", "quantity": "{{secondQuantity}}", "unitPrice": "20" } ]
            """);
        var mapped = InvoiceMapper.Map(doc);
        var totals = mapped.CalculateTotals();
        Assert.Equal(expectedNet, totals.LineNetTotal);
        Assert.Equal(expectedNet * 1.19m, totals.TaxInclusiveAmount);
        Assert.All(mapped.Lines, line => Assert.True(line.NetPrice >= 0));
        if (expectedNet == 120m)
            Assert.All(mapped.Lines, line => Assert.True(line.Quantity >= 0));
    }

    [Fact]
    public void NegativeUnitPriceOnAPlainInvoiceMovesItsSignToTheQuantity()
    {
        var mapped = InvoiceMapper.Map(Invoice(""", "invoiceTypeCode": "380" """, """
            [ { "description": "Main", "quantity": "2", "unitPrice": "100" },
              { "description": "Discount", "quantity": "1", "unitPrice": "-20" } ]
            """));
        Assert.Equal(180m, mapped.CalculateTotals().LineNetTotal);
        var discount = mapped.Lines.Single(l => l.Item.Name == "Discount");
        Assert.Equal(-1m, discount.Quantity);
        Assert.Equal(20m, discount.NetPrice);
    }

    // ── 1.1.0: price base quantity, line charge / discount, deliver-to, Skonto percent, "st" ──

    [Fact]
    public void PriceUnitAndLineChargeReproduceTheErpLineAmount()
    {
        // AX 2009 customer invoice: 2000 pieces at 60.45 per 100 plus a 315.00 line surcharge = 1524.00,
        // and a plain 2000 at 50.00 per 100 = 1000.00; the ERP totals are 2524.00 / 479.56 / 3003.56.
        var mapped = InvoiceMapper.Map(Invoice("", """
            [ { "description": "Formteil", "quantity": "2000.00", "unit": "st", "unitPrice": "0.6045",
                "priceUnit": "100.00", "priceUnitPrice": "60.45", "lineChargeAmount": "315.00", "lineChargeReason": "Zuschlag" },
              { "description": "Werkzeugamortisation", "quantity": "2000.00", "unit": "st", "unitPrice": "0.5000",
                "priceUnit": "100.00", "priceUnitPrice": "50.00" } ]
            """));
        var first = mapped.Lines[0];
        Assert.Equal(60.45m, first.NetPrice);
        Assert.Equal(100m, first.PriceBaseQuantity);
        Assert.Equal(UnitCode.Piece, first.UnitCode);
        var charge = Assert.Single(first.AllowancesAndCharges);
        Assert.True(charge.IsCharge);
        Assert.Equal(315m, charge.Amount);
        Assert.Equal("Zuschlag", charge.Reason);
        Assert.Equal(1524m, InvoiceCalculator.LineNetAmount(first));
        var totals = mapped.CalculateTotals();
        Assert.Equal(2524m, totals.LineNetTotal);
        Assert.Equal(479.56m, totals.VatTotal);
        Assert.Equal(3003.56m, totals.TaxInclusiveAmount);
        string xml = System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(mapped));
        Assert.Contains("<ram:BasisQuantity unitCode=\"H87\">100</ram:BasisQuantity>", xml);
        Assert.Contains("<ram:ChargeIndicator>", xml);
    }

    [Fact]
    public void PriceUnitWithoutAPerBasePriceScalesTheUnitPrice()
    {
        var mapped = InvoiceMapper.Map(Invoice("", """
            [ { "description": "A", "quantity": "31680", "unitPrice": "0.0335", "priceUnit": "100" } ]
            """));
        Assert.Equal(3.35m, mapped.Lines[0].NetPrice);
        Assert.Equal(100m, mapped.Lines[0].PriceBaseQuantity);
        Assert.Equal(1061.28m, mapped.CalculateTotals().LineNetTotal);
    }

    [Fact]
    public void LineDiscountIsAnAllowanceAndKeepsTheLineAddingUp()
    {
        var mapped = InvoiceMapper.Map(Invoice("", """
            [ { "description": "A", "quantity": "10", "unitPrice": "100", "lineDiscountAmount": "50.00" } ]
            """));
        var allowance = Assert.Single(mapped.Lines[0].AllowancesAndCharges);
        Assert.False(allowance.IsCharge);
        Assert.Equal(50m, allowance.Amount);
        Assert.Equal("Rabatt", allowance.Reason);
        Assert.Equal(950m, mapped.CalculateTotals().LineNetTotal);
    }

    [Fact]
    public void DeliverToAddressIsWrittenWithItsCountry()
    {
        var mapped = InvoiceMapper.Map(Invoice(""", "deliveryName": "Lager Süd GmbH", "deliveryStreet": "Werk 1\nIndustriestraße 5", "deliveryPostcode": "12345", "deliveryCity": "Musterstadt", "deliveryCountry": "DE" """));
        Assert.Equal("Lager Süd GmbH", mapped.Delivery!.PartyName);
        Assert.Equal("Werk 1", mapped.Delivery.Address!.Line1);
        Assert.Equal("Industriestraße 5", mapped.Delivery.Address.Line2);
        Assert.Equal("12345", mapped.Delivery.Address.PostCode);
        Assert.Equal("DE", mapped.Delivery.Address.CountryCode);
        string xml = System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(mapped));
        Assert.Contains("<ram:ShipToTradeParty>", xml);
        Assert.Contains("<ram:CountryID>DE</ram:CountryID>", xml);
    }

    [Fact]
    public void DeliverToCountryFallsBackToTheBuyerCountryAndIsAbsentWithoutAnyField()
    {
        var withName = InvoiceMapper.Map(Invoice(""", "deliveryName": "Lager Nord" """));
        Assert.Equal("DE", withName.Delivery!.Address!.CountryCode);
        var without = InvoiceMapper.Map(Invoice());
        Assert.Null(without.Delivery!.Address);
        Assert.Null(without.Delivery.PartyName);
    }

    [Fact]
    public void CashDiscountPercentIsDerivedFromTheAmountWhenMissing()
    {
        // 2 x 150 = 300 net, 357 gross; 10.71 is 3 % of the gross total.
        var warnings = new List<string>();
        var mapped = InvoiceMapper.Map(Invoice(""", "cashDiscountDate": "2026-10-01", "cashDiscountAmount": "10.71" """), warnings.Add);
        Assert.Contains("3 % Skonto (10.71 EUR)", mapped.PaymentTerms);
        var discount = Assert.Single(mapped.CashDiscounts);
        Assert.Equal(3m, discount.Percent);
        Assert.Equal(9, discount.Days);
        Assert.DoesNotContain(warnings, w => w.Contains("Skonto") || w.Contains("cash discount"));
    }

    [Fact]
    public void CashDiscountPercentFromTheErpWins()
    {
        var mapped = InvoiceMapper.Map(Invoice(""", "cashDiscountDate": "2026-10-01", "cashDiscountAmount": "10.71", "cashDiscountPercent": "3.00", "cashDiscountDays": "25" """));
        Assert.Contains("3 % Skonto", mapped.PaymentTerms);
        Assert.Equal(25, Assert.Single(mapped.CashDiscounts).Days);
    }

    private static AxDocument Invoice(string extraJson = "", string lines = """
        [ { "description": "A", "quantity": "2", "unit": "piece", "unitPrice": "150.00" } ]
        """) => AxDocument.Parse($$"""
        {
          "documentType": "SalesInvoice", "documentNumber": "2026000001", "eInvoiceType": "XRechnung",
          "issueDate": "2026-09-22", "dueDate": "2026-10-22", "currency": "EUR", "vatRate": "19",
          "senderName": "Muster Lieferant", "senderCountry": "DE", "senderVatId": "DE123456789",
          "recipientName": "Balsoft", "recipientCountry": "DE", "bankIban": "DE89370400440532013000",
          "lineItems": {{lines}}{{extraJson}}
        }
        """);

    [Fact]
    public void AktivbankIdentifiersLandInTheRightElements()
    {
        var doc = Invoice(""", "zrKennzeichen": "42", "senderId": "99001", "senderGln": "4000001000005", "recipientId": "99002", "recipientGln": "11111111111" """);
        var warnings = new List<string>();
        var inv = InvoiceMapper.Map(doc, warnings.Add);

        Assert.Equal("0042", inv.ContractReference);
        Assert.Equal("99001", inv.Seller.Identifiers[0].Value);
        Assert.Null(inv.Seller.Identifiers[0].Scheme);
        Assert.Equal("4000001000005", inv.Seller.Identifiers[1].Value);
        Assert.Equal(IdentifierScheme.Gln, inv.Seller.Identifiers[1].Scheme);
        Assert.Equal("99002", Assert.Single(inv.Buyer.Identifiers).Value);
        Assert.Contains(warnings, w => w.Contains("recipientGln"));   // 11 digits is not a GLN
    }

    [Fact]
    public void NegativeTotalWithoutTypeCodeBecomesCreditNoteWithPositiveAmounts()
    {
        var doc = Invoice(lines: """[ { "description": "Return", "quantity": "-1", "unit": "piece", "unitPrice": "150.00" } ]""");
        var inv = InvoiceMapper.Map(doc);

        Assert.Equal(InvoiceTypeCode.CreditNote, inv.TypeCode);
        Assert.Equal(1m, inv.Lines.Single().Quantity);
        Assert.Equal(178.50m, inv.CalculateTotals().TaxInclusiveAmount);
    }

    [Theory]
    [InlineData("380", InvoiceTypeCode.CommercialInvoice)]
    [InlineData("381", InvoiceTypeCode.CreditNote)]
    [InlineData("84", InvoiceTypeCode.DebitNoteFinancialAdjustments)]
    public void TypeCodeIsMapped(string code, InvoiceTypeCode expected)
        => Assert.Equal(expected, InvoiceMapper.Map(Invoice($""", "invoiceTypeCode": "{code}" """)).TypeCode);

    [Fact]
    public void MixedVatRatesProduceOneBreakdownPerRate()
    {
        var doc = Invoice(lines: """
            [ { "description": "19", "quantity": "1", "unit": "piece", "unitPrice": "100.00", "vatRate": "19" },
              { "description": "7",  "quantity": "2", "unit": "piece", "unitPrice": "10.00",  "vatRate": "7" } ]
            """);
        var totals = InvoiceMapper.Map(doc).CalculateTotals();
        var taxes = totals.VatBreakdown.OrderByDescending(t => t.Rate).ToList();

        Assert.Equal(2, taxes.Count);
        Assert.Equal((100m, 19m), (taxes[0].TaxableAmount, taxes[0].Rate!.Value));
        Assert.Equal((20m, 7m), (taxes[1].TaxableAmount, taxes[1].Rate!.Value));
        Assert.Equal(140.40m, totals.TaxInclusiveAmount);
    }

    [Fact]
    public void CashDiscountBecomesASkontoTermWithDaysFromIssueDate()
    {
        var doc = Invoice(""", "cashDiscountDate": "2026-10-02", "cashDiscountPercent": "2", "cashDiscountAmount": "7.14" """);
        var inv = InvoiceMapper.Map(doc);

        var skonto = Assert.Single(inv.CashDiscounts);
        Assert.Equal(10, skonto.Days);
        Assert.Equal(2m, skonto.Percent);
        Assert.Equal(new DateTime(2026, 10, 22), inv.DueDate);
        Assert.Equal("Zahlbar ohne Abzug bis 22.10.2026\nBei Zahlung bis 02.10.2026 2 % Skonto (7.14 EUR)", inv.PaymentTerms);
    }

    [Fact]
    public void PlainInvoiceHasNoSettlementIdentifiers()
    {
        var inv = InvoiceMapper.Map(Invoice());
        Assert.Null(inv.ContractReference);
        Assert.Empty(inv.Seller.Identifiers);
        Assert.Equal(InvoiceTypeCode.CommercialInvoice, inv.TypeCode);
        Assert.Equal(357.00m, inv.CalculateTotals().TaxInclusiveAmount);
    }

    [Fact]
    public void ExemptCategoriesGetTheExportsVatNoteAsReason()
    {
        var inv = InvoiceMapper.Map(Invoice(""", "vatRate": "0", "vatNote": "Steuerfreie innergemeinschaftliche Lieferung" """));
        var exemption = Assert.Single(inv.VatExemptions);
        Assert.Equal(VatCategory.Exempt, exemption.Category);
        Assert.Equal("Steuerfreie innergemeinschaftliche Lieferung", exemption.Reason);
    }

    // Customer case 2026-10-07: AX maps CustomerRef to the JSON key "purchase order reference"; the
    // customer wants it in BT-13 (CII BuyerOrderReferencedDocument/IssuerAssignedID).
    [Theory]
    [InlineData("purchase order reference")]
    [InlineData("PurchaseOrderReference")]
    [InlineData("purchaseOrderReference")]
    [InlineData("BT-13")]
    public void ExtraJsonKeysReachTheirBusinessTermsInTheXml(string poKey)
    {
        var doc = Invoice($$"""
            , "reference": "2026004721", "{{poKey}}": "45001234", "buyerReference": "LW-4711",
              "orderReference": "2026120033", "bankSwift": "COBADEFFXXX",
              "bankAccountHolder": "Muster Lieferant GmbH", "bankName": "Musterbank"
            """, """
            [ { "description": "A", "quantity": "1", "unitPrice": "1.53", "itemId": "W-100", "gtin": "4006209000001" } ]
            """);
        var info = new List<string>();
        var inv = InvoiceMapper.Map(doc, null, info.Add);

        Assert.Equal("45001234", inv.PurchaseOrderReference);
        Assert.Equal("LW-4711", inv.BuyerReference);
        Assert.Equal("2026120033", inv.SalesOrderReference);
        Assert.Equal("COBADEFFXXX", inv.PaymentInstructions!.CreditTransfers[0].ServiceProviderIdentifier);
        Assert.Equal("W-100", inv.Lines[0].Item.SellersIdentifier);

        string xml = System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(inv));
        Assert.Matches(@"<ram:BuyerOrderReferencedDocument>\s*<ram:IssuerAssignedID>45001234</ram:IssuerAssignedID>", xml);
        Assert.Contains("<ram:BuyerReference>LW-4711</ram:BuyerReference>", xml);
        Assert.Matches(@"<ram:SellerOrderReferencedDocument>\s*<ram:IssuerAssignedID>2026120033</ram:IssuerAssignedID>", xml);
        Assert.Contains("COBADEFFXXX", xml);
        Assert.Contains(info, m => m.Contains("BT-13") && m.Contains(poKey));
        Assert.Contains(info, m => m.Contains("not written") && m.Contains("bankName"));
    }

    [Fact]
    public void WithoutDedicatedKeysTheGenericReferenceStillFillsBt10AndBt13()
    {
        var inv = InvoiceMapper.Map(Invoice(""", "reference": "R-1" """));
        Assert.Equal("R-1", inv.BuyerReference);
        Assert.Equal("R-1", inv.PurchaseOrderReference);
        Assert.Null(inv.SalesOrderReference);
    }

    // AX 2012 export (2026-10-12): "reference" carries the invoice number and the customer's
    // purchase order comes as "purchOrderReference"; BT-13 must be the purchase order.
    [Fact]
    public void Ax2012PurchOrderReferenceIsThePurchaseOrderNotTheInvoiceNumber()
    {
        var inv = InvoiceMapper.Map(Invoice("""
            , "invoiceNumber": "RE-2026-0815", "reference": "RE-2026-0815", "purchOrderReference": "4500123456",
              "buyerReference": "4500123456", "orderReference": "SO-2026-0042"
            """));
        Assert.Equal("4500123456", inv.PurchaseOrderReference);
        Assert.Equal("4500123456", inv.BuyerReference);
        Assert.Equal("SO-2026-0042", inv.SalesOrderReference);
        string xml = System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(inv));
        Assert.Matches(@"<ram:BuyerOrderReferencedDocument>\s*<ram:IssuerAssignedID>4500123456</ram:IssuerAssignedID>", xml);
        Assert.DoesNotMatch(@"<ram:BuyerOrderReferencedDocument>\s*<ram:IssuerAssignedID>RE-2026-0815<", xml);
    }

    [Fact]
    public void GenericReferenceEqualToTheInvoiceNumberDoesNotBecomeBt13()
    {
        var info = new List<string>();
        var inv = InvoiceMapper.Map(Invoice(""", "invoiceNumber": "RE-77", "reference": "re-77" """), null, info.Add);
        Assert.Null(inv.PurchaseOrderReference);
        Assert.Equal("re-77", inv.BuyerReference);
        Assert.Contains(info, m => m.Contains("BT-13") && m.Contains("invoice number"));
        string xml = System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(inv));
        Assert.DoesNotContain("<ram:BuyerOrderReferencedDocument>", xml);
    }

    // Validators warn when the register court, number, seat and managing directors are missing from
    // the XML (customer, 2026-10-20). The export's legalNotice becomes a BT-22 note with BT-21 REG,
    // next to the general notes, which keep no subject code.
    [Fact]
    public void LegalNoticeBecomesARegNoteNextToTheGeneralNotes()
    {
        var info = new List<string>();
        var inv = InvoiceMapper.Map(Invoice("""
            , "notes": "Es gelten unsere AGB.\n",
              "legalNotice": "Muster Lieferant GmbH mit Sitz in Musterstadt.\nAmtsgericht Musterstadt - HRB 12345\nGeschäftsführer: Max Muster\n"
            """), null, info.Add);

        Assert.Equal(2, inv.Notes.Count);
        Assert.Equal(("Es gelten unsere AGB.", null), (inv.Notes[0].Text, inv.Notes[0].SubjectCode));
        Assert.Equal("REG", inv.Notes[1].SubjectCode);
        Assert.Equal("Muster Lieferant GmbH mit Sitz in Musterstadt.\nAmtsgericht Musterstadt - HRB 12345\nGeschäftsführer: Max Muster", inv.Notes[1].Text);
        Assert.DoesNotContain(info, m => m.Contains("not written") && m.Contains("legalNotice"));

        string xml = System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(inv));
        Assert.Matches(@"<ram:IncludedNote>\s*<ram:Content>Muster Lieferant GmbH[^<]*HRB 12345[^<]*</ram:Content>\s*<ram:SubjectCode>REG</ram:SubjectCode>\s*</ram:IncludedNote>", xml);
        Assert.Matches(@"<ram:IncludedNote>\s*<ram:Content>Es gelten unsere AGB.</ram:Content>\s*</ram:IncludedNote>", xml);
    }

    [Fact]
    public void EmptyLegalNoticeAddsNoNote()
    {
        var inv = InvoiceMapper.Map(Invoice(""", "legalNotice": "  " """));
        Assert.Empty(inv.Notes);
    }

    [Fact]
    public void EmptyExtraValuesAreNotWritten()
    {
        var inv = InvoiceMapper.Map(Invoice(""", "reference": "R-1", "purchase order reference": "  ", "projectReference": "" """));
        Assert.Equal("R-1", inv.PurchaseOrderReference);
        Assert.Null(inv.ProjectReference);
    }
}
