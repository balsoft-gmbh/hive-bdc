using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The HTTPS target through a full scan against a real endpoint, for example a Logic App whose HTTP trigger answers
/// {"id": "run|documentNumber|bytes|previousId"}. HIVEBDC_HTTPS_TRIGGER_URL holds the trigger URL (kept as the
/// connection secret, as a user would).
/// </summary>
public sealed class HttpsTargetLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-https-live-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;

    public HttpsTargetLiveTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [HttpsLiveFact]
    public void ARealEndpointGetsTheDocumentAndTheRepeatCarriesItsAnsweredId()
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        var route = new DocumentRoute
        {
            DataAreaId = "A", Input = input, Output = Path.Combine(input, "out"),
            Targets = { new TargetOptions { Kind = "https", Connection = "logicapp", Fields = { ["number"] = "{documentNumber}" } } },
        };
        var options = new BDCOptions
        {
            Connections = { new ConnectionOptions { Id = "logicapp", Kind = "https", Secret = Environment.GetEnvironmentVariable("HIVEBDC_HTTPS_TRIGGER_URL")! } },
            DocumentTypes = { new DocumentTypeOptions { Id = "PurchaseOrder", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { route } } },
        };
        options.Normalize();
        var license = new LicenseResult { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };
        var processor = new DocumentProcessor(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger);
        void Pair()
        {
            File.WriteAllText(Path.Combine(input, "order_1.json"), """{"documentType":"PurchaseOrder","dataAreaId":"A","documentNumber":"HTTPS-CHECK-1"}""");
            File.WriteAllText(Path.Combine(input, "order_1.pdf"), "%PDF-1.4 Hive BDC HTTPS check");
        }

        Pair();
        Assert.Equal(1, processor.ScanOnce(options, license, null, null).Processed);
        Pair();
        Assert.Equal(1, processor.ScanOnce(options, license, null, null).Processed);

        var delivered = _ledger.Events("HTTPS-CHECK-1", 50).Where(e => e.Step == "delivered").Select(e => e.Message).ToList();
        Assert.Equal(2, delivered.Count);
        Assert.Contains("|HTTPS-CHECK-1|29|none)", delivered[0]);
        string firstRun = delivered[0][(delivered[0].IndexOf(", id ", StringComparison.Ordinal) + 5)..].Split('|')[0];
        Assert.Contains($"|HTTPS-CHECK-1|29|{firstRun}|HTTPS-CHECK-1|29|none)", delivered[1]);
        Assert.DoesNotContain(delivered, m => m.Contains("sig="));
    }
}

public sealed class HttpsLiveFactAttribute : FactAttribute
{
    public HttpsLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIVEBDC_HTTPS_TRIGGER_URL")))
            Skip = "Set HIVEBDC_HTTPS_TRIGGER_URL (an HTTPS trigger that answers {\"id\": ...}) to run this integration test.";
    }
}