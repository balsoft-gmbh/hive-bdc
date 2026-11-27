using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The findings of the 1.15.1 product review (2026-11-27), one test each: payment means with a bank account, unreadable
/// attachments, allowance and charge signs, the BT-8 code list, list keys sent as text, MIME case and VAT prefixes.
/// </summary>
public sealed class MapperReviewTests
{
    private static AxDocument Doc(string extra = "", string lineExtra = "", string typeCode = "380", string buyerCountry = "DE")
        => AxDocument.Parse(DocText(extra, lineExtra, typeCode, buyerCountry));

    private static string DocText(string extra = "", string lineExtra = "", string typeCode = "380", string buyerCountry = "DE") => $$"""
        {
          "documentType": "SalesInvoice", "documentNumber": "2026000001", "eInvoiceType": "XRechnung", "invoiceTypeCode": "{{typeCode}}",
          "issueDate": "2026-09-22", "dueDate": "2026-10-22", "currency": "EUR", "vatRate": "19",
          "senderName": "Muster Lieferant", "senderCountry": "DE", "senderVatId": "DE123456789", "senderStreet": "Weg 1",
          "senderPostcode": "10115", "senderCity": "Berlin", "senderPhone": "+49 30 000000", "senderEmail": "rechnung@example.com",
          "recipientName": "Kunde", "recipientCountry": "{{buyerCountry}}", "recipientPostcode": "20095", "recipientCity": "Hamburg",
          "recipientEmail": "ap@example.org", "buyerReference": "04011000-12345-67", "bankIban": "DE89370400440532013000",
          "lineItems": [ { "description": "A", "quantity": "2", "unit": "piece", "unitPrice": "150.00"{{lineExtra}} } ]{{extra}}
        }
        """;

    private static string DocJson() => DocText();

    private static (Invoice Invoice, List<string> Warnings) Map(AxDocument doc, string? folder = null)
    {
        var warnings = new List<string>();
        return (InvoiceMapper.Map(doc, warnings.Add, null, folder), warnings);
    }

    private static IEnumerable<string> XRechnungIssues(Invoice inv)
        => EInvoiceGenerator.Preflight(inv, InvoiceProfile.XRechnung, _ => { }).Issues.Select(i => i.RuleId);

    private const string DirectDebit = """, "mandateReference": "M-1", "creditorIdentifier": "DE98ZZZ09999999999", "debitedIban": "DE02120300000000202051" """;

    [Fact]
    public void ADirectDebitNextToTheSellersIbanIsMeans59WithoutTheAccount()
    {
        var (inv, warnings) = Map(Doc(DirectDebit));
        var p = inv.PaymentInstructions!;
        Assert.Equal("59", p.MeansCode);
        Assert.Empty(p.CreditTransfers);
        Assert.NotNull(p.DirectDebit);
        Assert.Contains(warnings, w => w.Contains("BG-17"));
        Assert.DoesNotContain(XRechnungIssues(inv), id => id.StartsWith("BR-DE-2", StringComparison.Ordinal));
    }

