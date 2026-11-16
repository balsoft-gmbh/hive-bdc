using System.Text.Json;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class EventHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-history-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public EventHistoryTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeTarget(string kind) : IDocumentTarget
    {
        public bool Fail { get; set; }
        public string Kind => kind;
        public bool ReplacesOnRepeat => false;
        public string? Unavailable(TargetOptions target) => null;

        public DeliveryResult Deliver(DeliveryContext ctx)
        {
            if (Fail) throw new InvalidOperationException("the archive answered 503 for anna.berg@example.com");
            return new DeliveryResult("archive://store", "doc-1", $"Stored {ctx.Output.Name} in the archive.");
        }
    }

    private DocumentRoute Route(params TargetOptions[] targets)
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        return new DocumentRoute { DataAreaId = "A", Input = input, Output = Path.Combine(input, "out"), Targets = targets.ToList() };
    }

    private static BDCOptions Options(DocumentRoute route)
    {
        var options = new BDCOptions();
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "PurchaseOrder", DisplayName = "Purchase order", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { route },
        });
        options.Normalize();
        return options;
    }

    private static string Pair(DocumentRoute route, string id, bool pdf = true)
    {
        string path = Path.Combine(route.Input, id + "_1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { documentType = "PurchaseOrder", dataAreaId = "A", documentNumber = "PO-7" }));
        if (pdf) File.WriteAllText(Path.ChangeExtension(path, ".pdf"), "Synthetic pass-through fixture");
        return path;
    }

    private DocumentProcessor Processor(FakeTarget target)
    {
        var connectors = ConnectorRegistry.BuiltIn();
        connectors.RegisterTarget(target.Kind, _ => target);
        return new DocumentProcessor(NullLogger.Instance,
            new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger, connectors);
    }

    private static TargetOptions Archive() => new() { Kind = "archive", Settings = { ["Cabinet"] = "one" } };

    [Fact]
    public void ADeliveredDocumentHasEveryStepInOrderWithItsNumber()
    {
        var route = Route(Archive());
        Pair(route, "order");

        Assert.Equal(1, Processor(new FakeTarget("archive")).ScanOnce(Options(route), License, null, null).Processed);

        var history = _ledger.Events("PO-7", 100);
        Assert.Equal(new[] { "received", "read", "produced", "delivered", "done" }, history.Select(e => e.Step));
        Assert.All(history, e => Assert.Equal("order", e.DocumentId));
        Assert.All(history.Skip(1), e => Assert.Equal("PO-7", e.DocumentNumber));
        Assert.Equal("Read as Purchase order PO-7 for company A.", history[1].Message);
        Assert.Equal("archive", history[3].Target);
        Assert.Equal("Stored PO-7.pdf in the archive.", history[3].Message);
        Assert.All(history, e => Assert.Equal("ok", e.State));
    }

    [Fact]
    public void AWaitingDocumentIsWrittenOnceNotOnEveryScan()
    {
        var route = Route(Archive());
        string json = Pair(route, "order", pdf: false);
        var processor = Processor(new FakeTarget("archive"));
        var options = Options(route);

        for (int i = 0; i < 3; i++)
            Assert.Equal(1, processor.ScanOnce(options, License, null, null).Held);
        File.WriteAllText(Path.ChangeExtension(json, ".pdf"), "Synthetic pass-through fixture");
        Assert.Equal(1, processor.ScanOnce(options, License, null, null).Processed);

        var steps = _ledger.Events("order", 100).Select(e => e.Step).ToList();
        Assert.Equal(new[] { "received", "read", "waiting", "produced", "delivered", "done" }, steps);
        Assert.Equal("Waiting for the sibling PDF.", _ledger.Events("order", 100)[2].Message);
    }

    [Fact]
    public void AFailedTargetIsNamedAndTheDocumentSaysHowToRetry()
    {
        var route = Route(Archive());
        Pair(route, "order");

        Assert.Equal(1, Processor(new FakeTarget("archive") { Fail = true }).ScanOnce(Options(route), License, null, null).Errors);

        var history = _ledger.Events("PO-7", 100);
        var target = history.Single(e => e.Step == "delivered");
        Assert.Equal("failed", target.State);
        Assert.Equal("Archive: the archive answered 503 for a***@example.com", target.Message);
        var last = history[^1];
        Assert.Equal("failed", last.Step);
        Assert.Contains("put them back into the input folder to retry", last.Message);
        Assert.Equal("PO-7", last.DocumentNumber);
    }

    [Fact]
    public void ADocumentDroppedBackInStartsANewArrival()
    {
        var route = Route(Archive());
        var processor = Processor(new FakeTarget("archive"));
        var options = Options(route);

        Pair(route, "order");
        processor.ScanOnce(options, License, null, null);
        Pair(route, "order");
        processor.ScanOnce(options, License, null, null);

        Assert.Equal(2, _ledger.Events("order", 100).Count(e => e.Step == "received"));
        Assert.Equal(2, _ledger.Events("order", 100).Count(e => e.Step == "done"));
    }

    [Fact]
    public void ASearchReturnsOnlyThatDocumentAndABlankSearchTheNewestSteps()
    {
        _ledger.RecordEvent(new DocumentEvent("a", "a_1.json", "received", "ok", "first"));
        _ledger.RecordEvent(new DocumentEvent("b", "b_1.json", "received", "ok", "second", DocumentNumber: "RE-2"));
        _ledger.RecordEvent(new DocumentEvent("a", "a_1.json", "done", "ok", "third"));

        Assert.Equal(new[] { "first", "third" }, _ledger.Events("a", 10).Select(e => e.Message));
        Assert.Equal(new[] { "second" }, _ledger.Events("RE-2", 10).Select(e => e.Message));
        Assert.Equal(new[] { "second", "third" }, _ledger.Events(null, 2).Select(e => e.Message));
        Assert.Empty(_ledger.Events("nothing", 10));
    }

    [Theory]
    [InlineData("Mailbox anna.berg@example.com was not found", "Mailbox a***@example.com was not found")]
    [InlineData("line one\r\n  line two", "line one line two")]
    [InlineData("  ", "")]
    public void EventTextMasksMailAddressesAndFlattensLines(string raw, string stored)
        => Assert.Equal(stored, EventText.Clean(raw));

    [Fact]
    public void EventTextIsCapped()
        => Assert.Equal(EventText.MaxLength, EventText.Clean(new string('x', 5000)).Length);
}