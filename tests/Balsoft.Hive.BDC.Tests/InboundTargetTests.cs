using System.Text;
using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Inbound flows delivering to Blob Storage and Service Bus (design 2026-11-20, failure cases 1 to 9 and 14).</summary>
[Collection("global run mode")]
public sealed class InboundTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-inbound-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private readonly FakeSource _source = new();
    private readonly MemoryBlobs _blobs = new("https://erpstorage.blob.core.windows.net/erp-inbound");
    private readonly MemoryBlobs _files = new("https://filestore.blob.core.windows.net/erp-files");
    private readonly MemoryOutbox _bus = new();

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public InboundTargetTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
        SettingsPaths.Azure = false;
    }

    public void Dispose()
    {
        SettingsPaths.Azure = false;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A document system with records 17 and 18 ready; 17 is a vendor invoice with a PDF.</summary>
    private sealed class FakeSource : IRecordSource
    {
        public readonly HashSet<string> Ready = new() { "17" };
        public readonly List<string> Marked = new();
        public int Reads;
        public bool FailMark;

        public SourceDescriptor Descriptor { get; } = new("fake", "Test DMS", "", "", "documents", "document", "DMS", "{ID}.csv",
            Array.Empty<SourceSetting>());

        public IReadOnlyList<string> Problems(SourceOptions source) => Array.Empty<string>();

        public Task<IReadOnlyList<string>> ReadyAsync(SourceOptions source, int max)
            => Task.FromResult<IReadOnlyList<string>>(Ready.OrderBy(x => x).Take(max).ToList());

        public Task<SourceRecord> ReadAsync(SourceOptions source, string id)
        {
            Reads++;
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = id, ["INVOICE"] = "RE-2026-1187", ["AMOUNT"] = 1234.5m, ["DATE"] = new DateTime(2026, 11, 18),
            };
            return Task.FromResult(new SourceRecord(id, values,
                new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)));
        }

        public Task<DocumentFile?> FileAsync(SourceOptions source, string id)
            => Task.FromResult<DocumentFile?>(DocumentFile.FromBytes($"invoice {id}.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 " + id)));

        public Task<string> MarkDoneAsync(SourceOptions source, string id)
        {
            if (FailMark) throw new ConnectorException("the DMS is busy.", retryLater: true);
            Marked.Add(id);
            Ready.Remove(id);
            return Task.FromResult($"Marked {id} done.");
        }

        public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, SourceOptions source)
            => Task.FromResult<IReadOnlyList<SourceChoice>>(Array.Empty<SourceChoice>());

        public string RecordKey(SourceOptions source, string id) => id;

        public string Scope(SourceOptions source) => "fake";
    }

    private sealed class MemoryBlobs(string container) : IBlobStore
    {
        public readonly Dictionary<string, (byte[] Content, string Type)> Blobs = new(StringComparer.Ordinal);
        public int Uploads;
        public int FailNext;
        public int FailStatus = 503;

        public Uri ContainerUri { get; } = new(container);

        public IReadOnlyList<string> List(string prefix) => Blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        public bool Exists(string name) => Blobs.ContainsKey(name);

        public byte[] Download(string name) => Blobs[name].Content;

        public void Upload(string name, byte[] content, string contentType)
        {
            if (FailNext > 0) { FailNext--; throw new RequestFailedException(FailStatus, "The server is busy."); }
            Uploads++;
            Blobs[name] = (content, contentType);
        }

        public void Move(string name, string newName) => throw new NotSupportedException();
    }

    private sealed class MemoryOutbox : IBusOutbox
    {
        public readonly List<(byte[] Body, string Id, string Subject, IReadOnlyDictionary<string, object> Props, string Type)> Sent = new();
        public Exception? Fail;

        public string Where => "queue bdc-inbound";

        public void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties)
            => Send(body, messageId, subject, properties, "application/json");

        public void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties, string contentType)
        {
            if (Fail is { } fail) throw fail;
            Sent.Add((body, messageId, subject, properties, contentType));
        }
    }

    private ConnectorRegistry Registry()
    {
        var registry = new ConnectorRegistry();
        registry.RegisterSource("fake", _ => _source);
        registry.RegisterInbound("folder", _ => new FolderInboundTarget());
        registry.RegisterInbound("blob", o => new BlobInboundTarget(o, (_, container) => container == "erp-files" ? _files : _blobs));
        registry.RegisterInbound(ConnectionKinds.ServiceBus, o => new ServiceBusInboundTarget(o, (_, _) => _bus, (_, _) => _files));
        return registry;
    }

    private ImportProcessor Processor() => new(NullLogger.Instance, _ledger, Registry());

    private string Folder => Path.Combine(_root, "erp-in");

    private BDCOptions Options(TargetOptions target)
    {
        var options = new BDCOptions();
        options.Connections.Add(new ConnectionOptions { Id = "erp-storage", Kind = ConnectionKinds.AzureStorage, Settings = { ["AccountUrl"] = "https://erpstorage.blob.core.windows.net" } });
        options.Connections.Add(new ConnectionOptions { Id = "file-storage", Kind = ConnectionKinds.AzureStorage, Settings = { ["AccountUrl"] = "https://filestore.blob.core.windows.net" } });
        options.Connections.Add(new ConnectionOptions { Id = "erp-bus", Kind = ConnectionKinds.ServiceBus, Settings = { ["Namespace"] = "erp.servicebus.windows.net" } });
        options.Flows.Add(new FlowOptions
        {
            Id = "vendor-invoice-demf",
            Name = "VendorInvoice (DEMF)",
            DocumentType = "VendorInvoice",
            DataAreaId = "DEMF",
            Source = new SourceOptions { Kind = "fake" },
            Produce = new ProduceOptions
            {
                Columns =
                {
                    new ColumnOptions { Name = "DOCID", Value = "{ID}" },
                    new ColumnOptions { Name = "InvoiceId", Value = "{INVOICE}" },
                    new ColumnOptions { Name = "Amount", Value = "{AMOUNT}" },
                    new ColumnOptions { Name = "InvoiceDate", Value = "{DATE}" },
                    new ColumnOptions { Name = "Company", Value = "{DataAreaId}" },
                    new ColumnOptions { Name = "PdfLink", Value = "{FilePath}" },
                },
            },
            Targets = { target },
        });
        options.Normalize();
        return options;
    }

    private static TargetOptions Blob(string prefix = "pending/", string? filePrefix = null, bool saveFile = true)
    {
        var t = new TargetOptions { Kind = "blob", Connection = "erp-storage", Settings = { ["Container"] = "erp-inbound", ["Prefix"] = prefix, ["SaveFile"] = saveFile ? "true" : "false" } };
        if (filePrefix is not null) t.Settings["FilePrefix"] = filePrefix;
        return t;
    }

    private static TargetOptions Bus(bool saveFile = true) => new()
    {
        Kind = ConnectionKinds.ServiceBus, Connection = "erp-bus",
        Settings = { ["Queue"] = "bdc-inbound", ["SaveFile"] = saveFile ? "true" : "false", ["FileConnection"] = "file-storage", ["FileContainer"] = "erp-files", ["FilePrefix"] = "vendor" },
    };

    private const string Header = "DOCID;InvoiceId;Amount;InvoiceDate;Company;PdfLink\r\n";

    [Fact]
    public void BlobGetsTheCsvWithoutBomUnderTheErpFolderAndTheFileOutsideIt()
    {
        var result = Processor().RunOnce(Options(Blob()), License);

        Assert.Equal(1, result.Processed);
        var csv = _blobs.Blobs["pending/17.csv"];
        Assert.NotEqual(0xEF, csv.Content[0]);
        Assert.Equal("text/csv; charset=utf-8", csv.Type);
        Assert.Equal(Header + "17;RE-2026-1187;1234,5;18.11.2026;DEMF;https://erpstorage.blob.core.windows.net/erp-inbound/files/DMS17.pdf\r\n",
            Encoding.UTF8.GetString(csv.Content));
        Assert.Equal("%PDF-1.7 17", Encoding.ASCII.GetString(_blobs.Blobs["files/DMS17.pdf"].Content));
        Assert.Equal("application/pdf", _blobs.Blobs["files/DMS17.pdf"].Type);
        Assert.Equal(new[] { "17" }, _source.Marked);
        var history = _ledger.Events("17", 50);
        Assert.Equal(new[] { "received", "delivered", "done" }, history.Select(e => e.Step));
        Assert.Equal("Wrote 17.csv with 1 row(s) to https://erpstorage.blob.core.windows.net/erp-inbound/pending/ and the file as erp-inbound/files/DMS17.pdf.",
            history[1].Message);
    }

    [Theory]
    [InlineData("pending/", "pending/files/")]
    [InlineData("", "files/")]
    [InlineData("pending", "pending/")]
    public void AFileFolderInsideTheErpFolderIsRefusedAndNothingIsTouched(string prefix, string filePrefix)
    {
        var options = Options(Blob(prefix, filePrefix));
        var registry = Registry();

        var problems = InboundChecks.Problems(options.Flows, registry, new SourceRuntime(options, NullLogger.Instance));
        Assert.Contains(problems, p => p.StartsWith("Inbound flow \"VendorInvoice (DEMF)\" (DEMF): ", StringComparison.Ordinal) && p.Contains("CSV"));
        Assert.Equal(0, Processor().RunOnce(options, License).Processed);
        Assert.Empty(_blobs.Blobs);
        Assert.Equal(0, _source.Reads);
    }

    [Fact]
    public void AFileFolderOutsideTheErpFolderOrNoFileIsAccepted()
    {
        var registry = Registry();
        foreach (var target in new[] { Blob("pending/", "archive/"), Blob("in/pending/", "in/files/"), Blob("", null, saveFile: false) })
        {
            var options = Options(target);
            Assert.Empty(InboundChecks.Problems(options.Flows, registry, new SourceRuntime(options, NullLogger.Instance)));
        }
    }

    [Fact]
    public void ServiceBusGetsTheCsvAsTheBodyAndTheFileGoesToBlobStorage()
    {
        Assert.Equal(1, Processor().RunOnce(Options(Bus()), License).Processed);

        var message = Assert.Single(_bus.Sent);
        Assert.Equal("vendor-invoice-demf_17", message.Id);
        Assert.Equal("VendorInvoice", message.Subject);
        Assert.Equal("text/csv", message.Type);
        Assert.NotEqual(0xEF, message.Body[0]);
        Assert.Equal(Header + "17;RE-2026-1187;1234,5;18.11.2026;DEMF;https://filestore.blob.core.windows.net/erp-files/vendor/DMS17.pdf\r\n",
            Encoding.UTF8.GetString(message.Body));
        Assert.Equal("DEMF", message.Props["dataAreaId"]);
        Assert.Equal("17", message.Props["recordId"]);
        Assert.Equal("17.csv", message.Props["fileName"]);
        Assert.True(_files.Blobs.ContainsKey("vendor/DMS17.pdf"));
        Assert.Equal("Wrote 17.csv with 1 row(s) to Service Bus queue bdc-inbound as message vendor-invoice-demf_17 and the file as erp-files/vendor/DMS17.pdf.",
            _ledger.Events("17", 50).Single(e => e.Step == "delivered").Message);
    }

    [Fact]
    public void AMessageOverTheSizeLimitFailsInWordsAndMarksNothing()
    {
        _bus.Fail = new ServiceBusException("The message is too large.", ServiceBusFailureReason.MessageSizeExceeded);
        var processor = Processor();

        Assert.Equal(1, processor.RunOnce(Options(Bus(saveFile: false)), License).Errors);

        Assert.Empty(_source.Marked);
        var failed = _ledger.Events("17", 50).Single(e => e.Step == "failed").Message;
        Assert.Contains("The CSV has 1 KB, more than Service Bus queue bdc-inbound takes in one message", failed);
        Assert.Equal(0, processor.RunOnce(Options(Bus(saveFile: false)), License).Errors);
    }

    [Fact]
    public void ABusyStorageWaitsAndTheNextScanDeliversOnce()
    {
        _blobs.FailNext = 1;
        var processor = Processor();
        var options = Options(Blob());

        Assert.Equal(1, processor.RunOnce(options, License).Held);
        Assert.Empty(_source.Marked);
        Assert.StartsWith("Blob Storage: The server is busy.", _ledger.Events("17", 50).Single(e => e.Step == "waiting").Message);

        Assert.Equal(1, processor.RunOnce(options, License).Processed);
        Assert.Equal(new[] { "17" }, _source.Marked);
        Assert.Single(_blobs.Blobs, b => b.Key.EndsWith(".csv", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedUploadFailsInWords()
    {
        _blobs.FailNext = 1;
        _blobs.FailStatus = 403;

        Assert.Equal(1, Processor().RunOnce(Options(Blob()), License).Errors);
        Assert.Empty(_source.Marked);
        Assert.Contains("Nothing was written for it.", _ledger.Events("17", 50).Single(e => e.Step == "failed").Message);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("servicebus")]
    public void WhenTheDoneMarkFailsTheNextRunOnlyRepeatsTheMark(string kind)
    {
        _source.FailMark = true;
        var processor = Processor();
        var options = Options(kind == "blob" ? Blob() : Bus());

        Assert.Equal(1, processor.RunOnce(options, License).Held);
        _source.FailMark = false;
        Assert.Equal(1, processor.RunOnce(options, License).Processed);

        Assert.Equal(1, _source.Reads);
        Assert.Equal(new[] { "17" }, _source.Marked);
        Assert.Equal(kind == "blob" ? 2 : 1, _blobs.Uploads + _files.Uploads);
        Assert.Equal(kind == "blob" ? 0 : 1, _bus.Sent.Count);
        Assert.Contains("Test DMS: the DMS is busy.", _ledger.Events("17", 50).Single(e => e.Step == "waiting").Message);
    }

    [Fact]
    public void ACsvWrittenToTheFolderBeforeTheFlowMovedToBlobStorageIsNotDeliveredAgain()
    {
        _ledger.RecordDelivery("vendor-invoice-demf|17", "folder", "folder", @"C:\ERP\in\17.csv", "17");

        Assert.Equal(1, Processor().RunOnce(Options(Blob()), License).Processed);

        Assert.Empty(_blobs.Blobs);
        Assert.Equal(0, _source.Reads);
        Assert.Equal(new[] { "17" }, _source.Marked);
    }

    [Fact]
    public void AColumnThatCannotBeFilledLeavesNothingBehind()
    {
        var options = Options(Blob());
        options.Flows[0].Produce.Columns.Add(new ColumnOptions { Name = "Project", Value = "{PROJECT}" });

        Assert.Equal(1, Processor().RunOnce(options, License).Errors);

        Assert.Empty(_blobs.Blobs);
        Assert.Empty(_source.Marked);
    }

    [Fact]
    public void TheFolderKeepsItsBomAndItsWords()
    {
        var target = new TargetOptions { Kind = "folder", Settings = { ["Folder"] = Folder, ["SaveFile"] = "true" } };

        Assert.Equal(1, Processor().RunOnce(Options(target), License).Processed);

        byte[] bytes = File.ReadAllBytes(Path.Combine(Folder, "17.csv"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.EndsWith($";DEMF;{Path.Combine(Folder, "DMS17.pdf")}\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal($"Wrote 17.csv with 1 row(s) to {Folder} and the file as DMS17.pdf.",
            _ledger.Events("17", 50).Single(e => e.Step == "delivered").Message);
    }

    [Fact]
    public void AFlowWithoutADestinationOrWithAnUnknownOneTouchesNothing()
    {
        var none = Options(Blob());
        none.Flows[0].Targets.Clear();
        var unknown = Options(new TargetOptions { Kind = "ftp", Settings = { ["Host"] = "erp" } });

        Assert.Equal(0, Processor().RunOnce(none, License).Processed);
        Assert.Equal(0, Processor().RunOnce(unknown, License).Processed);
        Assert.Equal(0, _source.Reads);
    }

    [Fact]
    public void AnAzureInstanceRunsBlobAndServiceBusFlowsButHoldsAFolderFlow()
    {
        Assert.Empty(AzureSettingsCheck.Problems(Options(Blob())));
        Assert.Empty(AzureSettingsCheck.Problems(Options(Bus())));
        var folder = AzureSettingsCheck.Problems(Options(new TargetOptions { Kind = "folder", Settings = { ["Folder"] = "/erp/in" } }));
        Assert.Equal("Inbound flow 'VendorInvoice (DEMF)' writes to the folder '/erp/in', which an Azure instance does not have; choose Blob Storage or Service Bus under Inbound.",
            Assert.Single(folder));
    }

    [Theory]
    [InlineData(false, "folder,blob,servicebus")]
    [InlineData(true, "blob,servicebus")]
    public void TheDashboardsAreOfferedTheDestinationsOfTheirRunMode(bool azure, string kinds)
    {
        var answer = JsonSerializer.SerializeToElement(LocalStatusServer.InboundDestinations(ConnectorRegistry.BuiltIn(), Options(Blob()), azure));

        Assert.Equal(kinds, string.Join(",", answer.EnumerateArray().Select(d => d.GetProperty("kind").GetString())));
        var blob = answer.EnumerateArray().Single(d => d.GetProperty("kind").GetString() == "blob");
        Assert.True(blob.GetProperty("connected").GetBoolean());
        Assert.Equal("azure-storage", blob.GetProperty("connectionKind").GetString());
        Assert.Contains(blob.GetProperty("settings").EnumerateArray(), s => s.GetProperty("key").GetString() == "FilePrefix" && s.GetProperty("dependsOn").GetString() == "SaveFile");
    }

    [Fact]
    public void ThePreviewIsTheCsvARunWouldDeliver()
    {
        var options = Options(Blob());
        var flow = options.Flows[0];
        var record = _source.ReadAsync(flow.Source, "17").Result;
        var registryPreview = InboundPreview.Build(_source.Descriptor, record, flow.Produce, "DEMF", flow.Targets[0], options);

        Assert.Empty(registryPreview.Problems);
        Assert.Equal("17.csv", registryPreview.FileName);
        Assert.Equal(1, registryPreview.Rows);
        Assert.Equal(Header.TrimEnd(), registryPreview.Lines[0]);
        Assert.Equal("17;RE-2026-1187;1234,5;18.11.2026;DEMF;https://erpstorage.blob.core.windows.net/erp-inbound/files/DMS17.pdf", registryPreview.Lines[1]);
    }

    [Fact]
    public void ThePreviewOfADestinationWithoutItsConnectionLeavesTheFileAddressEmpty()
    {
        var options = Options(Blob());
        var flow = options.Flows[0];
        flow.Targets[0].Connection = "not-set-up";
        var record = _source.ReadAsync(flow.Source, "17").Result;

        var preview = InboundPreview.Build(_source.Descriptor, record, flow.Produce, "DEMF", flow.Targets[0], options);

        Assert.Equal("17;RE-2026-1187;1234,5;18.11.2026;DEMF;", preview.Lines[1]);
    }

    [Fact]
    public void MessageIdsStayInTheServiceBusAlphabet()
    {
        Assert.Equal("flow-1_DW-17-a.b", ServiceBusInboundTarget.MessageIdFor("flow 1", "DW/17 a.b"));
        Assert.Equal(128, ServiceBusInboundTarget.MessageIdFor(new string('f', 100), new string('r', 100)).Length);
    }
}