using System.Text.Json;
using System.Xml.Linq;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.EInvoice;
using Balsoft.Hive.EInvoice;
using Xunit;
using Xunit.Abstractions;

namespace Balsoft.Hive.BDC.Tests;

public sealed class ReplayFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("HIVE_BDC_REPLAY");

    public ReplayFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
            Skip = "Set HIVE_BDC_REPLAY to a folder of real ERP invoice JSON files to replay them.";
    }
}

/// <summary>
/// Maps real ERP exports kept outside the repository and checks the bank accounts: one payment means per distinct
/// IBAN of the account list (else the flat bankIban), one means code for all. With HIVE_VALIDATORS set the XML also
/// goes through KoSIT, which must report nothing about payment means; other findings depend on the export data.
/// </summary>
public class BankAccountsReplayTests
{
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";

    private static readonly HashSet<string> PaymentRules = new(StringComparer.Ordinal)
    {
        "BR-49", "BR-50", "BR-61", "BR-DE-1", "BR-DE-19", "BR-DE-23-a", "BR-DE-23-b", "CII-SR-467", "CII-SR-468", "CII-SR-470",
    };

    private readonly ITestOutputHelper _output;

    public BankAccountsReplayTests(ITestOutputHelper output) => _output = output;

    [ReplayFact]
    public void RealExportsGetOnePaymentMeansPerAccount()
    {
        var files = Directory.GetFiles(ReplayFactAttribute.Folder!, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        string xmlFolder = Path.Combine(Path.GetTempPath(), "hive-bdc-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(xmlFolder);
        var failures = new List<string>();
        var xmlFiles = new List<string>();
        try
        {
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string json = File.ReadAllText(file);
                int expected = ExpectedAccounts(json, out bool listed);
                var invoice = InvoiceMapper.Map(AxDocument.Parse(json));
                var accounts = invoice.PaymentInstructions?.CreditTransfers ?? new List<CreditTransferAccount>();
                byte[] xml = EInvoiceGenerator.BuildCiiXml(invoice, InvoiceProfile.XRechnung);
                var means = XDocument.Parse(System.Text.Encoding.UTF8.GetString(xml)).Descendants(Ram + "SpecifiedTradeSettlementPaymentMeans").ToList();
                var codes = means.Select(m => m.Element(Ram + "TypeCode")?.Value).Distinct().ToList();
                _output.WriteLine($"{name}: {(listed ? "list" : "flat")}, {accounts.Count} account(s), {means.Count} payment means, code {string.Join("/", codes)}");
                if (accounts.Count != expected || means.Count != expected || codes.Count > 1)
                    failures.Add($"{name}: expected {expected} accounts, mapped {accounts.Count}, XML {means.Count}, codes {string.Join("/", codes)}");
                string xmlFile = Path.Combine(xmlFolder, name + ".xml");
                File.WriteAllBytes(xmlFile, xml);
                xmlFiles.Add(xmlFile);
            }

            if (ExternalValidators.Directory is { } tools)
                foreach (var (name, outcome) in ExternalValidators.Kosit(tools, xmlFiles))
                {
                    var payment = outcome.Findings.Where(f => PaymentRules.Contains(f.Code)).ToList();
                    _output.WriteLine($"{name}: KoSIT {(outcome.Accepted ? "ACCEPT" : "REJECT")} [{string.Join(", ", outcome.Findings.Select(f => f.Code).Distinct())}]");
                    if (payment.Count > 0)
                        failures.Add($"{name}: KoSIT payment findings {string.Join(", ", payment.Select(f => f.Code))}");
                }
        }
        finally
        {
            try { Directory.Delete(xmlFolder, true); } catch (IOException) { }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>The distinct IBANs of the export's account list, else 1 for a flat bankIban, else 0; read straight from the JSON.</summary>
    private static int ExpectedAccounts(string json, out bool listed)
    {
        using var doc = JsonDocument.Parse(json, AxDocument.DocumentOptions);
        var root = doc.RootElement;
        var ibans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("bankAccounts", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in property.Value.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("bankIban", out var iban) && iban.ValueKind == JsonValueKind.String
                    && Iban.Compact(iban.GetString()!) is { Length: > 0 } compact)
                    ibans.Add(compact);
        }
        listed = ibans.Count > 0;
        if (listed) return ibans.Count;
        return root.TryGetProperty("bankIban", out var flat) && flat.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(flat.GetString()) ? 1 : 0;
    }
}