using System.Diagnostics;
using System.Xml.Linq;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice;
using Balsoft.Hive.EInvoice.Cii;
using Balsoft.Hive.EInvoice.Ubl;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>An invoice JSON that is complete for XRechnung, with the documented example IBANs of the IBAN standard and the KoSIT test suite.</summary>
public static class BankAccountSamples
{
    public static readonly string[] Ibans =
    {
        "DE89370400440532013000", "DE02120300000000202051", "GB82WEST12345698765432", "NL91ABNA0417164300", "AT611904300234573201",
    };

    public const string WrongCheckDigits = "DE89370400440532013001";

    public static readonly string[] Bics = { "MUSTDEFFXXX", "BEISDEB1XXX", "TESTGB2LXXX", "SAMPNL2AXXX", "DEMOATWWXXX" };

    public static string Entry(int index, bool bic = true, bool holder = true)
        => "{ \"bankIban\": \"" + Ibans[index] + "\""
           + (bic ? ", \"bankSwift\": \"" + Bics[index] + "\"" : "")
           + (holder ? ", \"bankAccountHolder\": \"Muster Lieferant GmbH\"" : "")
           + ", \"bankName\": \"Musterbank " + (index + 1) + "\" }";

    public static string List(int count) => "\"bankAccounts\": [ " + string.Join(", ", Enumerable.Range(0, count).Select(i => Entry(i))) + " ]";

    public static string Flat(int index = 0)
        => "\"bankIban\": \"" + Ibans[index] + "\", \"bankSwift\": \"" + Bics[index] + "\", \"bankAccountHolder\": \"Muster Lieferant GmbH\", \"bankName\": \"Musterbank 1\"";

    /// <summary>Flat keys for account 1 plus the list, as the ERP exports it; 0 = no bank at all.</summary>
    public static string Export(int count) => count == 0 ? "" : count == 1 ? Flat() : Flat() + ", " + List(count);

    public static AxDocument Invoice(string bank, string typeCode = "380") => AxDocument.Parse($$"""
        {
          "documentType": "SalesInvoice", "documentNumber": "RE-2026-0101", "eInvoiceType": "XRechnung", "invoiceTypeCode": "{{typeCode}}",
          "issueDate": "2026-11-19", "dueDate": "2026-12-19", "currency": "EUR", "vatRate": "19",
          "buyerReference": "04011000-12345-34", "paymentReference": "RE-2026-0101",
          "senderName": "Muster Lieferant GmbH", "senderStreet": "Musterweg 1", "senderPostcode": "50667", "senderCity": "Köln",
          "senderCountry": "DE", "senderVatId": "DE123456789", "senderEmail": "rechnung@lieferant.example", "senderPhone": "+49 221 123456",
          "recipientName": "Beispiel Kunde AG", "recipientStreet": "Beispielstr. 2", "recipientPostcode": "10115", "recipientCity": "Berlin",
          "recipientCountry": "DE", "recipientEmail": "eingang@kunde.example",
          {{(bank.Length == 0 ? "" : bank + ",")}}
          "lineItems": [ { "description": "Beratung", "quantity": "2", "unit": "hour", "unitPrice": "150.00" } ]
        }
        """);
}

