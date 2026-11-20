using System.Text;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// Inbound flows into a real container and a real, empty queue (design 2026-11-20, failure cases 1, 2 and 7), with the
/// environment of <see cref="ServiceBusLiveTests"/>. The test deletes the blobs it wrote and takes its message.
/// </summary>
public sealed class InboundLiveTests : IDisposable
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-inbound-live-" + Guid.NewGuid());
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly ExportLedger _ledger;
    private readonly Source _source = new();
    private static readonly LicenseResult License = new() { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

    public InboundLiveTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Source : IRecordSource
    {
        public readonly List<string> Marked = new();

        public SourceDescriptor Descriptor { get; } = new("live", "Live DMS", "", "", "documents", "document", "DMS", "{ID}.csv", Array.Empty<SourceSetting>());
        public IReadOnlyList<string> Problems(SourceOptions source) => Array.Empty<string>();
        public Task<IReadOnlyList<string>> ReadyAsync(SourceOptions source, int max)
            => Task.FromResult<IReadOnlyList<string>>(Marked.Contains("71") ? Array.Empty<string>() : new[] { "71" });
        public Task<SourceRecord> ReadAsync(SourceOptions source, string id)
            => Task.FromResult(new SourceRecord(id, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ID"] = id, ["INVOICE"] = "RE-LIVE-71" },
                new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>()));
        public Task<DocumentFile?> FileAsync(SourceOptions source, string id)
            => Task.FromResult<DocumentFile?>(DocumentFile.FromBytes("invoice.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 live")));
        public Task<string> MarkDoneAsync(SourceOptions source, string id) { Marked.Add(id); return Task.FromResult("Marked."); }
        public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, SourceOptions source) => Task.FromResult<IReadOnlyList<SourceChoice>>(Array.Empty<SourceChoice>());
        public string RecordKey(SourceOptions source, string id) => id;
        public string Scope(SourceOptions source) => "live";
    }

    private ImportProcessor Processor()
    {
        var registry = ConnectorRegistry.BuiltIn();
        registry.RegisterSource("live", _ => _source);
        return new ImportProcessor(NullLogger.Instance, _ledger, registry);
    }

    private static BDCOptions Options(TargetOptions target)
    {
        var options = new BDCOptions
        {
            Connections =
            {
                new ConnectionOptions { Id = "bus", Kind = "servicebus", Settings = { ["Namespace"] = Env("HIVEBDC_SB_NAMESPACE") } },
                new ConnectionOptions { Id = "storage", Kind = "azure-storage", Settings = { ["AccountUrl"] = Env("HIVEBDC_SB_PDF_ACCOUNT") } },
            },
        };
        options.Flows.Add(new FlowOptions
        {
            Id = "live-inbound", Name = "Vendor invoice (DEMF)", DocumentType = "Vendor invoice", DataAreaId = "DEMF",
            Source = new SourceOptions { Kind = "live" },
            Produce = new ProduceOptions { Columns = { new ColumnOptions { Name = "DOCID", Value = "{ID}" }, new ColumnOptions { Name = "InvoiceId", Value = "{INVOICE}" }, new ColumnOptions { Name = "PdfLink", Value = "{FilePath}" } } },
            Targets = { target },
        });
        options.Normalize();
        return options;
    }

    private static BlobContainerClient Container()
        => new BlobServiceClient(new Uri(Env("HIVEBDC_SB_PDF_ACCOUNT")), new DefaultAzureCredential()).GetBlobContainerClient(Env("HIVEBDC_SB_PDF_CONTAINER"));

    [ServiceBusLiveFact]
    public async Task ARealContainerGetsTheCsvWithoutBomUnderTheErpFolderAndTheFileBesideIt()
    {
        string folder = $"hivebdc-inbound-{_run}/";
        var target = new TargetOptions
        {
            Kind = "blob", Connection = "storage",
            Settings = { ["Container"] = Env("HIVEBDC_SB_PDF_CONTAINER"), ["Prefix"] = folder + "pending/", ["SaveFile"] = "true", ["FilePrefix"] = folder + "files/" },
        };
        var container = Container();
        try
        {
            Assert.Equal(1, Processor().RunOnce(Options(target), License).Processed);

            byte[] csv = (await container.GetBlobClient(folder + "pending/71.csv").DownloadContentAsync()).Value.Content.ToArray();
            Assert.NotEqual(0xEF, csv[0]);
            string fileUrl = $"{container.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{folder}files/DMS71.pdf";
            Assert.Equal($"DOCID;InvoiceId;PdfLink\r\n71;RE-LIVE-71;{fileUrl}\r\n", Encoding.UTF8.GetString(csv));
            var properties = await container.GetBlobClient(folder + "pending/71.csv").GetPropertiesAsync();
            Assert.Equal("text/csv; charset=utf-8", properties.Value.ContentType);
            Assert.Equal("%PDF-1.7 live", (await container.GetBlobClient(folder + "files/DMS71.pdf").DownloadContentAsync()).Value.Content.ToString());
            Assert.Equal(new[] { "71" }, _source.Marked);
        }
        finally
        {
            await foreach (var blob in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, folder, CancellationToken.None)) await container.DeleteBlobIfExistsAsync(blob.Name);
        }
    }

    [ServiceBusLiveFact]
    public async Task ARealQueueGetsTheCsvAsTheMessageBodyWithAStableId()
    {
        string queue = Env("HIVEBDC_SB_QUEUE");
        string folder = $"hivebdc-inbound-{_run}/";
        var target = new TargetOptions
        {
            Kind = "servicebus", Connection = "bus",
            Settings = { ["Queue"] = queue, ["SaveFile"] = "true", ["FileConnection"] = "storage", ["FileContainer"] = Env("HIVEBDC_SB_PDF_CONTAINER"), ["FilePrefix"] = folder },
        };
        var container = Container();
        await using var client = new ServiceBusClient(Env("HIVEBDC_SB_NAMESPACE"), new DefaultAzureCredential());
        try
        {
            Assert.Equal(1, Processor().RunOnce(Options(target), License).Processed);

            await using var reader = client.CreateReceiver(queue);
            var message = await reader.ReceiveMessageAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(message);
            await reader.CompleteMessageAsync(message!);
            Assert.Equal("live-inbound_71", message!.MessageId);
            Assert.Equal("text/csv", message.ContentType);
            Assert.Equal("Vendor invoice", message.Subject);
            Assert.Equal("DEMF", message.ApplicationProperties["dataAreaId"]);
            byte[] body = message.Body.ToArray();
            Assert.NotEqual(0xEF, body[0]);
            Assert.StartsWith("DOCID;InvoiceId;PdfLink\r\n71;RE-LIVE-71;https://", Encoding.UTF8.GetString(body));
            Assert.True((await container.GetBlobClient(folder + "DMS71.pdf").ExistsAsync()).Value);
        }
        finally
        {
            await foreach (var blob in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, folder, CancellationToken.None)) await container.DeleteBlobIfExistsAsync(blob.Name);
        }
    }
}