    [Fact]
    public void ACardNextToTheSellersIbanIsMeans48WithoutTheAccount()
    {
        var (inv, _) = Map(Doc(""", "paymentCardNumber": "1234", "paymentCardHolder": "E. Muster" """));
        var p = inv.PaymentInstructions!;
        Assert.Equal("48", p.MeansCode);
        Assert.Empty(p.CreditTransfers);
        Assert.NotNull(p.Card);
        Assert.DoesNotContain(XRechnungIssues(inv), id => id.StartsWith("BR-DE-2", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitTransferCodeDropsTheDirectDebitWithAWarning()
    {
        var (inv, warnings) = Map(Doc(DirectDebit + """, "paymentMeansCode": "58" """));
        var p = inv.PaymentInstructions!;
        Assert.Equal("58", p.MeansCode);
        Assert.Single(p.CreditTransfers);
        Assert.Null(p.DirectDebit);
        Assert.Contains(warnings, w => w.Contains("direct debit (BG-19)"));
        Assert.DoesNotContain(XRechnungIssues(inv), id => id.StartsWith("BR-DE-2", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAttachmentThatCannotBeReadIsAWarningNotAnError()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hivebdc-locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "locked.pdf");
        File.WriteAllBytes(file, "%PDF-1.4\n"u8.ToArray());
        FileStream? hold = null;
        try
        {
            if (OperatingSystem.IsWindows()) hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else File.SetUnixFileMode(file, UnixFileMode.None);
            var (inv, warnings) = Map(Doc(""", "supportingDocuments": [ { "reference": "ANL-1", "fileName": "locked.pdf" } ] """), folder);
            var doc = Assert.Single(inv.SupportingDocuments);
            Assert.Equal("ANL-1", doc.Reference);
            Assert.Null(doc.Content);
            Assert.Contains(warnings, w => w.Contains("cannot be read"));
        }
        finally
        {
            hold?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheMimeCodeIgnoresCase()
    {
        var (inv, warnings) = Map(Doc(""", "supportingDocuments": [ { "reference": "ANL-1", "content": "JVBERi0xLjQK", "mimeCode": "Application/PDF" } ] """));
        Assert.NotNull(Assert.Single(inv.SupportingDocuments).Content);
        Assert.DoesNotContain(warnings, w => w.Contains("not allowed"));
    }

    [Theory]
    [InlineData("""{ "amount": "-10", "isCharge": "true" }""", true, false)]
    [InlineData("""{ "amount": "10", "isCharge": "ja" }""", true, false)]
    [InlineData("""{ "amount": "10", "isCharge": "false" }""", false, false)]
    [InlineData("""{ "chargeAmount": "5", "isCharge": "false" }""", true, true)]
    [InlineData("""{ "allowanceAmount": "5", "isCharge": "true" }""", false, true)]
    [InlineData("""{ "amount": "10" }""", true, false)]
    [InlineData("""{ "amount": "-10" }""", false, false)]
    public void OnAnInvoiceTheKeyThenIsChargeThenTheSignDecide(string entry, bool charge, bool contradiction)
    {
        var (inv, warnings) = Map(Doc($", \"allowancesCharges\": [ {entry} ]"));
        var ac = Assert.Single(inv.AllowancesAndCharges);
        Assert.Equal(charge, ac.IsCharge);
        Assert.True(ac.Amount > 0m);
        Assert.Equal(contradiction, warnings.Any(w => w.Contains("contradicts")));
    }

    [Fact]
    public void AYesNoValueThatIsNeitherIsAWarning()
    {
        var (inv, warnings) = Map(Doc(""", "allowancesCharges": [ { "amount": "10", "isCharge": "maybe" } ] """));
        Assert.True(Assert.Single(inv.AllowancesAndCharges).IsCharge);
        Assert.Contains(warnings, w => w.Contains("'maybe' is not true or false"));
    }

    [Fact]
    public void OnACreditNoteTheSignOfAnAmountSaysNothing()
    {
        var (inv, warnings) = Map(Doc("""
            , "allowancesCharges": [ { "allowanceAmount": "-10" }, { "chargeAmount": "-3" }, { "amount": "-4", "isCharge": "false" }, { "amount": "-2" } ]
            """, typeCode: "381"));
        Assert.Equal(new[] { (false, 10m), (true, 3m), (false, 4m) }, inv.AllowancesAndCharges.Select(a => (a.IsCharge, a.Amount!.Value)));
        Assert.Contains(warnings, w => w.Contains("entry 4") && w.Contains("send isCharge"));
    }

    [Theory]
    [InlineData("381", """, "lineDiscountBaseAmount": "-300", "lineDiscountPercent": "10" """)]
    [InlineData("381", """, "lineDiscountAmount": "-30" """)]
    [InlineData("380", """, "lineDiscountBaseAmount": "300", "lineDiscountPercent": "10" """)]
    public void ALineDiscountStaysAnAllowance(string typeCode, string lineExtra)
    {
        var (inv, _) = Map(Doc(lineExtra: lineExtra, typeCode: typeCode));
        var ac = Assert.Single(inv.Lines.Single().AllowancesAndCharges);
        Assert.False(ac.IsCharge);
        Assert.Equal(30m, ac.Amount);
    }

    [Theory]
    [InlineData("3", "5")]
    [InlineData("35", "29")]
    [InlineData("432", "72")]
    [InlineData("29", "29")]
    public void TheVatPointCodeIsWrittenInTheCiiCodeList(string sent, string written)
    {
        var (inv, _) = Map(Doc($", \"taxPointDateCode\": \"{sent}\""));
        Assert.Equal(written, inv.TaxPointDateCode);
        Assert.DoesNotContain("BR-CL-06", XRechnungIssues(inv));
    }

    [Theory]
    [InlineData("invoiceNotes", true)]
    [InlineData("precedingInvoices", true)]
    [InlineData("supportingDocuments", true)]
    [InlineData("itemAttributes", false)]
    public void AListKeySentAsTextIsAWarningExceptItemAttributes(string key, bool warned)
    {
        var doc = key == "itemAttributes"
            ? Doc(lineExtra: $", \"{key}\": \"Colour: Blue\"")
            : Doc($", \"{key}\": \"RE-0\"");
        var (_, warnings) = Map(doc);
        Assert.Equal(warned, warnings.Any(w => w.Contains($"'{key}' is not a list")));
    }

    [Theory]
    [InlineData("U12345678", "AT", "ATU12345678")]
    [InlineData("atu12345678", "AT", "ATU12345678")]
    [InlineData("123 456 789", "DE", "DE123456789")]
    [InlineData("094259216", "GR", "EL094259216")]
    [InlineData("NL123456789B01", "DE", "NL123456789B01")]
    public void AVatIdentifierCarriesItsCountryPrefix(string sent, string country, string written)
    {
        var (inv, _) = Map(Doc($", \"recipientVatId\": \"{sent}\"", buyerCountry: country));
        Assert.Equal(written, inv.Buyer.VatIdentifier);
        Assert.DoesNotContain("BR-CO-09", XRechnungIssues(inv));
    }

    [Theory]
    [InlineData("E")]
    [InlineData("Z")]
    [InlineData("AE")]
    [InlineData("K")]
    [InlineData("G")]
    public void AZeroRateCategoryDoesNotInheritTheHeaderRate(string category)
    {
        var (inv, warnings) = Map(Doc(", \"vatNote\": \"Grund\"", lineExtra: $", \"vatCategory\": \"{category}\""));
        Assert.Equal(0m, inv.Lines.Single().VatRate);
        Assert.DoesNotContain(warnings, w => w.Contains("has the rate 0"));
        Assert.DoesNotContain(XRechnungIssues(inv), id => id.EndsWith("-05", StringComparison.Ordinal));
    }

    [Fact]
    public void AZeroRateCategoryWithItsOwnRateIsAWarning()
    {
        var (inv, warnings) = Map(Doc(lineExtra: """, "vatCategory": "E", "vatRate": "19" """));
        Assert.Equal(0m, inv.Lines.Single().VatRate);
        Assert.Contains(warnings, w => w.Contains("has the rate 0, not 19"));
    }

    [Fact]
    public void AnIntraCommunitySupplyGetsTheBuyersCountryAsDeliverTo()
    {
        var (inv, _) = Map(Doc(""", "recipientVatId": "FR12345678901" """, lineExtra: """, "vatCategory": "K" """, buyerCountry: "FR"));
        Assert.Equal("FR", inv.Delivery?.Address?.CountryCode);
        Assert.DoesNotContain("BR-IC-12", XRechnungIssues(inv));
    }

    [Theory]
    [InlineData("4000001123452", "0088", false)]
    [InlineData("rechnung@example.com", "EM", false)]
    [InlineData("DE-ABC-1", null, true)]
    public void TheSellerElectronicAddressGetsItsScheme(string address, string? scheme, bool warned)
    {
        var (inv, warnings) = Map(Doc($", \"sellerElectronicAddress\": \"{address}\""));
        if (scheme is null) Assert.Equal("rechnung@example.com", inv.Seller.ElectronicAddress?.Value);
        else Assert.Equal((address, scheme), (inv.Seller.ElectronicAddress?.Value, inv.Seller.ElectronicAddress?.Scheme));
        Assert.Equal(warned, warnings.Any(w => w.Contains("needs its scheme")));
        Assert.DoesNotContain("BR-62", XRechnungIssues(inv));
    }

    [Theory]
    [InlineData("05.03.2026", 2026, 3, 5)]
    [InlineData("5.3.2026", 2026, 3, 5)]
    [InlineData("2026-03-05", 2026, 3, 5)]
    [InlineData("2026-03-05T00:00:00", 2026, 3, 5)]
    [InlineData("20260305", 2026, 3, 5)]
    [InlineData("2026/03/05", 2026, 3, 5)]
    public void AGermanDateIsReadDayFirst(string sent, int year, int month, int day)
    {
        var doc = AxDocument.Parse(Doc().ToString() is { } _ ? DocJson().Replace("\"issueDate\": \"2026-09-22\"", $"\"issueDate\": \"{sent}\"") : "");
        Assert.Equal(new DateTime(year, month, day), InvoiceMapper.Map(doc).IssueDate);
    }

    [Theory]
    [InlineData("AUT", "AT", false)]
    [InlineData("USA", "US", false)]
    [InlineData("Österreich", "AT", false)]
    [InlineData("Atlantis", "DE", true)]
    public void ACountryIsReadAsCodeNameOrAlpha3(string sent, string written, bool warned)
    {
        var (inv, warnings) = Map(Doc(buyerCountry: sent));
        Assert.Equal(written, inv.Buyer.Address.CountryCode);
        Assert.Equal(warned, warnings.Any(w => w.Contains($"recipientCountry '{sent}'")));
    }

    [Fact]
    public void ALeitwegIdInReferenceIsNoPurchaseOrder()
    {
        var doc = AxDocument.Parse(DocJson().Replace("\"buyerReference\": \"04011000-12345-67\",", "\"reference\": \"991-12345-67\","));
        var inv = InvoiceMapper.Map(doc);
        Assert.Equal("991-12345-67", inv.BuyerReference);
        Assert.Null(inv.PurchaseOrderReference);
    }

    // The delivery element must not be empty in CII (PEPPOL-EN16931-R008): the issue date stays BT-72 also next to a period.
    [Theory]
    [InlineData("")]
    [InlineData(", \"servicePeriodFrom\": \"2026-09-01\", \"servicePeriodTo\": \"2026-09-30\"")]
    public void TheIssueDateIsTheDeliveryDateWhenNoneIsSent(string extra)
    {
        var (inv, _) = Map(Doc(extra));
        Assert.Equal(new DateTime(2026, 9, 22), inv.Delivery?.ActualDeliveryDate);
        Assert.Contains("<ram:ActualDeliverySupplyChainEvent>", System.Text.Encoding.UTF8.GetString(EInvoiceGenerator.BuildCiiXml(inv, InvoiceProfile.XRechnung)));
    }

    [Fact]
    public void EachExemptCategoryGetsItsOwnReason()
    {
        var doc = AxDocument.Parse(DocJson().Replace("""
            "lineItems": [ { "description": "A", "quantity": "2", "unit": "piece", "unitPrice": "150.00" } ]
            """.Trim(), """
            "vatNote": "Steuerfrei nach § 4 Nr. 11 UStG", "recipientVatId": "FR12345678901",
            "lineItems": [ { "description": "A", "quantity": "1", "unit": "piece", "unitPrice": "100.00", "vatCategory": "E", "vatRate": "0" },
                           { "description": "B", "quantity": "1", "unit": "piece", "unitPrice": "50.00", "vatCategory": "K", "vatRate": "0" } ]
            """.Trim()));
        var warnings = new List<string>();
        var inv = InvoiceMapper.Map(doc, warnings.Add);
        Assert.Equal("Steuerfrei nach § 4 Nr. 11 UStG", inv.VatExemptions.Single(e => e.Category == VatCategory.Exempt).Reason);
        Assert.Equal("Intra-community supply", inv.VatExemptions.Single(e => e.Category == VatCategory.IntraCommunitySupply).Reason);
        Assert.Contains(warnings, w => w.Contains("apply to E only"));
    }

    [Fact]
    public void OppositeLineSignsOnAnAbsoluteDocumentAreAWarning()
    {
        var doc = AxDocument.Parse(DocJson().Replace("\"invoiceTypeCode\": \"380\"", "\"invoiceTypeCode\": \"381\"").Replace("""
            "lineItems": [ { "description": "A", "quantity": "2", "unit": "piece", "unitPrice": "150.00" } ]
            """.Trim(), """
            "lineItems": [ { "description": "A", "quantity": "-1", "unit": "piece", "unitPrice": "100.00" },
                           { "description": "Fee", "quantity": "1", "unit": "piece", "unitPrice": "20.00" } ]
            """.Trim()));
        var warnings = new List<string>();
        InvoiceMapper.Map(doc, warnings.Add);
        Assert.Contains(warnings, w => w.Contains("lines with opposite signs") && w.Contains("120") && w.Contains("80"));
    }
}