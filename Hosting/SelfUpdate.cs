using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Where an Azure instance keeps its packages and its update state. App Service runs the ZIP that
/// <c>SitePackages/packagename.txt</c> names (WEBSITE_RUN_FROM_PACKAGE=1) and mounts it again when the process ends,
/// which is how the instance switches itself (design part 15b-0).
/// </summary>
public sealed class UpdatePlaces(string dataRoot)
{
    public const string AzureDataRoot = "/home/data";

    public string SitePackages => Path.Combine(dataRoot, "SitePackages");
    public string NameFile => Path.Combine(SitePackages, "packagename.txt");
    public string Work => Path.Combine(dataRoot, "hivebdc", "updates");
    public string StateFile => Path.Combine(Work, "state.json");
    public string Backups => Path.Combine(Work, "backups");

    /// <summary>The places of this process when it runs as an Azure instance with a package folder, else null.</summary>
    public static UpdatePlaces? ForAzure()
        => string.Equals(Environment.GetEnvironmentVariable("HiveBDC__RunMode"), RunModes.Azure, StringComparison.OrdinalIgnoreCase)
           && Directory.Exists(Path.Combine(AzureDataRoot, "SitePackages"))
            ? new UpdatePlaces(AzureDataRoot)
            : null;
}

public enum StartGuard { Continue, Exit }

public sealed record UpdateHealthLine(string Name, bool Ok, string Detail);

public sealed record UpdateOutcome(bool Ok, string Message);

public sealed class UpdateState
{
    public InstalledVersion Installed { get; set; } = new();
    public PreviousVersion? Previous { get; set; }
    public PendingSwitch? Pending { get; set; }
    public ScheduledUpdate? Schedule { get; set; }
    public LastCheck? LastCheck { get; set; }
    public List<UpdateStep> Steps { get; set; } = [];
    public List<UpdateStep> Checks { get; set; } = [];
    public List<UpdateHistoryEntry> History { get; set; } = [];
}