/// <summary>
/// Runs the KoSIT validator (validator.jar with the XRechnung configuration in xrechnung/) from the directory in
/// HIVE_VALIDATORS, as the Balsoft.Hive.EInvoice tests do; eng/get-validators.sh in that repository downloads it.
/// </summary>
public static class ExternalValidators
{
    public static string? Directory
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("HIVE_VALIDATORS");
            return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "validator.jar")) ? dir : null;
        }
    }

    public sealed record Finding(string Level, string Code, string Text);

    public sealed record Outcome(bool Accepted, IReadOnlyList<Finding> Findings)
    {
        public override string ToString()
            => (Accepted ? "ACCEPT" : "REJECT") + Environment.NewLine + string.Join(Environment.NewLine, Findings.Select(f => $"  {f.Level} {f.Code}: {f.Text}"));
    }

    /// <summary>Validates every file in one JVM run; returns the outcome per file name (without extension).</summary>
    public static Dictionary<string, Outcome> Kosit(string dir, IReadOnlyCollection<string> files)
    {
        string reports = Path.Combine(Path.GetTempPath(), "hive-bdc-kosit-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(reports);
        var args = new List<string>
        {
            "-jar", Path.Combine(dir, "validator.jar"),
            "-s", Path.Combine(dir, "xrechnung", "scenarios.xml"),
            "-r", Path.Combine(dir, "xrechnung"),
            "-o", reports,
        };
        args.AddRange(files);
        Run("java", args);

        var result = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string report = Path.Combine(reports, name + "-report.xml");
            if (!File.Exists(report))
            {
                result[name] = new Outcome(false, new[] { new Finding("error", "NO-REPORT", "KoSIT produced no report (no matching scenario?)") });
                continue;
            }
            XNamespace rep = "http://www.xoev.de/de/validator/varl/1";
            var doc = XDocument.Load(report);
            bool accepted = doc.Descendants(rep + "accept").Any();
            var findings = doc.Descendants(rep + "message")
                .Select(m => new Finding((string?)m.Attribute("level") ?? "", (string?)m.Attribute("code") ?? "", m.Value.Trim()))
                .ToList();
            result[name] = new Outcome(accepted, findings);
        }
        try { System.IO.Directory.Delete(reports, true); } catch (IOException) { }
        return result;
    }

    private static void Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = err.Result;
    }
}

public sealed class KositTheoryAttribute : TheoryAttribute
{
    public KositTheoryAttribute()
    {
        if (ExternalValidators.Directory is null) Skip = "Set HIVE_VALIDATORS to the KoSIT validator folder (Balsoft.Hive.EInvoice eng/get-validators.sh) to run this check.";
    }
}

/// <summary>Writes Hive BDC's e-invoice for 0, 1, 2 and 4 bank accounts in CII and UBL once and validates all of them with KoSIT.</summary>
public sealed class BankAccountsKositFixture
{
    public static readonly int[] Counts = { 0, 1, 2, 4 };

    public static readonly string[] Syntaxes = { "cii", "ubl" };

    public string OutputDirectory { get; } = Path.Combine(Path.GetTempPath(), "hive-bdc-bank-accounts-" + Guid.NewGuid().ToString("N"));

    public Dictionary<string, ExternalValidators.Outcome>? Outcomes { get; }

    public BankAccountsKositFixture()
    {
        if (ExternalValidators.Directory is not { } tools) return;
        System.IO.Directory.CreateDirectory(OutputDirectory);
        var files = new List<string>();
        foreach (var syntax in Syntaxes)
            foreach (int count in Counts)
            {
                var invoice = InvoiceMapper.Map(BankAccountSamples.Invoice(BankAccountSamples.Export(count)));
                string file = Path.Combine(OutputDirectory, $"{syntax}-{count}.xml");
                File.WriteAllBytes(file, syntax == "ubl"
                    ? UblWriter.Write(invoice, InvoiceProfile.XRechnung, new WriterOptions { ValidateBeforeWriting = false })
                    : EInvoiceGenerator.BuildCiiXml(invoice, InvoiceProfile.XRechnung));
                files.Add(file);
            }
        Outcomes = ExternalValidators.Kosit(tools, files);
    }
}

public sealed class BankAccountsKositTests : IClassFixture<BankAccountsKositFixture>
{
    private readonly BankAccountsKositFixture _fx;

    public BankAccountsKositTests(BankAccountsKositFixture fx) => _fx = fx;

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var syntax in BankAccountsKositFixture.Syntaxes)
            foreach (int count in BankAccountsKositFixture.Counts)
                data.Add(syntax, count);
        return data;
    }

    [KositTheory]
    [MemberData(nameof(Cases))]
    public void KositJudgesEveryAccountCount(string syntax, int count)
    {
        var outcome = _fx.Outcomes![$"{syntax}-{count}"];
        if (count == 0)
        {
            Assert.False(outcome.Accepted, outcome.ToString());
            Assert.Equal(new[] { "BR-DE-1" }, outcome.Findings.Select(f => f.Code).Distinct().ToArray());
            return;
        }
        Assert.True(outcome.Accepted, outcome.ToString());
        Assert.DoesNotContain(outcome.Findings, f => f.Level == "warning");
    }
}