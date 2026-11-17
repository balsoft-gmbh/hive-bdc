using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>What only the Table Storage store has: a restart on the same tables, prune by id, failures of the service.</summary>
public sealed class TableStateStoreTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly MemoryTableRows _rows = new();

    [Fact]
    public void AStoreOpenedAgainOnTheSameTablesKeepsCountsDedupeAndHistory()
    {
        var first = new TableStateStore(_rows, NullLogger.Instance);
        first.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1, "1000");
        first.Record("RE-2", "SalesInvoice", "xrechnung", "RE-2.xml", null, 1, "1000");
        first.RecordEvent(new DocumentEvent("RE-1", "RE-1.json", "done", "ok", "delivered"));
        first.RecordDelivery("1000|SalesInvoice|RE-1", "docuware:Invoices", "docuware", "Invoices", "17");

        var second = new TableStateStore(_rows, NullLogger.Instance);
        second.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", "https://x.sharepoint.com/sites/a", 2, "1000");
        second.RecordEvent(new DocumentEvent("RE-1", "RE-1.json", "received", "ok", "again"));

        var m = second.Metrics(10);
        Assert.Equal(2, m.Total);
        Assert.Equal(new[] { "RE-1", "RE-2" }, m.Recent.Select(r => r.DocumentId));
        Assert.Equal(2, _rows.Count("HiveBDCExports"));
        var history = second.Events("RE-1", 10);
        Assert.Equal(new[] { "delivered", "again" }, history.Select(e => e.Message));
        Assert.True(history[1].Id > history[0].Id);
        Assert.Equal("17", second.DeliveryRemoteId("1000|SalesInvoice|RE-1", "docuware:Invoices"));
    }

    [Fact]
    public void PruneDropsWhatIsOlderThanTheRetentionAndKeepsTheLifetimeTotal()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
        var store = new TableStateStore(_rows, NullLogger.Instance, clock: clock);
        store.Record("OLD-1", "SalesInvoice", "xrechnung", "OLD-1.xml", null, 1);
        store.RecordEvent(new DocumentEvent("OLD-1", "OLD-1.json", "failed", "failed", "old failure"));
        store.RecordDelivery("OLD-1", "sharepoint:Sales", "sharepoint", "Sales", "a");

        clock.Now = clock.Now.AddDays(61);
        store.Record("NEW-1", "SalesInvoice", "xrechnung", "NEW-1.xml", null, 1);
        store.RecordEvent(new DocumentEvent("NEW-1", "NEW-1.json", "done", "ok", "new"));
        store.RecordDelivery("NEW-1", "sharepoint:Sales", "sharepoint", "Sales", "b");

        Assert.Equal(1, store.Prune(60));

        var m = store.Metrics(10);
        Assert.Equal(new[] { "NEW-1" }, m.Recent.Select(r => r.DocumentId));
        Assert.Equal(2, m.Total);
        Assert.Empty(store.Events("OLD-1", 10));
        Assert.Empty(store.Attention(10));
        Assert.Null(store.DeliveryRemoteId("OLD-1", "sharepoint:Sales"));
        Assert.Equal("b", store.DeliveryRemoteId("NEW-1", "sharepoint:Sales"));
        Assert.Equal((1, 1, 1, 1), (_rows.Count("HiveBDCExports"), _rows.Count("HiveBDCEvents"), _rows.Count("HiveBDCDocuments"), _rows.Count("HiveBDCDeliveries")));
        Assert.Equal(new[] { "new" }, new TableStateStore(_rows, NullLogger.Instance, clock: clock).Events(null, 10).Select(e => e.Message));
    }

    [Fact]
    public void PruneIfDueRunsAtMostOncePerDay()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var store = new TableStateStore(_rows, NullLogger.Instance, clock: clock);

        store.PruneIfDue();
        clock.Now = clock.Now.AddHours(23);
        store.PruneIfDue();
        Assert.Equal(1, _rows.QueriesOf("HiveBDCDeliveries"));

        clock.Now = clock.Now.AddHours(2);
        store.PruneIfDue();
        Assert.Equal(2, _rows.QueriesOf("HiveBDCDeliveries"));
    }

    [Fact]
    public void AnUnreachableTableNeverStopsADocumentAndTheStoreRecoversAfterwards()
    {
        _rows.FailOn = (_, _) => true;
        var store = new TableStateStore(_rows, NullLogger.Instance);

        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1);
        store.RecordEvent(new DocumentEvent("RE-1", "RE-1.json", "done", "ok", "delivered"));
        store.RecordDelivery("RE-1", "mail", "mail", null, null);
        Assert.Null(store.DeliveryRemoteId("RE-1", "mail"));
        Assert.Empty(store.Events(null, 10));
        Assert.Empty(store.Attention(10));
        Assert.Equal(0, store.Prune());
        var empty = store.Metrics(10);
        Assert.Equal((0, 0, 0L, 0), (empty.Today, empty.Month, empty.Total, empty.Recent.Count));

        _rows.FailOn = null;
        store.Record("RE-2", "SalesInvoice", "xrechnung", "RE-2.xml", null, 1);
        store.RecordEvent(new DocumentEvent("RE-2", "RE-2.json", "done", "ok", "delivered"));
        Assert.Equal(1, store.Metrics(10).Total);
        Assert.Equal(new[] { "delivered" }, store.Events("RE-2", 10).Select(e => e.Message));
    }

    [Fact]
    public void AReExportWhoseOldRowCouldNotBeDeletedIsRepairedOnTheNextLoad()
    {
        var store = new TableStateStore(_rows, NullLogger.Instance);
        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1);
        _rows.FailOn = (operation, table) => operation == "Delete" && table == "HiveBDCExports";
        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 2);
        Assert.Equal(2, _rows.Count("HiveBDCExports"));

        _rows.FailOn = null;
        var m = store.Metrics(10);
        Assert.Equal(2, Assert.Single(m.Recent).SizeBytes);
        Assert.Equal(1, m.Total);
        Assert.Equal(1, _rows.Count("HiveBDCExports"));
    }

    [Fact]
    public void TheLifetimeCounterReadsAgainWhenAnotherWriterChangedIt()
    {
        var store = new TableStateStore(_rows, NullLogger.Instance);
        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1);
        _rows.OtherWriterChangesLeft = 2;
        store.Record("RE-2", "SalesInvoice", "xrechnung", "RE-2.xml", null, 1);

        Assert.Equal(4, _rows.Stored("HiveBDCMeta", "meta", "lifetime_exports")!.Number("Value"));
        Assert.Equal(4, store.Metrics(10).Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1Hive")]
    [InlineData("Hive-BDC")]
    [InlineData("HiveBDCHiveBDCHiveBDCHiveBDCHiveBDCHiveBDCHiveBDCHiveBDC")]
    public void ATablePrefixAzureWouldRefuseIsRefusedAtStart(string prefix)
        => Assert.Throws<ArgumentException>(() => new TableStateStore(_rows, NullLogger.Instance, prefix));
}