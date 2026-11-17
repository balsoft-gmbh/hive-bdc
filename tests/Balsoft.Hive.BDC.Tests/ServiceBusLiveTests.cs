using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// Service Bus target and source against a real, empty queue (the managed identity path: namespace plus the sign-in of
/// the machine). HIVEBDC_SB_NAMESPACE, HIVEBDC_SB_QUEUE, HIVEBDC_SB_PDF_ACCOUNT (https://{account}.blob.core.windows.net)
/// and HIVEBDC_SB_PDF_CONTAINER; the sign-in needs data rights on the queue and the container.
/// </summary>
public sealed class ServiceBusLiveTests : IDisposable
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-sb-live-" + Guid.NewGuid());
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly ExportLedger _ledger;
    private static readonly LicenseResult License = new() { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

    public ServiceBusLiveTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static BDCOptions Options(params DocumentTypeOptions[] types)
    {
        var options = new BDCOptions
        {
            Connections =
            {
                new ConnectionOptions { Id = "bus", Kind = "servicebus", Settings = { ["Namespace"] = Env("HIVEBDC_SB_NAMESPACE") } },
                new ConnectionOptions { Id = "pdfs", Kind = "azure-storage", Settings = { ["AccountUrl"] = Env("HIVEBDC_SB_PDF_ACCOUNT") } },
            },
        };
        foreach (var t in types) options.DocumentTypes.Add(t);
        options.Normalize();
        return options;
    }

    private DocumentProcessor Processor() => new(NullLogger.Instance, new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger);

    [ServiceBusLiveFact]
    public async Task TheTargetSendsAndTheSourceTakesCompletesAndDeadLettersOnARealQueue()
    {
        string queue = Env("HIVEBDC_SB_QUEUE");
        await using var client = new ServiceBusClient(Env("HIVEBDC_SB_NAMESPACE"), new DefaultAzureCredential());

        // Target: an outbound document becomes one JSON message with a stable id.
        string outbound = Path.Combine(_root, "out-in");
        Directory.CreateDirectory(outbound);
        var sendRoute = new DocumentRoute
        {
            DataAreaId = "USMF", Input = outbound, Output = Path.Combine(outbound, "out"),
            Targets = { new TargetOptions { Kind = "servicebus", Connection = "bus", Settings = { ["Queue"] = queue } } },
        };
        File.WriteAllText(Path.Combine(outbound, $"t{_run}_1.json"), $$"""{"documentType":"SalesConfirmation","dataAreaId":"USMF","documentNumber":"SB-OUT-{{_run}}"}""");
        File.WriteAllText(Path.Combine(outbound, $"t{_run}_1.pdf"), "%PDF-1.4 out");
        Assert.Equal(1, Processor().ScanOnce(Options(new DocumentTypeOptions { Id = "SalesConfirmation", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { sendRoute } }), License, null, null).Processed);
        await using (var reader = client.CreateReceiver(queue))
        {
            var sent = await reader.ReceiveMessageAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(sent);
            Assert.Equal($"SalesConfirmation_USMF_t{_run}", sent!.MessageId);
            var body = (JsonObject)JsonNode.Parse(sent.Body.ToString())!;
            Assert.Equal($"SB-OUT-{_run}", (string)body["documentNumber"]!);
            Assert.Equal("%PDF-1.4 out", Encoding.UTF8.GetString(Convert.FromBase64String((string)body["content"]!)));
            await reader.CompleteMessageAsync(sent);
        }

        // Source: two F&O messages with a pdfLink; one is processed and completed, one fails and is dead-lettered.
        var store = AzureBlobStore.For(new ConnectionOptions { Kind = "azure-storage", Settings = { ["AccountUrl"] = Env("HIVEBDC_SB_PDF_ACCOUNT") } }, Env("HIVEBDC_SB_PDF_CONTAINER"));
        string pdf = $"hivebdc-sb-check/{_run}.pdf";
        store.Upload(pdf, Encoding.ASCII.GetBytes("%PDF-1.4 from F&O"), "application/pdf");
        try
        {
            string link = store.ContainerUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + pdf;
            await using (var sender = client.CreateSender(queue))
            {
                foreach (var (id, type, number) in new[] { ($"ok{_run}", "SalesConfirmation", "SB-OK"), ($"err{_run}", "Disabled", "SB-ERR") })
                    await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(new { documentType = type, dataAreaId = "USMF", documentNumber = number, pdfLink = link })) { MessageId = id });
            }
            string inbound = Path.Combine(_root, "in");
            var takeRoute = new DocumentRoute
            {
                DataAreaId = "USMF", Input = inbound, Output = Path.Combine(inbound, "out"),
                Source = new SourceOptions { Kind = "servicebus", Connection = "bus", Settings = { ["Queue"] = queue, ["PdfConnection"] = "pdfs", ["PdfContainer"] = Env("HIVEBDC_SB_PDF_CONTAINER") } },
            };
            var options = Options(
                new DocumentTypeOptions { Id = "SalesConfirmation", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { takeRoute } },
                new DocumentTypeOptions { Id = "Disabled", Handler = DocumentHandlerKind.Pdf, Mail = false, Enabled = false, Companies = { new DocumentRoute { DataAreaId = "USMF", Input = inbound } } });
            var stager = new ServiceBusStager(NullLogger.Instance, _ledger);

            for (int i = 0; i < 5 && Directory.Exists(inbound) is false || Directory.GetFiles(inbound, "*_sb.json").Length < 2; i++)
                stager.Run(options);
            Assert.Equal("%PDF-1.4 from F&O", File.ReadAllText(Path.Combine(inbound, $"ok{_run}_sb.pdf")));
            var scan = Processor().ScanOnce(options, License, null, null);
            Assert.Equal((1, 1), (scan.Processed, scan.Errors));
            stager.Run(options);

            Assert.Empty(Directory.GetFiles(Path.Combine(inbound, ".hive-sources")));
            await using var deadLetters = client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            var dead = await deadLetters.ReceiveMessageAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(dead);
            Assert.Equal(($"err{_run}", "HiveBDC.DocumentError"), (dead!.MessageId, dead.DeadLetterReason));
            Assert.False(string.IsNullOrWhiteSpace(dead.DeadLetterErrorDescription));
            await deadLetters.CompleteMessageAsync(dead);
            await using var rest = client.CreateReceiver(queue);
            Assert.Null(await rest.PeekMessageAsync());
        }
        finally
        {
            if (store.Exists(pdf)) store.Move(pdf, pdf + ".deleted");
            new Azure.Storage.Blobs.BlobContainerClient(new Uri(Env("HIVEBDC_SB_PDF_ACCOUNT").TrimEnd('/') + "/" + Env("HIVEBDC_SB_PDF_CONTAINER")), new DefaultAzureCredential())
                .DeleteBlobIfExists(pdf + ".deleted");
        }
    }
}

public sealed class ServiceBusLiveFactAttribute : FactAttribute
{
    public ServiceBusLiveFactAttribute()
    {
        if (new[] { "HIVEBDC_SB_NAMESPACE", "HIVEBDC_SB_QUEUE", "HIVEBDC_SB_PDF_ACCOUNT", "HIVEBDC_SB_PDF_CONTAINER" }
            .Any(n => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))))
            Skip = "Set HIVEBDC_SB_NAMESPACE, _QUEUE, _PDF_ACCOUNT and _PDF_CONTAINER (an empty queue the sign-in may use) to run this integration test.";
    }
}