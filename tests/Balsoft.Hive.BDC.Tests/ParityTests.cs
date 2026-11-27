using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.Cii;
using Balsoft.Hive.EInvoice.Reading;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// Hive BDC 2.2.0 moved e-invoice generation from ZUGFeRD-csharp to Balsoft.Hive.EInvoice. Every
/// fixture in Parity/Fixtures was run through Hive BDC 2.1.3 and its XML kept in Parity/Golden
/// (the generator reproduced a customer's production XML byte for byte). This test maps each fixture
/// with the current code and compares business content: both documents are read back into the
/// model and written again, so formatting and element order do not count, every value does.
/// Intended differences are applied to the golden side explicitly, each with its reason.
/// </summary>
public class ParityTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Parity");

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "Fixtures"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void NewOutputCarriesTheSameInvoiceAs213(string name)
    {
        var warnings = new List<string>();
        var doc = AxDocument.Parse(File.ReadAllText(Path.Combine(Root, "Fixtures", name + ".json")));
        var profile = EInvoiceGenerator.ProfileFor(doc);
        byte[] current = EInvoiceGenerator.BuildCiiXml(InvoiceMapper.Map(doc, warnings.Add), profile);
        byte[] golden = File.ReadAllBytes(Path.Combine(Root, "Golden", name + ".xml"));

        var now = CiiReader.Read(current);
        var then = CiiReader.Read(golden);
        ApplyIntendedDifferences(then.Invoice, profile);

        Assert.Equal(Canonical(then.Invoice), Canonical(now.Invoice));
        Assert.Empty(now.TotalsDiscrepancies());

        // The mapper's own warnings are unchanged.
        var goldenWarnings = File.ReadAllLines(Path.Combine(Root, "Golden", name + ".warnings.txt"));
        Assert.Equal(goldenWarnings, warnings);
    }

    /// <summary>What 2.2.0 writes differently on purpose.</summary>
    private static void ApplyIntendedDifferences(Invoice golden, InvoiceProfile profile)
    {
        // 0. ZUGFeRD is Factur-X profile EN 16931 since 1.15.1 and carries no BT-23 (design 2026-11-27).
        if (profile == InvoiceProfile.EN16931) golden.BusinessProcess = null;

        // 1. One buyer identifier (BT-46 occurs once). 2.1.3 wrote the Anschlusshaus number AND the
        //    GLN, which triggers CII-SR-450; Aktivbank needs one of them and validates the number.
        if (golden.Buyer.Identifiers.Count > 1)
        {
            var first = golden.Buyer.Identifiers[0];
            golden.Buyer.Identifiers.Clear();
            golden.Buyer.Identifiers.Add(first);
        }

        // 1b. A buyer VAT identifier without its country prefix gets it (BR-CO-09, review 2026-11-27).
        if (golden.Buyer.VatIdentifier is { } vat && vat.Length >= 2 && !(char.IsAsciiLetter(vat[0]) && char.IsAsciiLetter(vat[1])))
            golden.Buyer.VatIdentifier = golden.Buyer.Address.CountryCode + vat;

        // 2. BT-20 groups the #SKONTO# lines after the readable text instead of interleaving them;
        //    the reader separates both, so no adjustment is needed here (listed for completeness).
    }

    private static string Canonical(Invoice invoice)
        => CiiWriter.WriteToString(invoice, InvoiceProfile.XRechnung, new WriterOptions { ValidateBeforeWriting = false });
}
