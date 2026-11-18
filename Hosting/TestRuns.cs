using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>What a test run did: the export it took (if any), whether it went all the way through, and why not.</summary>
public sealed record TestRunResult(bool Ok, string? DocumentId, string Message);

/// <summary>A test run the console asked for; the worker takes it and completes it.</summary>
public sealed class TestRunRequest(string documentType, string company, Action<TestRunResult> complete)
{
    public string DocumentType { get; } = documentType;
    public string Company { get; } = company;

    public void Complete(TestRunResult result) => complete(result);
}

/// <summary>
/// Hands one test run at a time from the web console to the worker and wakes the worker for it (failure cases 56, 57).
/// While the settings are paused the worker takes nothing else.
/// </summary>
public sealed class TestRuns
{
    public const string PausedReason = "set-up not finished: Hive BDC starts taking documents when Start is pressed in the set-up assistant.";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private TestRunRequest? _pending;
    private bool _running;

    /// <summary>The worker's reason to take nothing this cycle: the other reasons first, then the pause.</summary>
    public static string? Hold(BDCOptions opts, string? gateHold) => gateHold ?? (opts.Paused ? PausedReason : null);

    public bool TryRequest(string documentType, string company, out Task<TestRunResult> result)
    {
        lock (_gate)
        {
            if (_pending is not null || _running)
            {
                result = Task.FromResult(new TestRunResult(false, null, "A test run is already going."));
                return false;
            }
            var done = new TaskCompletionSource<TestRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = new TestRunRequest(documentType, company, r =>
            {
                lock (_gate) _running = false;
                done.TrySetResult(r);
            });
            result = done.Task;
        }
        _wake.Release();
        return true;
    }

    /// <summary>The waiting request, once; it counts as running until it is completed.</summary>
    public TestRunRequest? Take()
    {
        lock (_gate)
        {
            var request = _pending;
            _pending = null;
            if (request is not null) _running = true;
            return request;
        }
    }

    /// <summary>The worker's pause between cycles, cut short by a test run request.</summary>
    public async Task WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        await Task.WhenAny(Task.Delay(delay, stoppingToken), _wake.WaitAsync(stoppingToken));
        if (stoppingToken.IsCancellationRequested) throw new TaskCanceledException();
        while (_wake.CurrentCount > 0) _wake.Wait(0);
    }
}

/// <summary>
/// A test run: the first waiting export of one company's Blob source, through every step. A success is settled like
/// any document; a failed or waiting one leaves the export where it was in the container, so the run can be repeated
/// after a fix (failure case 56).
/// </summary>
public static class TestRun
{
    public static TestRunResult Execute(BDCOptions opts, string documentType, string company, BlobStager blobs,
        Func<BDCOptions, DocumentProcessor.ScanResult> scan, ILogger log)
    {
        var type = opts.DocumentTypes.FirstOrDefault(t => string.Equals(t.Id, documentType, StringComparison.OrdinalIgnoreCase));
        if (type is null) return new TestRunResult(false, null, $"There is no document type {documentType}.");
        var route = type.Companies.FirstOrDefault(r => string.Equals(r.DataAreaId, company, StringComparison.OrdinalIgnoreCase));
        if (route is null) return new TestRunResult(false, null, $"{Name(type)} has no company {company}.");
        if (route.Source is not { Kind: "blob" })
            return new TestRunResult(false, null, "A test run takes its export from Blob Storage, and this company gets its documents another way. Press Start and follow the document history.");

        var one = Only(opts, type, route);
        string? id = blobs.StageOne(one, route, out var problem);
        if (id is null)
            return new TestRunResult(false, null, problem ?? $"No export is waiting for {Name(type)} of company {route.DataAreaId}.");

        log.LogInformation("Test run of {Type} {Company}: {Id}.", type.Id, route.DataAreaId, id);
        scan(one);
        string staged = BlobStager.StagedJson(id);
        if (File.Exists(Path.Combine(route.Input, "processed", staged)))
        {
            blobs.SettleRoute(one, route);
            return new TestRunResult(true, id, "Done. The export went through every step.");
        }
        bool failed = File.Exists(Path.Combine(route.Input, "error", staged));
        blobs.Abandon(route, id);
        return new TestRunResult(false, id, failed
            ? "A step failed, so the export stays where it was. The steps below name the reason."
            : "The document is waiting, so the export stays where it was. The steps below name the reason.");
    }

    private static string Name(DocumentTypeOptions type) => string.IsNullOrWhiteSpace(type.DisplayName) ? type.Id : type.DisplayName;

    /// <summary>The settings with this one document type and company; connections, mail and the rest are shared.</summary>
    private static BDCOptions Only(BDCOptions opts, DocumentTypeOptions type, DocumentRoute route)
    {
        var copy = JsonSerializer.Deserialize<DocumentTypeOptions>(JsonSerializer.Serialize(type))!;
        copy.Companies = new List<DocumentRoute> { route };
        return new BDCOptions
        {
            PollSeconds = opts.PollSeconds,
            LogDir = opts.LogDir,
            License = opts.License,
            SharePointAuth = opts.SharePointAuth,
            Mail = opts.Mail,
            Connections = opts.Connections,
            DocumentTypes = new List<DocumentTypeOptions> { copy },
        };
    }
}