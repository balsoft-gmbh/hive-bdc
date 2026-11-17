using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// What every state store must do the same way: the SQLite ledger of local installs, the Table Storage store of the Azure
/// run mode (in memory here, against a real account in <see cref="TableStateStoreLiveTests"/>).
/// </summary>
public static class StateStoreScenarios
{
    public static readonly IReadOnlyDictionary<string, Action<IStateStore>> All = new Dictionary<string, Action<IStateStore>>
    {
        [nameof(AReExportComesBackToTheTopWithoutCountingTwice)] = AReExportComesBackToTheTopWithoutCountingTwice,
        [nameof(TheSameNumberInAnotherCompanyOrFormatIsItsOwnExport)] = TheSameNumberInAnotherCompanyOrFormatIsItsOwnExport,
        [nameof(TodayAndTheMonthAreCountedPerTypeMostActiveFirst)] = TodayAndTheMonthAreCountedPerTypeMostActiveFirst,
        [nameof(RecentExportsAreNewestFirstAndKeepTheirSource)] = RecentExportsAreNewestFirstAndKeepTheirSource,
        [nameof(ALaterDeliveryWithoutRemoteIdKeepsTheEarlierOne)] = ALaterDeliveryWithoutRemoteIdKeepsTheEarlierOne,
        [nameof(ASearchReturnsOnlyThatDocumentAndABlankSearchTheNewestSteps)] = ASearchReturnsOnlyThatDocumentAndABlankSearchTheNewestSteps,
        [nameof(ASearchFindsPartOfTheNumberIdOrFileNameAndTreatsWildcardsAsText)] = ASearchFindsPartOfTheNumberIdOrFileNameAndTreatsWildcardsAsText,
        [nameof(ASearchMatchesAnEarlierInputOrNumberOfTheDocument)] = ASearchMatchesAnEarlierInputOrNumberOfTheDocument,
        [nameof(ASearchReturnsTheTwentyNewestMatchingDocuments)] = ASearchReturnsTheTwentyNewestMatchingDocuments,
        [nameof(TheNewestStepsComeFromAllDocumentsOldestFirst)] = TheNewestStepsComeFromAllDocumentsOldestFirst,
        [nameof(NeedsAttentionListsDocumentsWhoseLastStepFailedOrWaits)] = NeedsAttentionListsDocumentsWhoseLastStepFailedOrWaits,
        [nameof(AStoredStepIsCleanedAndHasItsTimeAndId)] = AStoredStepIsCleanedAndHasItsTimeAndId,
        [nameof(KeysWithCharactersTableStorageRefusesStillWork)] = KeysWithCharactersTableStorageRefusesStillWork,
    };

    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach (var name in All.Keys) names.Add(name);
        return names;
    }

    private static void AReExportComesBackToTheTopWithoutCountingTwice(IStateStore store)
    {
        store.Record("2026004721", "SalesInvoice", "zugferd", @"C:\out\2026004721.pdf", null, 10, "001");
        store.Record("2026004722", "SalesInvoice", "zugferd", @"C:\out\2026004722.pdf", null, 10, "001");
        store.Record("2026004721", "SalesInvoice", "zugferd", @"C:\out\2026004721.pdf", "https://x.sharepoint.com/sites/a", 12, "001");

        var m = store.Metrics(10);
        Assert.Equal(2, m.Recent.Count);
        Assert.Equal("2026004721", m.Recent[0].DocumentId);
        Assert.Equal("https://x.sharepoint.com/sites/a", m.Recent[0].TargetSite);
        Assert.Equal(12, m.Recent[0].SizeBytes);
        Assert.Equal(2, m.Total);
    }

    private static void TheSameNumberInAnotherCompanyOrFormatIsItsOwnExport(IStateStore store)
    {
        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1, "001");
        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1, "002");
        store.Record("RE-1", "SalesInvoice", "zugferd", "RE-1.pdf", null, 1, "001");

        var m = store.Metrics(10);
        Assert.Equal(3, m.Total);
        Assert.Equal(new (string, string?)[] { ("zugferd", "001"), ("xrechnung", "002"), ("xrechnung", "001") }, m.Recent.Select(r => (r.Format, r.DataAreaId)));
    }

    private static void TodayAndTheMonthAreCountedPerTypeMostActiveFirst(IStateStore store)
    {
        store.Record("RE-1", "SalesInvoice", "xrechnung", "RE-1.xml", null, 1);
        store.Record("GS-1", "Credit", "xrechnung", "GS-1.xml", null, 1);
        store.Record("RE-2", "SalesInvoice", "xrechnung", "RE-2.xml", null, 1);
        store.Record("AB-1", "Abc", "pdf", "AB-1.pdf", null, 1);

        var m = store.Metrics(10);
        Assert.Equal(4, m.Today);
        Assert.Equal(4, m.Month);
        Assert.Equal(new[] { ("SalesInvoice", 2), ("Abc", 1), ("Credit", 1) }, m.MonthByType.Select(t => (t.DocumentType, t.Count)));
    }

    private static void RecentExportsAreNewestFirstAndKeepTheirSource(IStateStore store)
    {
        for (int i = 1; i <= 5; i++)
            store.Record($"RE-{i}", "SalesInvoice", "xrechnung", $"RE-{i}.xml", null, i, "1000", i == 5 ? "word" : "json");

        var recent = store.Metrics(3).Recent;
        Assert.Equal(new[] { "RE-5", "RE-4", "RE-3" }, recent.Select(r => r.DocumentId));
        Assert.Equal(new[] { "word", "json", "json" }, recent.Select(r => r.Source));
        Assert.Equal(5, recent[0].SizeBytes);
        Assert.Equal("RE-5.xml", recent[0].OutputFile);
        Assert.Equal("xrechnung", recent[0].Format);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", recent[0].ExportedAt);
    }

    private static void ALaterDeliveryWithoutRemoteIdKeepsTheEarlierOne(IStateStore store)
    {
        store.RecordDelivery("1000|SalesInvoice|RE-1", "docuware:Invoices", "docuware", "Invoices", "17");
        store.RecordDelivery("1000|SalesInvoice|RE-1", "docuware:Invoices", "docuware", "Invoices", null);
        Assert.Equal("17", store.DeliveryRemoteId("1000|SalesInvoice|RE-1", "docuware:Invoices"));

        store.RecordDelivery("1000|SalesInvoice|RE-1", "docuware:Invoices", "docuware", "Invoices", "18");
        Assert.Equal("18", store.DeliveryRemoteId("1000|SalesInvoice|RE-1", "docuware:Invoices"));
        Assert.Null(store.DeliveryRemoteId("1000|SalesInvoice|RE-1", "sharepoint:Sales"));
        Assert.Null(store.DeliveryRemoteId("1000|SalesInvoice|RE-2", "docuware:Invoices"));
    }

    private static void ASearchReturnsOnlyThatDocumentAndABlankSearchTheNewestSteps(IStateStore store)
    {
        store.RecordEvent(new DocumentEvent("a", "a_1.json", "received", "ok", "first"));
        store.RecordEvent(new DocumentEvent("b", "b_1.json", "received", "ok", "second", DocumentNumber: "RE-2"));
        store.RecordEvent(new DocumentEvent("a", "a_1.json", "done", "ok", "third"));

        Assert.Equal(new[] { "first", "third" }, store.Events("a_1", 10).Select(e => e.Message));
        Assert.Equal(new[] { "second" }, store.Events("re-2", 10).Select(e => e.Message));
        Assert.Equal(new[] { "second", "third" }, store.Events(null, 2).Select(e => e.Message));
        Assert.Empty(store.Events("nothing", 10));
    }

    private static void ASearchFindsPartOfTheNumberIdOrFileNameAndTreatsWildcardsAsText(IStateStore store)
    {
        store.RecordEvent(new DocumentEvent("DEMO-02-Rechnung-ZUGFeRD-4Pos", "DEMO-02_Rechnung_ZUGFeRD_4Pos.docx", "received", "ok", "word"));
        store.RecordEvent(new DocumentEvent("RE-260042", "RE-260042_K-10010.json", "read", "ok", "pair", DocumentNumber: "RE-260042"));
        store.RecordEvent(new DocumentEvent("x", "50%_off.json", "read", "ok", "percent"));

        Assert.Equal(new[] { "word" }, store.Events("demo-02", 10).Select(e => e.Message));
        Assert.Equal(new[] { "pair" }, store.Events("260042", 10).Select(e => e.Message));
        Assert.Equal(new[] { "word" }, store.Events("Rechnung_ZUGFeRD", 10).Select(e => e.Message));
        Assert.Equal(new[] { "percent" }, store.Events("50%", 10).Select(e => e.Message));
        Assert.Empty(store.Events("%%", 10));
    }

    private static void ASearchMatchesAnEarlierInputOrNumberOfTheDocument(IStateStore store)
    {
        store.RecordEvent(new DocumentEvent("doc-x", "x_first.json", "read", "ok", "one", DocumentNumber: "RE-9"));
        store.RecordEvent(new DocumentEvent("doc-x", "x_second.json", "received", "ok", "two"));

        Assert.Equal(new[] { "one", "two" }, store.Events("x_first", 10).Select(e => e.Message));
        Assert.Equal(new[] { "one", "two" }, store.Events("re-9", 10).Select(e => e.Message));
    }

    private static void ASearchReturnsTheTwentyNewestMatchingDocuments(IStateStore store)
    {
        for (int i = 0; i < 25; i++)
            store.RecordEvent(new DocumentEvent($"doc{i:D2}", $"batch_{i:D2}.json", "received", "ok", $"m{i:D2}"));

        var found = store.Events("batch", 5000);
        Assert.Equal(Enumerable.Range(5, 20).Select(i => $"doc{i:D2}"), found.Select(e => e.DocumentId));
    }

    private static void TheNewestStepsComeFromAllDocumentsOldestFirst(IStateStore store)
    {
        string[] order = { "a", "b", "c", "a", "d", "b", "e", "a" };
        for (int i = 0; i < order.Length; i++)
            store.RecordEvent(new DocumentEvent(order[i], order[i] + ".json", "read", "ok", $"{order[i]}{i}"));

        Assert.Equal(new[] { "d4", "b5", "e6", "a7" }, store.Events(null, 4).Select(e => e.Message));
        Assert.Equal(new[] { "a0", "b1", "c2", "a3", "d4", "b5", "e6", "a7" }, store.Events(null, 100).Select(e => e.Message));
        Assert.Equal(new[] { "a7" }, store.Events("", 1).Select(e => e.Message));
    }

    private static void NeedsAttentionListsDocumentsWhoseLastStepFailedOrWaits(IStateStore store)
    {
        store.RecordEvent(new DocumentEvent("a", "a_1.json", "failed", "failed", "a failed", DocumentNumber: "RE-A", DocumentType: "SalesInvoice", DataAreaId: "1000", Target: "docuware"));
        store.RecordEvent(new DocumentEvent("b", "b_1.json", "failed", "failed", "b failed first"));
        store.RecordEvent(new DocumentEvent("b", "b_1.json", "done", "ok", "b done after a retry"));
        store.RecordEvent(new DocumentEvent("c", "c_1.json", "waiting", "waiting", "c waits"));

        var attention = store.Attention(10);
        Assert.Equal(new[] { "c waits", "a failed" }, attention.Select(e => e.Message));
        var a = attention[1];
        Assert.Equal(("a", "a_1.json", "failed", "RE-A", "SalesInvoice", "1000", "docuware"),
            (a.DocumentId, a.Input, a.Step, a.DocumentNumber, a.DocumentType, a.DataAreaId, a.Target));
        Assert.Equal(new[] { "c waits" }, store.Attention(1).Select(e => e.Message));
    }

    private static void AStoredStepIsCleanedAndHasItsTimeAndId(IStateStore store)
    {
        store.RecordEvent(new DocumentEvent("RE-7", "RE-7.json", "delivered", "ok", "Sent to max.mustermann@contoso.com\r\nsecond line", Target: "mail"));

        var stored = Assert.Single(store.Events("RE-7", 10));
        Assert.Equal("Sent to m***@contoso.com second line", stored.Message);
        Assert.Equal("mail", stored.Target);
        Assert.True(stored.Id > 0);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", stored.At);
    }

    private static void KeysWithCharactersTableStorageRefusesStillWork(IStateStore store)
    {
        const string id = @"RE/2026#7?a\b";
        store.RecordEvent(new DocumentEvent(id, @"in\RE#7.json", "failed", "failed", "odd key"));
        store.RecordDelivery(id, "sharepoint:/sites/a#b", "sharepoint", "/sites/a", "item?1");
        store.Record(id, "SalesInvoice", "xrechnung", "RE#7.xml", null, 1, "10/00");

        Assert.Equal(new[] { "odd key" }, store.Events("2026#7", 10).Select(e => e.Message));
        Assert.Equal("item?1", store.DeliveryRemoteId(id, "sharepoint:/sites/a#b"));
        Assert.Equal(id, Assert.Single(store.Metrics(10).Recent).DocumentId);
        Assert.Equal(id, Assert.Single(store.Attention(10)).DocumentId);
    }
}

public sealed class SqliteStateStoreContractTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "hivebdc-contract-" + Guid.NewGuid() + ".db");

    [Theory]
    [MemberData(nameof(StateStoreScenarios.Names), MemberType = typeof(StateStoreScenarios))]
    public void TheSqliteLedgerHolds(string scenario)
        => StateStoreScenarios.All[scenario](ExportLedger.CreateIfMissing(NullLogger.Instance, _db));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public sealed class TableStateStoreContractTests
{
    [Theory]
    [MemberData(nameof(StateStoreScenarios.Names), MemberType = typeof(StateStoreScenarios))]
    public void TheTableStoreHolds(string scenario)
        => StateStoreScenarios.All[scenario](new TableStateStore(new MemoryTableRows(), NullLogger.Instance));
}