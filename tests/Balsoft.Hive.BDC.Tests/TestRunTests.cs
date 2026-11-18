using System.Text;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure cases 56 and 57 (part 14d): the test run of one export and the pause until Start.</summary>
public sealed class TestRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-testrun-" + Guid.NewGuid());
    private readonly MemoryBlobStore _store = new();

    private sealed class MemoryBlobStore : IBlobStore
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public Uri ContainerUri { get; } = new("https://account.blob.core.windows.net/fo-out");
        public IReadOnlyList<string> List(string prefix) => Blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        public bool Exists(string name) => Blobs.ContainsKey(name);
        public byte[] Download(string name) => Blobs[name];
        public void Upload(string name, byte[] content, string contentType) => Blobs[name] = content;
        public void Move(string name, string newName) { Blobs[newName] = Blobs[name]; Blobs.Remove(name); }
    }

    public TestRunTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Input => Path.Combine(_root, "in");

    private BDCOptions Options(string sourceKind = "blob")
    {
        var options = new BDCOptions { Paused = true };
        options.Connections.Add(new ConnectionOptions { Id = "fo", Kind = "azure-storage", Secret = "UseDevelopmentStorage=true" });
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "SalesInvoice", DisplayName = "Sales invoice", Handler = DocumentHandlerKind.Pdf, Mail = false,
            Companies =
            {
                new DocumentRoute
                {
                    DataAreaId = "1000", Input = Input, Output = Path.Combine(_root, "out"),
                    Source = new SourceOptions { Kind = sourceKind, Connection = "fo", Settings = { ["Container"] = "fo-out" } },
                },
                new DocumentRoute { DataAreaId = "DEMF", Input = Path.Combine(_root, "demf"), Output = Path.Combine(_root, "out-demf") },
            },
        });
        options.Normalize();
        return options;
    }

    private void Exports(params string[] ids)
    {
        foreach (var id in ids)
        {
            _store.Blobs[id] = Encoding.UTF8.GetBytes($$"""{ "dataAreaId": "1000", "documentNumber": "{{id}}" }""");
            _store.Blobs[id + ".pdf"] = Encoding.UTF8.GetBytes("%PDF");
        }
    }

    private readonly TableStateStore _state = new(new MemoryTableRows(), NullLogger.Instance);

    private BlobStager Stager() => new(NullLogger.Instance, _state, (_, _) => _store);

    /// <summary>Stands in for the pipeline: moves every staged pair to processed or error, or leaves it waiting.</summary>
    private Func<BDCOptions, DocumentProcessor.ScanResult> Scan(string outcome, List<int>? seen = null) => opts =>
    {
        var route = opts.DocumentTypes.Single().Companies.Single();
        var files = Directory.EnumerateFiles(route.Input).ToList();
        seen?.Add(files.Count(f => f.EndsWith(".json", StringComparison.Ordinal)));
        if (outcome == "waiting") return new DocumentProcessor.ScanResult(0, 0, 1);
        string target = Path.Combine(route.Input, outcome);
        Directory.CreateDirectory(target);
        foreach (var f in files) File.Move(f, Path.Combine(target, Path.GetFileName(f)));
        return outcome == "processed" ? new DocumentProcessor.ScanResult(1, 0, 0) : new DocumentProcessor.ScanResult(0, 1, 0);
    };

    [Fact]
    public void OnlyTheFirstWaitingExportRunsAndASuccessIsSettled()
    {
        Exports("RE-1", "RE-2", "RE-3");
        var seen = new List<int>();
        var result = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan("processed", seen), NullLogger.Instance);
        Assert.True(result.Ok);
        Assert.Equal("RE-1", result.DocumentId);
        Assert.Equal(new[] { 1 }, seen);
        Assert.True(_store.Exists("processed/RE-1"));
        Assert.True(_store.Exists("processed/RE-1.pdf"));
        Assert.True(_store.Exists("RE-2"));
        Assert.True(_store.Exists("RE-3"));
        Assert.False(Directory.Exists(Path.Combine(Input, ".hive-sources")) && Directory.EnumerateFiles(Path.Combine(Input, ".hive-sources")).Any());
    }

    [Theory]
    [InlineData("error")]
    [InlineData("waiting")]
    public void AFailedOrWaitingRunLeavesTheExportWhereItWasAndCanRunAgain(string outcome)
    {
        Exports("RE-1", "RE-2");
        var result = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan(outcome), NullLogger.Instance);
        Assert.False(result.Ok);
        Assert.Equal("RE-1", result.DocumentId);
        Assert.True(_store.Exists("RE-1"));
        Assert.True(_store.Exists("RE-1.pdf"));
        Assert.False(_store.Blobs.Keys.Any(k => k.StartsWith("error/", StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateFiles(Input, "*", SearchOption.AllDirectories));
        Assert.Contains(_state.Events("RE-1", 50), e => e.Message.StartsWith("Test run: nothing was moved", StringComparison.Ordinal));

        var again = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan("processed"), NullLogger.Instance);
        Assert.True(again.Ok);
        Assert.Equal("RE-1", again.DocumentId);
        Assert.True(_store.Exists("processed/RE-1"));
    }

    private void Export(string id, string type, string company)
    {
        _store.Blobs[id] = Encoding.UTF8.GetBytes($$"""{ "documentType": "{{type}}", "dataAreaId": "{{company}}", "documentNumber": "{{id}}" }""");
        _store.Blobs[id + ".pdf"] = Encoding.UTF8.GetBytes("%PDF");
    }

    [Fact]
    public void ASharedContainerGivesOnlyAnExportOfTheRequestedTypeAndCompany()
    {
        Export("A-1", "SalesInvoice", "DEMF");
        Export("B-1", "SalesConfirmation", "1000");
        Export("C-1", "SalesInvoice", "1000");
        var result = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan("processed"), NullLogger.Instance);
        Assert.True(result.Ok);
        Assert.Equal("C-1", result.DocumentId);
        Assert.True(_store.Exists("processed/C-1"));
        Assert.True(_store.Exists("A-1"));
        Assert.True(_store.Exists("B-1"));
        Assert.True(_store.Exists("A-1.pdf"));
    }

    [Fact]
    public void OnlyOtherTypesAndCompaniesWaitingIsSaid()
    {
        Export("A-1", "SalesInvoice", "DEMF");
        var result = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan("processed"), NullLogger.Instance);
        Assert.False(result.Ok);
        Assert.Null(result.DocumentId);
        Assert.Contains("No export is waiting", result.Message);
        Assert.True(_store.Exists("A-1"));
    }

    [Fact]
    public void OtherPairsInTheSharedStagingFolderAreNotTouched()
    {
        Directory.CreateDirectory(Input);
        File.WriteAllText(Path.Combine(Input, "X-1_blob.json"), """{ "documentType": "SalesInvoice", "dataAreaId": "DEMF" }""");
        Export("C-1", "SalesInvoice", "1000");
        var seen = new List<int>();
        var result = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan("processed", seen), NullLogger.Instance);
        Assert.True(result.Ok);
        Assert.Equal(new[] { 1 }, seen);
        Assert.True(File.Exists(Path.Combine(Input, "X-1_blob.json")));
        Assert.False(File.Exists(Path.Combine(Input, "processed", "X-1_blob.json")));
    }

    [Fact]
    public void NothingWaitingIsSaid()
    {
        var result = TestRun.Execute(Options(), "SalesInvoice", "1000", Stager(), Scan("processed"), NullLogger.Instance);
        Assert.False(result.Ok);
        Assert.Null(result.DocumentId);
        Assert.Contains("No export is waiting", result.Message);
    }

    [Theory]
    [InlineData("SalesInvoice", "2000", "no company 2000")]
    [InlineData("PurchaseOrder", "1000", "no document type")]
    [InlineData("SalesInvoice", "DEMF", "Blob Storage")]
    public void ARunNeedsAKnownCompanyWithABlobSource(string type, string company, string expected)
    {
        var result = TestRun.Execute(Options(), type, company, Stager(), Scan("processed"), NullLogger.Instance);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public void PausedSettingsHoldTheWorkerWithItsReasonAndOtherReasonsComeFirst()
    {
        Assert.Equal(TestRuns.PausedReason, TestRuns.Hold(new BDCOptions { Paused = true }, null));
        Assert.Equal("Table Storage cannot be reached", TestRuns.Hold(new BDCOptions { Paused = true }, "Table Storage cannot be reached"));
        Assert.Null(TestRuns.Hold(new BDCOptions(), null));
    }

    [Fact]
    public async Task OneRequestAtATimeAndTheWorkerIsWokenAtOnce()
    {
        var runs = new TestRuns();
        var waiting = runs.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(runs.TryRequest("SalesInvoice", "1000", out var first));
        Assert.False(runs.TryRequest("SalesInvoice", "1000", out _));
        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))));

        var taken = runs.Take();
        Assert.NotNull(taken);
        Assert.Null(runs.Take());
        taken!.Complete(new TestRunResult(true, "RE-1", "Done."));
        Assert.Equal("RE-1", (await first).DocumentId);
        Assert.True(runs.TryRequest("SalesInvoice", "1000", out _));
    }
}