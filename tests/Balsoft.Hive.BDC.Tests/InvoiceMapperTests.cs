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

    [Fact]
    public void EmptyExtraValuesAreNotWritten()
    {
        var inv = InvoiceMapper.Map(Invoice(""", "reference": "R-1", "purchase order reference": "  ", "projectReference": "" """));
        Assert.Equal("R-1", inv.PurchaseOrderReference);
        Assert.Null(inv.ProjectReference);
    }
}
