using Balsoft.Hive.BDC.Documents.Word;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Word invoices for the tests, built with the template builder; buyer and seller are fictitious.</summary>
internal static class WordTestDocuments
{
    /// <summary>A partial construction invoice to a public buyer: 20 % of 10,000.00 plus 20 % of 2,500.00, 19 % VAT.</summary>
    public static WordInvoiceContent Filled()
    {
        var c = new WordInvoiceContent();
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["BT-1"] = "RE-2026-0042", ["BT-2"] = "2026-06-12", ["BT-3"] = "875", ["eInvoiceType"] = "xrechnung",
            ["BT-11"] = "P-4711", ["BT-10"] = "991-12345-67", ["BT-13"] = "BE-2026-77",
            ["BT-44"] = "Stadt Musterhausen Bauamt", ["BT-50"] = "Rathausplatz 1", ["BT-53"] = "54321", ["BT-52"] = "Musterhausen",
            ["BT-55"] = "DE", ["BT-49"] = "991-12345-67", ["BT-73"] = "2026-03-01", ["BT-74"] = "2026-04-30",
            ["BT-22"] = "Bauvorhaben Kita Sonnenweg\nHaus B", ["BT-9"] = "2026-07-10",
            ["BT-109"] = "2.500,00", ["BT-119"] = "19", ["BT-110"] = "475,00", ["BT-112"] = "2.975,00",
        })
            c.Fields[k] = v;
        c.Lines.Add(new() { ["BT-126"] = "1.0", ["BT-153"] = "Modulgebäude", ["BT-154"] = "4 Raummodule\n-netto- 10.000,00 EUR, davon 20 %",
            ["BT-129"] = "1", ["BT-130"] = "LS", ["BT-146"] = "2.000,00", ["BT-152"] = "19", ["BT-131"] = "2.000,00" });
        c.Lines.Add(new() { ["BT-126"] = "2.0", ["BT-153"] = "Nachtrag Nr. 1", ["BT-154"] = "Photovoltaik 10 kWp",
            ["BT-129"] = "1", ["BT-130"] = "LS", ["BT-146"] = "500,00", ["BT-152"] = "19", ["BT-131"] = "500,00" });
        return c;
    }

    public static byte[] Build(WordInvoiceContent? content = null)
    {
        using var ms = new MemoryStream();
        WordTemplateBuilder.Write(ms, content);
        return ms.ToArray();
    }

    public static WordInvoice Read(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        return WordInvoiceReader.Read(ms);
    }
}