public sealed class InstalledVersion
{
    public string Version { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public string By { get; set; } = "";
}

public sealed class PreviousVersion
{
    public string Version { get; set; } = "";
    public string Package { get; set; } = "";
}

public sealed class PendingSwitch
{
    public string Kind { get; set; } = "update";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Package { get; set; } = "";
    public string PreviousPackage { get; set; } = "";
    public string By { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset SwitchedAt { get; set; }
    public int Starts { get; set; }
}

public sealed class ScheduledUpdate
{
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
}

public sealed class LastCheck
{
    public DateTimeOffset At { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public UpdateManifest? Available { get; set; }
}

public sealed class UpdateStep
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class UpdateHistoryEntry
{
    public string Version { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
    public string State { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>What the Service page shows: the state plus what this build can do; never a package address or signature.</summary>
public sealed record UpdateView(
    string Version, string Runtime, string TimeZone, bool CanUpdate, string? CannotUpdate, InstalledVersion Installed, PreviousVersion? Previous,
    PendingSwitch? Pending, ScheduledUpdate? Schedule, LastCheck? LastCheck, UpdateManifest? Available,
    IReadOnlyList<UpdateStep> Steps, IReadOnlyList<UpdateStep> Checks, IReadOnlyList<UpdateHistoryEntry> History);

/// <summary>
/// An Azure instance updating itself from the feed (design part 15b, failure cases 82 to 93): the feed entry and the ZIP
/// checked against Balsoft's update key, settings backup, switch of packagename.txt and the end of this process; the new
/// version checks itself on start and returns to the previous package when it does not become healthy. Everything that
/// changes the state runs under one lock, so a download, a return, a plan and a check never overwrite each other.
/// </summary>
public sealed class SelfUpdate(
    UpdatePlaces places, IUpdateFeed feed, string version, RSA? key, Func<string?> readSettings, Action endProcess,
    TimeProvider clock, ILogger log, TimeSpan? exitDelay = null, Func<TimeSpan, CancellationToken, Task>? wait = null,
    TimeSpan? downloadLimit = null)
{
    public const string PackageInfoName = "hivebdc-package.json";
    public static readonly string CurrentRuntime = $"DOTNETCORE|{Environment.Version.Major}.{Environment.Version.Minor}";
    private const string NoKey = "This build of Hive BDC cannot check update signatures, so it does not update itself.";
    private const string Busy = "An update is running already.";
    private const int MaxStarts = 3;
    private const long MaxPackageBytes = 1L << 30;
    private const int MaxNotes = 10;
    private const int MaxNoteLength = 300;
    private static readonly Regex PlainVersion = new(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,6}\z");
    private static readonly TimeSpan HealthyWithin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealthRetry = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeSpan _exitDelay = exitDelay ?? TimeSpan.FromSeconds(2);
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;
    // Below App Service's 230 s request limit: "Update now" answers within the request that started it.
    private readonly TimeSpan _downloadLimit = downloadLimit ?? TimeSpan.FromMinutes(3);
    private readonly string? _keyFingerprint = key is null ? null : UpdateSignature.Fingerprint(key);

    public UpdateView Describe()
    {
        var state = Load(places);
        var known = state.LastCheck?.Available;
        var available = known is not null && IsNewer(known.Version, version) ? Public(known) : null;
        var lastCheck = state.LastCheck is { } c
            ? new LastCheck { At = c.At, Ok = c.Ok, Message = c.Message, Available = c.Available is null ? null : Public(c.Available) }
            : null;
        return new UpdateView(version, CurrentRuntime, clock.LocalTimeZone.Id, key is not null, key is null ? NoKey : null, state.Installed,
            state.Previous, state.Pending, state.Schedule, lastCheck, available, state.Steps, state.Checks, state.History);
    }

    public async Task<UpdateOutcome> Check(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) return new UpdateOutcome(false, "An update is running; Hive BDC looks again afterwards.");
        try
        {
            var state = Load(places);
            var (_, outcome) = await Fetch(state, ct);
            return outcome;
        }
        finally { Gate.Release(); }
    }

    public async Task<UpdateOutcome> Run(string by, CancellationToken ct)
    {
        if (key is null) return new UpdateOutcome(false, NoKey);
        if (!await Gate.WaitAsync(0, ct)) return new UpdateOutcome(false, Busy);
        try { return await RunLocked(by, ct); }
        finally { Gate.Release(); }
    }

    public UpdateOutcome Schedule(string by, DateTimeOffset at)
    {
        if (key is null) return new UpdateOutcome(false, NoKey);
        if (!Gate.Wait(0)) return new UpdateOutcome(false, Busy);
        try
        {
            var state = Load(places);
            if (state.Pending is { } running) return new UpdateOutcome(false, $"An update to {running.To} is running.");
            state.Schedule = new ScheduledUpdate { At = at, By = by };
            Save(places, state);
            return new UpdateOutcome(true, $"Hive BDC updates itself at {Local(at):HH:mm}.");
        }
        finally { Gate.Release(); }
    }

    public UpdateOutcome Cancel(string by)
    {
        if (!Gate.Wait(0)) return new UpdateOutcome(false, Busy);
        try
        {
            var state = Load(places);
            if (state.Schedule is null) return new UpdateOutcome(false, "No update is planned.");
            state.Schedule = null;
            Save(places, state);
            log.LogInformation("Update: the planned update was cancelled by {By}.", by);
            return new UpdateOutcome(true, "The planned update is cancelled.");
        }
        finally { Gate.Release(); }
    }

    public UpdateOutcome Return(string by)
    {
        if (!Gate.Wait(0)) return new UpdateOutcome(false, Busy);
        try
        {
            var state = Load(places);
            if (state.Pending is { } running) return new UpdateOutcome(false, $"An update to {running.To} is running.");
            string current = ReadName(places);
            if (state.Previous is not { } previous || !IsPackageName(previous.Package) || !IsPackageName(current)
                || !File.Exists(Path.Combine(places.SitePackages, previous.Package)))
                return new UpdateOutcome(false, "There is no previous version to return to.");

            var now = clock.GetUtcNow();
            state.Pending = new PendingSwitch
            {
                Kind = "return", From = version, To = previous.Version, Package = previous.Package, PreviousPackage = current,
                By = by, StartedAt = now, SwitchedAt = now,
            };
            state.Steps = [];
            state.Checks = [];
            Step(state, "switch", "done", $"Restarting with {previous.Version}");
            WriteName(places, previous.Package);
            log.LogInformation("Update: returning from {From} to {To} for {By}.", version, previous.Version, by);
            EndSoon();
            return new UpdateOutcome(true, $"Returning to {previous.Version}. Hive BDC restarts now, which takes 2 to 4 minutes.");
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// The first lines of the web start in Azure: counts the starts of a pending switch and puts the previous package back
    /// at the third start that did not become healthy (failure case 86); a switch that did not take is undone.
    /// </summary>
    public static StartGuard Guard(UpdatePlaces places, string version, TimeProvider clock)
    {
        var state = Load(places);
        if (state.Pending is not { } pending) return StartGuard.Continue;
        var now = clock.GetUtcNow();
        if (pending.To == version)
        {
            pending.Starts++;
            if (pending.Starts >= MaxStarts)
            {
                if (!IsPackageName(pending.PreviousPackage))
                {
                    History(state, pending.To, now, pending.By, "stopped", $"{pending.To} did not start properly, and there is no usable previous package to return to.");
                    state.Pending = null;
                    Save(places, state);
                    return StartGuard.Continue;
                }
                WriteName(places, pending.PreviousPackage);
                state.Pending = null;
                History(state, pending.To, now, pending.By, "returned", $"Returned to {pending.From} by itself: {pending.To} did not start properly {MaxStarts} times.");
                Save(places, state);
                return StartGuard.Exit;
            }
            Save(places, state);
            return StartGuard.Continue;
        }

        if (pending.From == version && IsPackageName(pending.PreviousPackage)) WriteName(places, pending.PreviousPackage);
        state.Pending = null;
        History(state, pending.To, now, pending.By, "stopped", pending.From == version
            ? $"Still {pending.From} after the restart; the switch to {pending.To} did not take, nothing changed."
            : $"{version} was deployed while the switch to {pending.To} ran.");
        Save(places, state);
        return StartGuard.Continue;
    }

    /// <summary>After a start: records a deployment, or finishes the pending switch once the instance is healthy.</summary>
    public async Task Settle(Func<CancellationToken, Task<IReadOnlyList<UpdateHealthLine>>> health, CancellationToken ct)
    {
        PendingSwitch? pending;
        await Gate.WaitAsync(ct);
        try
        {
            var state = Load(places);
            if (state.Pending is null && state.Installed.Version != version)
            {
                bool first = state.Installed.Version.Length == 0;
                var now = clock.GetUtcNow();
                state.Installed = new InstalledVersion { Version = version, Since = now, By = first ? "installation" : "deployment" };
                History(state, version, now, state.Installed.By, "installed", first ? "Installed." : "Installed by a deployment.");
                state.LastCheck = null;   // what the feed offers is judged against the new version: look again at once
                Save(places, state);
            }
            pending = state.Pending;
        }
        finally { Gate.Release(); }
        if (pending is null || pending.To != version) return;

        while (true)
        {
            var lines = await health(ct);
            var now = clock.GetUtcNow();
            var bad = lines.Where(l => !l.Ok).ToList();
            if (bad.Count == 0 || now - pending.SwitchedAt >= HealthyWithin)
            {
                await Gate.WaitAsync(ct);
                try
                {
                    // Act only on the switch this start belongs to: something else may have ended it meanwhile.
                    var state = Load(places);
                    if (state.Pending is not { } still || still.StartedAt != pending.StartedAt || still.To != pending.To) return;
                    if (bad.Count == 0)
                    {
                        Finish(state, still, lines, now);
                        return;
                    }
                    if (IsPackageName(still.PreviousPackage)) WriteName(places, still.PreviousPackage);
                    state.Pending = null;
                    state.Checks = lines.Select(l => new UpdateStep { Name = l.Name, State = l.Ok ? "done" : "failed", At = now, Detail = l.Detail }).ToList();
                    History(state, still.To, now, still.By, "returned", $"Returned to {still.From} by itself: {string.Join(" ", bad.Select(b => b.Detail))}");
                    Save(places, state);
                    log.LogWarning("Update: {To} did not become healthy within 10 minutes; returning to {From}.", still.To, still.From);
                    EndSoon();
                    return;
                }
                finally { Gate.Release(); }
            }
            await _wait(HealthRetry, ct);
        }
    }

    /// <summary>Every minute: looks for updates once a day after 06:00 and starts a planned update when it is due.</summary>
    public async Task Tick(CancellationToken ct)
    {
        var state = Load(places);
        var now = clock.GetUtcNow();
        var local = Local(now);
        var sixToday = new DateTimeOffset(local.Date.AddHours(6), clock.LocalTimeZone.GetUtcOffset(local.Date.AddHours(6)));
        if (state.LastCheck is null || (local >= sixToday && state.LastCheck.At < sixToday))
            await Check(ct);

        state = Load(places);
        if (state.Schedule is null || now < state.Schedule.At || state.Pending is not null) return;
        if (!await Gate.WaitAsync(0, ct)) return;   // a running update keeps the plan for the next minute
        try
        {
            state = Load(places);
            if (state.Schedule is not { } planned || now < planned.At || state.Pending is not null) return;
            state.Schedule = null;
            Save(places, state);
            if (key is not null) await RunLocked(planned.By, ct);
        }
        finally { Gate.Release(); }
    }

    private async Task<UpdateOutcome> RunLocked(string by, CancellationToken ct)
    {
        var state = Load(places);
        if (state.Pending is { } running)
            return new UpdateOutcome(false, $"An update to {running.To} is running, started by {running.By} at {Local(running.StartedAt):HH:mm}.");
        string current = ReadName(places);
        if (!IsPackageName(current) || !File.Exists(Path.Combine(places.SitePackages, current)))
            return new UpdateOutcome(false, "This instance does not run from a package in SitePackages, so it could not return to it; it is not updated.");

        var (manifest, refused) = await Fetch(state, ct);
        if (manifest is null) return refused;
        // Signed, but its version still becomes a file name: it must be plain digits.
        if (!PlainVersion.IsMatch(manifest.Version))
            return new UpdateOutcome(false, $"The feed names \"{manifest.Version[..Math.Min(40, manifest.Version.Length)]}\", which is no plain version number.");
        if (manifest.Size is <= 0 or > MaxPackageBytes)
            return new UpdateOutcome(false, $"The feed names a package of {manifest.Size} bytes, which cannot be right.");
        if (!IsNewer(manifest.Version, version)) return new UpdateOutcome(false, $"{version} is the newest version.");
        if (manifest.Runtime != CurrentRuntime)
            return new UpdateOutcome(false, $"Hive BDC {manifest.Version} needs {manifest.Runtime}; this instance runs {CurrentRuntime}.");

        state.Steps = [];
        state.Checks = [];
        string package = $"hivebdc-{manifest.Version}.zip";
        string file = Path.Combine(places.SitePackages, package), part = file + ".part";

        Step(state, "download", "running", "");
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(_downloadLimit);
            try
            {
                await using var target = File.Create(part);
                await using var source = await feed.Open(manifest.Url, limit.Token);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, limit.Token)) > 0)
                {
                    total += read;
                    if (total > manifest.Size) throw new InvalidDataException($"it is longer than the {manifest.Size} bytes the feed names");
                    await target.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                }
                if (total != manifest.Size) throw new InvalidDataException($"it has {total} bytes, the feed names {manifest.Size}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                TryDelete(part);
                string why = limit.IsCancellationRequested ? $"it took longer than {Span(_downloadLimit)}" : e.Message;
                return Stop(state, manifest.Version, by, "download", $"The download stopped: {why}.");
            }
        }
        Step(state, "download", "done", $"{package}, {manifest.Size / 1048576.0:0.0} MB");

        Step(state, "signature", "running", "");
        string? problem = null;
        await using (var read = File.OpenRead(part))
        {
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(read, ct)), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                problem = "The file is not the one Balsoft signed: its checksum differs.";
            read.Position = 0;
            if (problem is null && !UpdateSignature.Verify(read, manifest.Signature, key!))
                problem = "The file is not the one Balsoft signed: the signature does not match.";
        }
        problem ??= PackageProblem(part, manifest.Version);
        if (problem is not null)
        {
            TryDelete(part);
            return Stop(state, manifest.Version, by, "signature", problem);
        }
        File.Move(part, file, overwrite: true);
        Step(state, "signature", "done", "Made and signed by Balsoft GmbH");

        Step(state, "backup", "running", "");
        try
        {
            if (readSettings() is { } settings)
            {
                Directory.CreateDirectory(places.Backups);
                string name = $"settings-{version}-{Local(clock.GetUtcNow()):yyyyMMdd-HHmm}.json";
                File.WriteAllText(Path.Combine(places.Backups, name), settings);
                foreach (var old in Directory.GetFiles(places.Backups, "settings-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(10))
                    TryDelete(old);
                Step(state, "backup", "done", name);
            }
            else Step(state, "backup", "done", "No settings yet");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            TryDelete(file);
            return Stop(state, manifest.Version, by, "backup", $"The settings could not be backed up: {e.Message}");
        }

        var now = clock.GetUtcNow();
        state.Pending = new PendingSwitch
        {
            Kind = "update", From = version, To = manifest.Version, Package = package, PreviousPackage = current,
            By = by, StartedAt = now, SwitchedAt = now,
        };
        Step(state, "switch", "done", $"Restarting with {manifest.Version}");
        WriteName(places, package);
        log.LogInformation("Update: switching from {From} to {To} for {By}; this process ends now and App Service starts the new package.", version, manifest.Version, by);
        EndSoon();
        return new UpdateOutcome(true, $"Switching to {manifest.Version}. Hive BDC restarts now, which takes 2 to 4 minutes.");
    }

    /// <summary>Reads the feed and keeps only an entry Balsoft signed; the result goes into the state as the last check.</summary>
    private async Task<(UpdateManifest? Manifest, UpdateOutcome Outcome)> Fetch(UpdateState state, CancellationToken ct)
    {
        string? problem = null;
        UpdateManifest? manifest = null;
        bool keepKnown = false;
        if (key is null) problem = NoKey;
        else
        {
            try { manifest = await feed.Latest(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                problem = $"Could not look for updates: {e.Message}";
                keepKnown = true;
            }
            if (manifest is not null
                && !UpdateSignature.Verify(new MemoryStream(Encoding.UTF8.GetBytes(manifest.Statement())), manifest.EntrySignature, key))
            {
                manifest = null;
                problem = "The feed entry is not signed by Balsoft, so Hive BDC ignores it.";
            }
        }
        if (manifest is not null) manifest = manifest with { Notes = manifest.Notes.Take(MaxNotes).Select(n => n.Length > MaxNoteLength ? n[..MaxNoteLength] : n).ToList() };

        state.LastCheck = new LastCheck
        {
            At = clock.GetUtcNow(),
            Ok = problem is null,
            Message = problem ?? (IsNewer(manifest!.Version, version) ? $"Hive BDC {manifest.Version} is ready." : $"{version} is the newest version."),
            Available = manifest ?? (keepKnown ? state.LastCheck?.Available : null),
        };
        Save(places, state);
        return (manifest, new UpdateOutcome(problem is null, state.LastCheck.Message));
    }

    private void Finish(UpdateState state, PendingSwitch pending, IReadOnlyList<UpdateHealthLine> lines, DateTimeOffset now)
    {
        state.Installed = new InstalledVersion { Version = version, Since = now, By = pending.By };
        state.Previous = pending.Kind == "update" && IsPackageName(pending.PreviousPackage)
            ? new PreviousVersion { Version = pending.From, Package = pending.PreviousPackage } : null;
        state.Pending = null;
        state.LastCheck = null;
        state.Checks = lines.Select(l => new UpdateStep { Name = l.Name, State = "done", At = now, Detail = l.Detail }).ToList();
        Step(state, "running", "done", $"Running {version}");
        History(state, version, now, pending.By, pending.Kind == "update" ? "updated" : "back",
            pending.Kind == "update" ? $"Updated from {pending.From}." : $"Returned from {pending.From}.");
        Save(places, state);

        // Never the package this process runs from, nor the one named for the next start.
        var keep = new HashSet<string>(StringComparer.Ordinal) { pending.Package, ReadName(places) };
        if (state.Previous is { } previous) keep.Add(previous.Package);
        foreach (var file in Directory.GetFiles(places.SitePackages))
        {
            string name = Path.GetFileName(file);
            if ((name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) && !keep.Contains(name))
                TryDelete(file);
        }
        log.LogInformation("Update: {Version} runs and is healthy; packages kept: {Kept}.", version, string.Join(", ", keep));
    }

    private UpdateOutcome Stop(UpdateState state, string target, string by, string step, string message)
    {
        Step(state, step, "failed", message);
        History(state, target, clock.GetUtcNow(), by, "stopped", message);
        Save(places, state);
        log.LogWarning("Update to {Version} stopped at {Step}: {Message} Nothing changed.", target, step, message);
        return new UpdateOutcome(false, message);
    }

    private void Step(UpdateState state, string name, string status, string detail)
    {
        state.Steps.RemoveAll(s => s.Name == name);
        state.Steps.Add(new UpdateStep { Name = name, State = status, At = clock.GetUtcNow(), Detail = detail });
        Save(places, state);
    }

    private static void History(UpdateState state, string version, DateTimeOffset at, string by, string status, string note)
    {
        state.History.Insert(0, new UpdateHistoryEntry { Version = version, At = at, By = by, State = status, Note = note });
        if (state.History.Count > 30) state.History.RemoveRange(30, state.History.Count - 30);
    }

    private string? PackageProblem(string zipFile, string expected)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            var entry = zip.GetEntry(PackageInfoName);
            if (entry is null) return "The package names no version.";
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            string Text(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (Text("version") != expected) return $"The package says {Text("version")}, the feed {expected}.";
            if (Text("runtime") != CurrentRuntime) return $"The package needs {Text("runtime")}; this instance runs {CurrentRuntime}.";
            if (!string.Equals(Text("keyFingerprint"), _keyFingerprint, StringComparison.OrdinalIgnoreCase))
                return "The package carries another update key; an instance updated to it could not check the next update.";
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            return $"The package cannot be read: {e.Message}";
        }
    }

    private static UpdateManifest Public(UpdateManifest manifest) => manifest with { Url = "", Signature = "", EntrySignature = "" };

    public static bool IsNewer(string offered, string installed)
        => System.Version.TryParse(Core(offered), out var a) && System.Version.TryParse(Core(installed), out var b) && a > b;

    private static string Core(string text) => text.Split('-', '+')[0].Trim();

    private static bool IsPackageName(string name)
        => name.Length > 0 && Path.GetFileName(name) == name && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string Span(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} minutes" : $"{span.TotalSeconds:0.##} seconds";

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone);

    private void EndSoon()
    {
        if (_exitDelay <= TimeSpan.Zero) { endProcess(); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(_exitDelay);
            endProcess();
        });
    }

    private static string ReadName(UpdatePlaces places) => File.Exists(places.NameFile) ? File.ReadAllText(places.NameFile).Trim() : "";

    private static void WriteName(UpdatePlaces places, string package)
    {
        if (!IsPackageName(package)) return;
        string temp = places.NameFile + ".new";
        File.WriteAllText(temp, package);
        File.Move(temp, places.NameFile, overwrite: true);
    }

    private static UpdateState Load(UpdatePlaces places)
    {
        try
        {
            return File.Exists(places.StateFile)
                ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(places.StateFile), Json) ?? new UpdateState()
                : new UpdateState();
        }
        catch (JsonException)
        {
            return new UpdateState();
        }
    }

    private static void Save(UpdatePlaces places, UpdateState state)
    {
        Directory.CreateDirectory(places.Work);
        string temp = places.StateFile + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, places.StateFile, overwrite: true);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Azure only: settles a switch after the start, then looks for updates and runs a planned one every minute.</summary>
public sealed class UpdateRunner(SelfUpdate update, Func<CancellationToken, Task<IReadOnlyList<UpdateHealthLine>>> health, ILogger log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            try { await update.Settle(health, stoppingToken); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "Update: settling the start failed."); }
            while (!stoppingToken.IsCancellationRequested)
            {
                // A feed timeout throws an OperationCanceledException too; only the service's own stop ends this loop.
                try { await update.Tick(stoppingToken); }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "Update: the check for updates failed."); }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>What a new version checks before it calls an update done (board P5), and how it ends itself.</summary>
public static class UpdateHealth
{
    /// <summary>App Service starts the package packagename.txt names when the process ends (proven in part 15b-0).</summary>
    public static void EndProcess() => Environment.Exit(1);

    public static IReadOnlyList<UpdateHealthLine> Lines(BDCOptions opts, ISettingsDocument settings, ILicenseStore licences, ILicenseTenant tenant, IStateStore state)
    {
        var lines = new List<UpdateHealthLine>();
        try
        {
            settings.ReadSection();
            lines.Add(new("Settings read", true, $"{opts.DocumentTypes.Count} document types, {opts.CompanyIds().Count} companies"));
        }
        catch (Exception e) { lines.Add(new("Settings read", false, $"The settings cannot be read: {e.Message}")); }

        var licence = licences.Check(opts, tenant.For(opts), DateOnly.FromDateTime(DateTime.Today));
        lines.Add(new("Licence", !licence.Blocked, licence.Blocked ? $"The licence does not allow processing: {licence.Message}"
            : licence.ExpiresOn is { } until ? $"Valid until {until:dd.MM.yyyy}" : "Valid"));

        try
        {
            state.Metrics(0);
            lines.Add(new("Document history", true, "Table Storage answers"));
        }
        catch (Exception e) { lines.Add(new("Document history", false, $"Table Storage does not answer: {e.Message}")); }
        return lines;
    }
}