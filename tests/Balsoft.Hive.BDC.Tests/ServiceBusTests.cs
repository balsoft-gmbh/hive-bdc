using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Service Bus as the source of F&amp;O documents and as a target back to F&amp;O (failure cases 13, 14, 16, 36).</summary>
public sealed class ServiceBusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-sb-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private readonly MemoryBus _bus = new();
    private readonly MemoryBlobStore _store = new();
    private static readonly LicenseResult License = new() { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

    public ServiceBusTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class MemoryBus : IBusInbox, IBusOutbox
    {
        public sealed class Entry(BusMessage message)
        {
            public BusMessage Message { get; } = message;
            public string State { get; set; } = "active";
            public bool Locked { get; set; }
            public int Deliveries { get; set; }
            public string? Reason { get; set; }
            public string? Description { get; set; }
        }

        private long _sequence = 100;

        public List<Entry> Entries { get; } = new();

        public List<(byte[] Body, string MessageId, string Subject, IReadOnlyDictionary<string, object> Properties)> Sent { get; } = new();

        public Exception? SendFailure { get; set; }

        public string Where => "sb-test.servicebus.windows.net/bdc-out";

        public Entry Put(string messageId, string json)
        {
            var entry = new Entry(new BusMessage(++_sequence, messageId, "SalesConfirmation", Encoding.UTF8.GetBytes(json)));
            Entries.Add(entry);
            return entry;
        }

        public IReadOnlyList<BusMessage> Receive(int max, TimeSpan wait)
        {
            var batch = Entries.Where(e => e.State == "active" && !e.Locked).Take(max).ToList();
            foreach (var e in batch) { e.Locked = true; e.Deliveries++; }
            return batch.Select(e => e.Message).ToList();
        }

        private Entry Of(BusMessage m) => Entries.Single(e => e.Message.SequenceNumber == m.SequenceNumber);

        public void Defer(BusMessage message) { var e = Of(message); e.State = "deferred"; e.Locked = false; }

        public void Abandon(BusMessage message) => Of(message).Locked = false;

        public void Complete(BusMessage message) { var e = Of(message); e.State = "completed"; e.Locked = false; }

        public void DeadLetter(BusMessage message, string reason, string description)
        {
            var e = Of(message);
            (e.State, e.Reason, e.Description, e.Locked) = ("deadlettered", reason, description, false);
        }

        public BusMessage? ReceiveDeferred(long sequenceNumber)
            => Entries.FirstOrDefault(e => e.Message.SequenceNumber == sequenceNumber && e.State == "deferred")?.Message;

        public void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties)
        {
            if (SendFailure is { } failure) throw failure;
            Sent.Add((body, messageId, subject, properties));
        }
    }

    private sealed class MemoryBlobStore : IBlobStore
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public Uri ContainerUri { get; } = new("https://account.blob.core.windows.net/fo-out");
        public IReadOnlyList<string> List(string prefix) => Blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        public bool Exists(string name) => Blobs.ContainsKey(name);
        public byte[] Download(string name) => Blobs[name];
        public void Upload(string name, byte[] content, string contentType) => Blobs[name] = content;
        public void Move(string name, string newName) { Blobs[newName] = Blobs[name]; Blobs.Remove(name); }
    }

    private DocumentRoute Route(bool withPdfSource = true)
    {
        string input = Path.Combine(_root, "staging");
        var source = new SourceOptions { Kind = "servicebus", Connection = "fo-bus", Settings = { ["Queue"] = "bdc-out" } };
        if (withPdfSource) { source.Settings["PdfConnection"] = "fo-storage"; source.Settings["PdfContainer"] = "fo-out"; }
        return new DocumentRoute { DataAreaId = "USMF", Input = input, Output = Path.Combine(input, "out"), Source = source };
    }

    private static BDCOptions Options(DocumentRoute route, bool typeEnabled = true)
    {
        var options = new BDCOptions();
        options.Connections.Add(new ConnectionOptions { Id = "fo-bus", Kind = "servicebus", Secret = "placeholder-for-the-fake-bus" });
        options.Connections.Add(new ConnectionOptions { Id = "fo-storage", Kind = "azure-storage", Secret = "UseDevelopmentStorage=true" });
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "SalesConfirmation", Handler = DocumentHandlerKind.Pdf, Mail = false, Enabled = typeEnabled, Companies = { route },
        });
        options.Normalize();
        return options;
    }

    private MemoryBus.Entry FoMessage(string outboundId = "5637144576", string? pdfLink = null, bool pdfThere = true)
    {
        string link = pdfLink ?? $"https://account.blob.core.windows.net/fo-out/SalesConfirmation/{outboundId}.pdf";
        if (pdfThere) _store.Upload($"SalesConfirmation/{outboundId}.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 confirmation"), "application/pdf");
        return _bus.Put(outboundId, JsonSerializer.Serialize(new { documentType = "SalesConfirmation", dataAreaId = "USMF", documentNumber = "SO-1001", pdfLink = link }));
    }

    private ServiceBusStager Stager() => new(NullLogger.Instance, _ledger, (_, _, _, _) => _bus, (_, _) => _store);

    private DocumentProcessor Processor(ConnectorRegistry? connectors = null) => new(NullLogger.Instance,
        new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger, connectors);

    [Fact]
    public void AMessageIsStagedWithItsPdfDeferredProcessedAndCompleted()
    {
        var route = Route();
        var options = Options(route);
        var message = FoMessage();

        Stager().Run(options);
        Assert.Equal("deferred", message.State);
        Assert.True(File.Exists(Path.Combine(route.Input, "5637144576_sb.json")));
        Assert.Equal("%PDF-1.4 confirmation", File.ReadAllText(Path.Combine(route.Input, "5637144576_sb.pdf")));

        Assert.Equal(1, Processor().ScanOnce(options, License, null, null).Processed);
        Stager().Run(options);

        Assert.Equal("completed", message.State);
        Assert.True(File.Exists(Path.Combine(route.Output, "SO-1001.pdf")));
        Assert.Empty(Directory.GetFiles(Path.Combine(route.Input, ".hive-sources")));
    }

    [Fact]
    public void AFailedDocumentIsDeadLetteredWithItsReason()
    {
        var route = Route();
        var staging = Options(route);
        var processing = Options(route, typeEnabled: false);
        processing.DocumentTypes.Add(new DocumentTypeOptions { Id = "Other", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { new DocumentRoute { DataAreaId = "USMF", Input = route.Input } } });
        processing.Normalize();
        var message = FoMessage();

        Stager().Run(staging);
        Assert.Equal(1, Processor().ScanOnce(processing, License, null, null).Errors);
        Stager().Run(staging);

        Assert.Equal(("deadlettered", "HiveBDC.DocumentError"), (message.State, message.Reason));
        string expected = _ledger.Events("5637144576_sb.json", 50).Last(e => e.State == "failed").Message;
        Assert.Equal(expected, message.Description);
    }

    [Fact]
    public void AMessageSentTwiceIsCompletedWithoutASecondDocument()
    {
        var route = Route();
        var options = Options(route);
        var first = FoMessage();
        Stager().Run(options);
        var again = FoMessage();

        Stager().Run(options);
        Assert.Equal(("deferred", "completed"), (first.State, again.State));
        Assert.Single(Directory.GetFiles(route.Input, "*_sb.json"));

        Processor().ScanOnce(options, License, null, null);
        var late = FoMessage();
        Stager().Run(options);
        Assert.Equal(("completed", "completed"), (first.State, late.State));
    }

    [Fact]
    public void APdfThatIsNotThereYetLeavesTheMessageForLaterAndItIsStagedOnceItArrives()
    {
        var route = Route();
        var options = Options(route);
        var message = FoMessage(pdfThere: false);

        Stager().Run(options);
        Assert.Equal(("active", false), (message.State, message.Locked));
        Assert.False(Directory.Exists(route.Input) && Directory.GetFiles(route.Input, "*_sb.json").Length > 0);

        _store.Upload("SalesConfirmation/5637144576.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 late"), "application/pdf");
        Stager().Run(options);
        Assert.Equal("deferred", message.State);
        Assert.Equal("%PDF-1.4 late", File.ReadAllText(Path.Combine(route.Input, "5637144576_sb.pdf")));
    }

    [Fact]
    public void APdfLinkOutsideTheContainerIsRefusedAndDeadLettered()
    {
        var route = Route();
        var message = FoMessage(pdfLink: "https://elsewhere.example/steal.pdf");

        Stager().Run(Options(route));

        Assert.Equal(("deadlettered", "HiveBDC.Refused"), (message.State, message.Reason));
        Assert.Contains("outside the container", message.Description);
        Assert.False(Directory.Exists(route.Input) && Directory.GetFiles(route.Input, "*_sb.json").Length > 0);
        Assert.Contains("outside the container", _ledger.Events("5637144576", 10).Single().Message);
    }

    [Fact]
    public void APdfLinkWithoutAPdfSourceIsRefused()
    {
        var route = Route(withPdfSource: false);
        var message = FoMessage();

        Stager().Run(Options(route));

        Assert.Equal("deadlettered", message.State);
        Assert.Contains("no PdfConnection and PdfContainer", message.Description);
    }

    [Fact]
    public void AWaitingDocumentKeepsItsMessageDeferredThroughManyScans()
    {
        var route = Route();
        var options = Options(route);
        var message = FoMessage();
        var noFeature = new LicenseResult { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "einvoice" } } };

        for (int scan = 0; scan < 12; scan++)
        {
            Stager().Run(options);
            Assert.Equal(1, Processor().ScanOnce(options, noFeature, null, null).Held);
        }

        Assert.Equal(("deferred", 1), (message.State, message.Deliveries));
    }

    [Fact]
    public void ADeferredMessageThatIsGoneIsNotAnErrorAndItsNoteIsCleared()
    {
        var route = Route();
        var options = Options(route);
        var message = FoMessage();
        Stager().Run(options);
        message.State = "expired";

        Processor().ScanOnce(options, License, null, null);
        Stager().Run(options);

        Assert.Empty(Directory.GetFiles(Path.Combine(route.Input, ".hive-sources")));
        Assert.True(File.Exists(Path.Combine(route.Input, "processed", "5637144576_sb.json")));
    }

    [Fact]
    public void AnOutboundDocumentGoesToTheQueueAsJsonWithAStableMessageId()
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        var route = new DocumentRoute
        {
            DataAreaId = "USMF", Input = input, Output = Path.Combine(input, "out"),
            Targets = { new TargetOptions { Kind = "servicebus", Connection = "fo-bus", Settings = { ["Queue"] = "bdc-in" }, Fields = { ["number"] = "{documentNumber}" } } },
        };
        var options = Options(route);
        File.WriteAllText(Path.Combine(input, "conf_1.json"), """{"documentType":"SalesConfirmation","dataAreaId":"USMF","documentNumber":"SO-1001"}""");
        File.WriteAllText(Path.Combine(input, "conf_1.pdf"), "%PDF-1.4 out");
        var connectors = ConnectorRegistry.BuiltIn();
        connectors.RegisterTarget("servicebus", runtime => new ServiceBusDocumentTarget(runtime.Options, (_, _) => _bus));

        Assert.Equal(1, Processor(connectors).ScanOnce(options, License, null, null).Processed);

        var sent = Assert.Single(_bus.Sent);
        var body = (JsonObject)JsonNode.Parse(sent.Body)!;
        Assert.Equal("SalesConfirmation_USMF_conf", sent.MessageId);
        Assert.Equal("SalesConfirmation", sent.Subject);
        Assert.Equal("SO-1001", sent.Properties["documentNumber"]);
        Assert.Equal("%PDF-1.4 out", Encoding.UTF8.GetString(Convert.FromBase64String((string)body["content"]!)));
        Assert.Equal("SO-1001", (string)body["fields"]!["number"]!);
        Assert.Contains("as message SalesConfirmation_USMF_conf", _ledger.Events("SO-1001", 50).Single(e => e.Step == "delivered").Message);
    }

    [Theory]
    [InlineData(true, 0, 1, 0)]
    [InlineData(false, 0, 0, 1)]
    public void ATransientServiceBusFailureWaitsAndAnyOtherIsAnError(bool transient, int processed, int held, int errors)
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        var route = new DocumentRoute
        {
            DataAreaId = "USMF", Input = input, Output = Path.Combine(input, "out"),
            Targets = { new TargetOptions { Kind = "servicebus", Connection = "fo-bus", Settings = { ["Topic"] = "bdc-in" } } },
        };
        File.WriteAllText(Path.Combine(input, "conf_1.json"), """{"documentType":"SalesConfirmation","dataAreaId":"USMF","documentNumber":"SO-1001"}""");
        File.WriteAllText(Path.Combine(input, "conf_1.pdf"), "%PDF-1.4 out");
        _bus.SendFailure = new ServiceBusException(transient, "namespace busy");
        var connectors = ConnectorRegistry.BuiltIn();
        connectors.RegisterTarget("servicebus", runtime => new ServiceBusDocumentTarget(runtime.Options, (_, _) => _bus));

        var result = Processor(connectors).ScanOnce(Options(route), License, null, null);

        Assert.Equal((processed, held, errors), (result.Processed, result.Held, result.Errors));
    }

    [Theory]
    [InlineData("SalesInvoice", "USMF", "RE/2026#1", "SalesInvoice_USMF_RE-2026-1")]
    [InlineData("T", "A", "ü", "T_A_-")]
    public void TheMessageIdKeepsWhatServiceBusAccepts(string type, string area, string id, string expected)
        => Assert.Equal(expected, ServiceBusDocumentTarget.MessageIdFor(type, area, id));

    [Fact]
    public void TheMessageIdIsAtMost128Characters()
        => Assert.Equal(128, ServiceBusDocumentTarget.MessageIdFor("T", "A", new string('x', 300)).Length);

    [Fact]
    public void AzureAcceptsAServiceBusSourceAndNamesAConnectionWithoutKeyOrNamespace()
    {
        var route = Route();
        route.Targets.Add(new TargetOptions { Kind = "blob", Connection = "fo-storage", Settings = { ["Container"] = "back" } });
        route.Input = "/tmp/hivebdc/sb";
        Assert.Empty(AzureSettingsCheck.Problems(new BDCOptions { DocumentTypes = { new DocumentTypeOptions { Id = "SalesConfirmation", Mail = false, Companies = { route } } } }));

        var bare = new ConnectionOptions { Id = "fo-bus", Kind = "servicebus" };
        Assert.Contains("give the Service Bus connection string", Assert.Single(bare.Problems()));
        bare.Settings["Namespace"] = "sb-test.servicebus.windows.net";
        Assert.Empty(bare.Problems());
    }
}