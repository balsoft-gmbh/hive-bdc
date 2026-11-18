using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Balsoft.Hive.BDC.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure cases 82 to 94 of the product design (part 15b) and the review findings: an Azure instance updating itself.</summary>
public sealed class SelfUpdateTests : IDisposable
{
    private const string Installed = "20261118155322.zip";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-update-" + Guid.NewGuid());
    private readonly RSA _signer = RSA.Create(2048);
    private readonly FakeFeed _feed = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 18, 15, 30, 0, TimeSpan.Zero));
    private int _ended;

    public SelfUpdateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "SitePackages"));
        File.WriteAllText(Path.Combine(_root, "SitePackages", "packagename.txt"), Installed);
        File.WriteAllBytes(Path.Combine(_root, "SitePackages", Installed), Package("1.8.0"));
    }

    public void Dispose()
    {
        _feed.Release();
        _signer.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    }

    private sealed class FakeFeed : IUpdateFeed
    {
        private TaskCompletionSource? _held;
        public UpdateManifest? Manifest { get; set; }
        public byte[] Zip { get; set; } = [];
        public bool Down { get; set; }
        public bool BreakDownload { get; set; }
        public Exception? LatestError { get; set; }
        public Exception? OpenError { get; set; }
        public Stream? Body { get; set; }
        public int Asked { get; private set; }
        public int Opened { get; private set; }

        public void Hold() => _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _held?.TrySetResult();

        public Task<UpdateManifest> Latest(CancellationToken ct)
        {
            Asked++;
            if (LatestError is not null) throw LatestError;
            return Down ? throw new HttpRequestException("No such host is known.") : Task.FromResult(Manifest!);
        }

        public async Task<Stream> Open(string url, CancellationToken ct)
        {
            Opened++;
            if (_held is not null) await _held.Task;
            if (OpenError is not null) throw OpenError;
            return Body ?? (BreakDownload ? new BreakingStream(Zip) : new MemoryStream(Zip));
        }
    }

    /// <summary>Gives the first half of the bytes, then fails as a dropped connection does.</summary>
    private sealed class BreakingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => Position >= Length / 2 ? throw new IOException("The response ended prematurely.") : base.Read(buffer, offset, (int)Math.Min(count, Length / 2 - Position));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= Length / 2) throw new IOException("The response ended prematurely.");
            int n = (int)Math.Min(buffer.Length, Length / 2 - Position);
            return base.ReadAsync(buffer[..n], ct);
        }
    }

    /// <summary>A feed that sends one byte now and then.</summary>
    private sealed class TricklingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(1000, ct);
            buffer.Span[0] = 1;
            return 1;
        }
    }

    private static string Fingerprint(RSA key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private byte[] Package(string version, string? runtime = null, string? fingerprint = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry(SelfUpdate.PackageInfoName).Open()))
                w.Write(JsonSerializer.Serialize(new { version, runtime = runtime ?? SelfUpdate.CurrentRuntime, keyFingerprint = fingerprint ?? Fingerprint(_signer) }));
            using var dll = zip.CreateEntry("Balsoft.Hive.BDC.dll").Open();
            dll.Write(RandomNumberGenerator.GetBytes(5000));
        }
        return buffer.ToArray();
    }

    private UpdateManifest Signed(UpdateManifest manifest, RSA? signer = null)
        => manifest with { EntrySignature = Convert.ToBase64String((signer ?? _signer).SignData(Encoding.UTF8.GetBytes(manifest.Statement()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };

    private UpdateManifest Offer(string version, byte[]? zip = null, RSA? signer = null)
    {
        zip ??= Package(version);
        _feed.Zip = zip;
        _feed.Manifest = Signed(new UpdateManifest
        {
            Version = version,
            Url = $"https://feed.example/azure/hivebdc-app-{version}.zip",
            Size = zip.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(),
            Signature = Convert.ToBase64String((signer ?? _signer).SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            Runtime = SelfUpdate.CurrentRuntime,
            Notes = ["DocuWare date fields take German dates."],
            ReleaseDate = "2026-11-17",
        });
        return _feed.Manifest;
    }

    private RSA PublicKey()
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(_signer.ExportParameters(false));
        return rsa;
    }

    private SelfUpdate Make(string version = "1.8.0", bool withKey = true, string? settings = """{"PollSeconds":30}""", TimeSpan? downloadLimit = null)
        => new(new UpdatePlaces(_root), _feed, version, withKey ? PublicKey() : null, () => settings, () => _ended++, _clock,
            NullLogger.Instance, exitDelay: TimeSpan.Zero, wait: (span, _) => { _clock.Now += span; return Task.CompletedTask; }, downloadLimit: downloadLimit);

    private string NameFile => File.ReadAllText(Path.Combine(_root, "SitePackages", "packagename.txt"));
    private string[] Packages => Directory.GetFiles(Path.Combine(_root, "SitePackages")).Select(Path.GetFileName).Where(n => n != "packagename.txt").Order().ToArray()!;
    private static Task<IReadOnlyList<UpdateHealthLine>> Healthy(CancellationToken _) => Task.FromResult<IReadOnlyList<UpdateHealthLine>>([new("Settings read", true, "")]);
    private static Task<IReadOnlyList<UpdateHealthLine>> Sick(CancellationToken _) => Task.FromResult<IReadOnlyList<UpdateHealthLine>>([new("Licence", false, "The licence is not valid.")]);

    [Fact]
    public async Task TheNewerVersionIsCheckedBackedUpAndSwitchedTo()
    {
        Offer("1.8.1");
        var update = Make();
        var outcome = await update.Run("Anna", CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("hivebdc-1.8.1.zip", NameFile);
        Assert.Contains("hivebdc-1.8.1.zip", Packages);
        Assert.Equal(1, _ended);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, "hivebdc", "updates", "backups")));
        Assert.StartsWith("settings-1.8.0-20261118-", Path.GetFileName(backup));
        Assert.Equal("""{"PollSeconds":30}""", File.ReadAllText(backup));
        var view = update.Describe();
        Assert.Equal("1.8.1", view.Pending!.To);
        Assert.Equal(Installed, view.Pending.PreviousPackage);
        Assert.Equal(["download", "signature", "backup", "switch"], view.Steps.Select(s => s.Name));
        Assert.All(view.Steps, s => Assert.Equal("done", s.State));
    }

    [Fact]
    public async Task ADownloadThatBreaksOffLeavesNothingBehind()
    {
        Offer("1.8.1");
        _feed.BreakDownload = true;
        var update = Make();
        var outcome = await update.Run("Anna", CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("download", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Installed, NameFile);
        Assert.Equal([Installed], Packages);
        Assert.Equal(0, _ended);
        Assert.Null(update.Describe().Pending);
        Assert.Equal("stopped", update.Describe().History[0].State);
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("signature")]
    [InlineData("size")]
    public async Task AFileThatIsNotTheOneBalsoftSignedIsNotUsed(string broken)
    {
        var manifest = Offer("1.8.1", signer: broken == "signature" ? RSA.Create(2048) : null);
        if (broken == "sha256") _feed.Manifest = Signed(manifest with { Sha256 = new string('0', 64) });
        if (broken == "size") _feed.Manifest = Signed(manifest with { Size = manifest.Size + 1 });
        var update = Make();
        var outcome = await update.Run("Anna", CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(Installed, NameFile);
        Assert.Equal([Installed], Packages);
        Assert.Equal(0, _ended);
        var stopped = update.Describe().Steps.Last();
        Assert.Equal("failed", stopped.State);
        Assert.Equal(broken == "size" ? "download" : "signature", stopped.Name);
    }

    [Fact]
    public async Task AFeedEntryNotSignedByBalsoftIsNeitherShownNorUsed()
    {
        Offer("1.8.1");
        _feed.Manifest = Signed(_feed.Manifest!, RSA.Create(2048));
        var update = Make();

        var check = await update.Check(CancellationToken.None);
        Assert.False(check.Ok);
        Assert.Contains("not signed by Balsoft", check.Message);
        Assert.Null(update.Describe().Available);

        _feed.Manifest = _feed.Manifest with { Notes = ["Call +49 000 for a free update."] };
        var run = await update.Run("Anna", CancellationToken.None);
        Assert.False(run.Ok);
        Assert.Equal(0, _feed.Opened);
        Assert.Equal(Installed, NameFile);
    }

    [Fact]
    public async Task TheViewHasNoPackageAddressNoSignatureAndShortNotes()
    {
        var manifest = Offer("1.8.1");
        _feed.Manifest = Signed(manifest with { Url = manifest.Url + "?sv=2024&sig=secret", Notes = Enumerable.Repeat(new string('x', 1000), 50).ToList() });
        var update = Make();
        await update.Check(CancellationToken.None);

        var view = update.Describe();
        Assert.Equal("", view.Available!.Url);
        Assert.Equal("", view.Available.Signature);
        Assert.Equal("", view.Available.EntrySignature);
        Assert.Equal("", view.LastCheck!.Available!.Url);
        Assert.Equal(10, view.Available.Notes.Count);
        Assert.All(view.Available.Notes, n => Assert.True(n.Length <= 300));
        Assert.DoesNotContain("sig=secret", JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task ABuildWithoutAnUpdateKeyDoesNotUpdateItself()
    {
        Offer("1.8.1");
        var outcome = await Make(withKey: false).Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("signature", outcome.Message);
        Assert.Equal(Installed, NameFile);
        Assert.False(Make(withKey: false).Describe().CanUpdate);
    }

    [Theory]
    [InlineData("1.8.0")]
    [InlineData("1.7.9")]
    public async Task AVersionThatIsNotNewerIsRefused(string offered)
    {
        Offer(offered);
        var outcome = await Make().Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("newest", outcome.Message);
        Assert.Equal(Installed, NameFile);
    }

    [Theory]
    [InlineData("1.8.2", null, null)]
    [InlineData("1.8.1", "DOTNETCORE|9.0", null)]
    [InlineData("1.8.1", null, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task APackageThatSaysOtherwiseThanTheFeedIsRefused(string packageVersion, string? runtime, string? fingerprint)
    {
        Offer("1.8.1", zip: Package(packageVersion, runtime, fingerprint));
        var outcome = await Make().Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains(fingerprint is not null ? "another update key" : runtime is null ? "1.8.2" : "DOTNETCORE|9.0", outcome.Message);
        Assert.Equal(Installed, NameFile);
        Assert.Equal([Installed], Packages);
    }

    [Theory]
    [InlineData("1.8.1-../../../escaped")]
    [InlineData("1.8.1+x/../../escaped")]
    [InlineData("9.9.9/../escaped")]
    [InlineData("1.8")]
    [InlineData("1.8.1\n")]
    public async Task AVersionThatIsNoPlainVersionNumberNeverBecomesAFileName(string version)
    {
        var manifest = Offer("1.8.1");
        _feed.Manifest = Signed(manifest with { Version = version });
        var outcome = await Make().Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("no plain version number", outcome.Message);
        Assert.Equal(0, _feed.Opened);
        Assert.Equal([Installed], Packages);
        Assert.Empty(Directory.GetFiles(_root, "*escaped*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ADownloadLongerThanTheFeedSaysIsCutOff()
    {
        var manifest = Offer("1.8.1");
        _feed.Manifest = Signed(manifest with { Size = 1000 });
        var outcome = await Make().Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("longer than the 1000 bytes the feed names", outcome.Message);
        Assert.Equal([Installed], Packages);
    }

    [Fact]
    public async Task ADownloadThatTakesTooLongStopsAndLeavesNothing()
    {
        Offer("1.8.1");
        _feed.Body = new TricklingStream();
        var outcome = await Make(downloadLimit: TimeSpan.FromMilliseconds(100)).Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("took longer than", outcome.Message);
        Assert.Equal([Installed], Packages);
    }

    [Fact]
    public async Task ATimeoutOfTheFeedIsAStopAndNotTheEndOfTheRunner()
    {
        Offer("1.8.1");
        _feed.OpenError = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var run = await Make().Run("Anna", CancellationToken.None);
        Assert.False(run.Ok);
        Assert.Contains("download", run.Message, StringComparison.OrdinalIgnoreCase);

        _feed.LatestError = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var check = await Make().Check(CancellationToken.None);
        Assert.False(check.Ok);
        Assert.StartsWith("Could not look for updates", check.Message);
    }

    [Fact]
    public async Task AFeedThatNamesAnotherRuntimeIsRefusedBeforeTheDownload()
    {
        var manifest = Offer("1.8.1");
        _feed.Manifest = Signed(manifest with { Runtime = "DOTNETCORE|11.0" });
        var outcome = await Make().Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("DOTNETCORE|11.0", outcome.Message);
        Assert.Equal([Installed], Packages);
    }

    [Fact]
    public async Task AnInstanceThatDoesNotRunFromAKnownPackageIsNotUpdated()
    {
        Offer("1.8.1");
        File.Delete(Path.Combine(_root, "SitePackages", "packagename.txt"));
        var outcome = await Make().Run("Anna", CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Contains("does not run from a package", outcome.Message);
        Assert.Equal(0, _feed.Opened);
    }

    [Fact]
    public async Task OnlyOneUpdateRunsAtATime()
    {
        Offer("1.8.1");
        var update = Make();
        Assert.True((await update.Run("Anna", CancellationToken.None)).Ok);
        var second = await Make().Run("Ben", CancellationToken.None);
        Assert.False(second.Ok);
        Assert.Contains("Anna", second.Message);
        Assert.Contains("running", second.Message);
    }

    [Fact]
    public async Task ReturnPlanAndCheckWaitWhileADownloadRuns()
    {
        Offer("1.8.1");
        var update = Make();
        var tonight = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.FromHours(1));
        Assert.True(update.Schedule("Anna", tonight).Ok);
        _feed.Hold();
        var running = update.Run("Anna", CancellationToken.None);
        try
        {
            Assert.Contains("running", Make().Return("Ben").Message);
            Assert.Contains("running", Make().Schedule("Ben", tonight.AddHours(1)).Message);
            Assert.Contains("running", Make().Cancel("Ben").Message);
            var check = await Make().Check(CancellationToken.None);
            Assert.False(check.Ok);
            Assert.Contains("running", check.Message);

            _clock.Now = tonight.AddMinutes(5);
            await Make().Tick(CancellationToken.None);
            Assert.NotNull(Make().Describe().Schedule);
        }
        finally
        {
            _feed.Release();
        }
        Assert.True((await running).Ok);
        Assert.Equal("hivebdc-1.8.1.zip", NameFile);
    }

    [Fact]
    public async Task AVersionThatDoesNotStartThreeTimesReturnsByItself()
    {
        Offer("1.8.1");
        await Make().Run("Anna", CancellationToken.None);
        var places = new UpdatePlaces(_root);

        Assert.Equal(StartGuard.Continue, SelfUpdate.Guard(places, "1.8.1", _clock));
        Assert.Equal(StartGuard.Continue, SelfUpdate.Guard(places, "1.8.1", _clock));
        Assert.Equal(StartGuard.Exit, SelfUpdate.Guard(places, "1.8.1", _clock));

        Assert.Equal(Installed, NameFile);
        var view = Make().Describe();
        Assert.Null(view.Pending);
        Assert.Equal("returned", view.History[0].State);
        Assert.Contains("by itself", view.History[0].Note);
    }

    [Fact]
    public void ANameFromTheStateThatIsAPathIsNeverWritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, "hivebdc", "updates"));
        File.WriteAllText(Path.Combine(_root, "hivebdc", "updates", "state.json"),
            """{"pending":{"kind":"update","from":"1.8.0","to":"1.8.1","package":"hivebdc-1.8.1.zip","previousPackage":"../../escaped.zip","by":"Anna","starts":2}}""");
        Assert.Equal(StartGuard.Continue, SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.1", _clock));
        Assert.Equal(Installed, NameFile);
        Assert.Contains("no usable previous package", Make().Describe().History[0].Note);
    }

    [Fact]
    public async Task AVersionThatStaysUnhealthyForTenMinutesReturnsByItself()
    {
        Offer("1.8.1");
        await Make().Run("Anna", CancellationToken.None);
        Assert.Equal(StartGuard.Continue, SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.1", _clock));
        _ended = 0;

        var started = _clock.Now;
        await Make("1.8.1").Settle(Sick, CancellationToken.None);

        Assert.True(_clock.Now - started >= TimeSpan.FromMinutes(10));
        Assert.Equal(Installed, NameFile);
        Assert.Equal(1, _ended);
        var view = Make().Describe();
        Assert.Null(view.Pending);
        Assert.Contains("The licence is not valid.", view.History[0].Note);
    }

    [Fact]
    public async Task SettleLeavesASwitchAloneThatSomethingElseEnded()
    {
        Offer("1.8.1");
        await Make().Run("Anna", CancellationToken.None);
        var places = new UpdatePlaces(_root);
        SelfUpdate.Guard(places, "1.8.1", _clock);
        _ended = 0;

        await Make("1.8.1").Settle(_ =>
        {
            SelfUpdate.Guard(places, "1.8.1", _clock);
            SelfUpdate.Guard(places, "1.8.1", _clock);
            return Healthy(CancellationToken.None);
        }, CancellationToken.None);

        Assert.Equal(Installed, NameFile);
        Assert.Equal([Installed, "hivebdc-1.8.1.zip"], Packages);
        Assert.NotEqual("1.8.1", Make().Describe().Installed.Version);
        Assert.Equal(0, _ended);
    }

    [Fact]
    public async Task AHealthyStartFinishesTheUpdateAndKeepsOnlyTheCurrentAndThePreviousPackage()
    {
        File.WriteAllBytes(Path.Combine(_root, "SitePackages", "20261101090000.zip"), [1, 2, 3]);
        Offer("1.8.1");
        await Make().Run("Anna", CancellationToken.None);
        SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.1", _clock);
        _ended = 0;

        await Make("1.8.1").Settle(Healthy, CancellationToken.None);

        var view = Make("1.8.1").Describe();
        Assert.Null(view.Pending);
        Assert.Equal("1.8.1", view.Installed.Version);
        Assert.Equal("Anna", view.Installed.By);
        Assert.Equal("1.8.0", view.Previous!.Version);
        Assert.Equal([Installed, "hivebdc-1.8.1.zip"], Packages);
        Assert.Equal("updated", view.History[0].State);
        Assert.Equal(0, _ended);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "hivebdc", "updates", "backups")));
    }

    [Fact]
    public async Task ReturnGoesBackToThePreviousPackageAndKeepsTheSettings()
    {
        Offer("1.8.1");
        await Make().Run("Anna", CancellationToken.None);
        SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.1", _clock);
        await Make("1.8.1").Settle(Healthy, CancellationToken.None);
        _ended = 0;

        var back = Make("1.8.1").Return("Ben");
        Assert.True(back.Ok, back.Message);
        Assert.Equal(Installed, NameFile);
        Assert.Equal(1, _ended);

        Assert.Equal(StartGuard.Continue, SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.0", _clock));
        await Make("1.8.0").Settle(Healthy, CancellationToken.None);
        var view = Make("1.8.0").Describe();
        Assert.Equal("1.8.0", view.Installed.Version);
        Assert.Null(view.Previous);
        Assert.Equal("back", view.History[0].State);
        Assert.Equal("Returned from 1.8.1.", view.History[0].Note);
        Assert.Equal([Installed], Packages);
    }

    [Fact]
    public async Task AnotherVersionLooksForUpdatesAtOnce()
    {
        Offer("1.8.1");
        var old = Make();
        await old.Check(CancellationToken.None);
        Assert.Equal("Hive BDC 1.8.1 is ready.", old.Describe().LastCheck!.Message);
        await old.Run("Anna", CancellationToken.None);
        SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.1", _clock);
        await Make("1.8.1").Settle(Healthy, CancellationToken.None);
        Assert.Null(Make("1.8.1").Describe().LastCheck);

        int asked = _feed.Asked;
        await Make("1.8.1").Tick(CancellationToken.None);
        Assert.Equal(asked + 1, _feed.Asked);
        Assert.Equal("1.8.1 is the newest version.", Make("1.8.1").Describe().LastCheck!.Message);

        await Make("1.8.5").Settle(Healthy, CancellationToken.None);
        Assert.Null(Make("1.8.5").Describe().LastCheck);
    }

    [Fact]
    public async Task TheVersionADeploymentBringsIsRecordedOnce()
    {
        await Make("1.8.0").Settle(Healthy, CancellationToken.None);
        await Make("1.8.0").Settle(Healthy, CancellationToken.None);
        var view = Make("1.8.0").Describe();
        Assert.Equal("1.8.0", view.Installed.Version);
        Assert.Equal("installation", view.Installed.By);
        var entry = Assert.Single(view.History);
        Assert.Equal("installed", entry.State);

        await Make("1.8.5").Settle(Healthy, CancellationToken.None);
        view = Make("1.8.5").Describe();
        Assert.Equal("deployment", view.Installed.By);
        Assert.Equal(2, view.History.Count);
        Assert.Equal("Installed by a deployment.", view.History[0].Note);
    }

    [Fact]
    public void ReturnWithoutAPreviousVersionIsRefused()
    {
        var back = Make().Return("Ben");
        Assert.False(back.Ok);
        Assert.Equal(Installed, NameFile);
        Assert.Equal(0, _ended);
    }

    [Fact]
    public async Task ASwitchThatDidNotHappenPutsTheNameBack()
    {
        Offer("1.8.1");
        await Make().Run("Anna", CancellationToken.None);

        Assert.Equal(StartGuard.Continue, SelfUpdate.Guard(new UpdatePlaces(_root), "1.8.0", _clock));

        Assert.Equal(Installed, NameFile);
        var view = Make().Describe();
        Assert.Null(view.Pending);
        Assert.Equal("stopped", view.History[0].State);
    }

    [Fact]
    public async Task TheUpdateTonightRunsOnceWhenItIsDueAndCanBeCancelled()
    {
        Offer("1.8.1");
        var update = Make();
        var tonight = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.FromHours(1));
        Assert.True(update.Schedule("Anna", tonight).Ok);

        await update.Tick(CancellationToken.None);
        Assert.Equal(Installed, NameFile);

        _clock.Now = tonight.AddMinutes(1);
        await update.Tick(CancellationToken.None);
        Assert.Equal("hivebdc-1.8.1.zip", NameFile);
        Assert.Equal(1, _ended);
        Assert.Null(update.Describe().Schedule);

        await update.Tick(CancellationToken.None);
        Assert.Equal(1, _ended);
    }

    [Fact]
    public async Task ACancelledUpdateTonightDoesNotRun()
    {
        Offer("1.8.1");
        var update = Make();
        var tonight = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.FromHours(1));
        update.Schedule("Anna", tonight);
        Assert.True(update.Cancel("Anna").Ok);
        _clock.Now = tonight.AddHours(2);
        await update.Tick(CancellationToken.None);
        Assert.Equal(Installed, NameFile);
        Assert.Equal(0, _ended);
    }

    [Fact]
    public async Task AFeedThatCannotBeReachedIsReportedWithoutAnError()
    {
        _feed.Down = true;
        var update = Make();
        var check = await update.Check(CancellationToken.None);
        Assert.False(check.Ok);
        Assert.StartsWith("Could not look for updates", check.Message);
        var view = update.Describe();
        Assert.False(view.LastCheck!.Ok);
        Assert.Null(view.Available);
    }

    [Fact]
    public async Task TheFeedIsAskedOnceADayAfterSixInTheMorning()
    {
        Offer("1.8.1");
        var update = Make();
        _clock.Now = new DateTimeOffset(2026, 11, 18, 6, 30, 0, TimeSpan.FromHours(1));
        await update.Tick(CancellationToken.None);
        Assert.Equal(1, _feed.Asked);
        Assert.Equal("1.8.1", update.Describe().Available!.Version);

        _clock.Now = _clock.Now.AddHours(10);
        await update.Tick(CancellationToken.None);
        Assert.Equal(1, _feed.Asked);

        _clock.Now = new DateTimeOffset(2026, 11, 19, 6, 1, 0, TimeSpan.FromHours(1));
        await update.Tick(CancellationToken.None);
        Assert.Equal(2, _feed.Asked);
    }

    [Theory]
    [InlineData("GET", "/service/updates", ApiRoles.Viewer)]
    [InlineData("POST", "/service/updates/run", ApiRoles.Administrator)]
    [InlineData("POST", "/service/updates/schedule", ApiRoles.Administrator)]
    [InlineData("POST", "/service/updates/return", ApiRoles.Administrator)]
    [InlineData("POST", "/service/updates/check", ApiRoles.Administrator)]
    public void ViewersAndOperatorsSeeUpdatesButOnlyAnAdministratorActs(string method, string path, string needed)
    {
        var call = new ApiCall(method, path, new Dictionary<string, string>(), "");
        Assert.Equal(needed, ApiRoles.Needed(call));
        if (needed == ApiRoles.Administrator)
        {
            Assert.False(ApiRoles.Allows([ApiRoles.Operator], needed));
            Assert.False(ApiRoles.Allows([ApiRoles.Viewer], needed));
        }
    }

    private sealed class Answering(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound, "the feed offers no Azure package yet")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "the feed answered 503")]
    public async Task AFeedWithoutAnAzurePackageSaysSoInWords(System.Net.HttpStatusCode status, string message)
    {
        using var http = new HttpClient(new Answering(status, "<Error/>"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new HttpUpdateFeed(http, "https://feed.example/hive-bdc").Latest(CancellationToken.None));
        Assert.Equal(message, error.Message);

        using var ok = new HttpClient(new Answering(System.Net.HttpStatusCode.OK, """{"version":"1.8.1","size":5}"""));
        Assert.Equal("1.8.1", (await new HttpUpdateFeed(ok, "https://feed.example/hive-bdc").Latest(CancellationToken.None)).Version);
    }

    [Fact]
    public async Task AFeedEntryLargerThan64KilobytesIsRefused()
    {
        string huge = """{"version":"1.8.1","notes":[" """ + new string('x', 70_000) + """ "]}""";
        using var http = new HttpClient(new Answering(System.Net.HttpStatusCode.OK, huge));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new HttpUpdateFeed(http, "https://feed.example/hive-bdc").Latest(CancellationToken.None));
        Assert.Equal("the feed entry is too large", error.Message);
    }

    [Theory]
    [InlineData("https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc", "https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc/azure/latest.json")]
    [InlineData("https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc/", "https://hiveeinvoiceupdates.blob.core.windows.net/hive-bdc/azure/latest.json")]
    [InlineData("https://mirror.example/feed?sv=2024&sig=abc", "https://mirror.example/feed/azure/latest.json?sv=2024&sig=abc")]
    public void TheFeedEntryIsReadBelowTheFeedAddressAndKeepsItsQuery(string feed, string expected)
        => Assert.Equal(expected, HttpUpdateFeed.EntryUrl(feed).AbsoluteUri);

    [Theory]
    [InlineData("https://mirror.example/feed?sv=1&sig=a", "https://mirror.example/feed/azure/a.zip", "https://mirror.example/feed/azure/a.zip?sv=1&sig=a")]
    [InlineData("https://mirror.example/feed?sv=1&sig=a", "https://other.example/a.zip", "https://other.example/a.zip")]
    [InlineData("https://mirror.example/feed", "https://mirror.example/feed/azure/a.zip?x=1", "https://mirror.example/feed/azure/a.zip?x=1")]
    public void ThePackageAddressGetsTheFeedQueryOnlyOnTheSameHost(string feed, string package, string expected)
        => Assert.Equal(expected, HttpUpdateFeed.PackageUrl(feed, package).AbsoluteUri);

    [Fact]
    public void APackageAddressThatIsNotHttpsIsRefused()
        => Assert.Throws<InvalidDataException>(() => HttpUpdateFeed.PackageUrl("https://mirror.example/feed", "http://mirror.example/feed/a.zip"));

    /// <summary>"Hive BDC update key check" signed on 2026-11-18 by KMS key balsoft-update-rsa3072, version 1.</summary>
    private const string KmsCheckSignature =
        "hllojXu6ZHgt1gREXV9upKbFvfX2fwH/xqo/5n6WRCQjIacx+FrR6hVpKi2lVvhsqTELJ3g7F0Nw+sIaG+FbYEDBJq2gEEupFtfD3WUtVEnDDNa1EJiVNYHsRJ3dsz01DiM2ytnYTRibZrlFWLrnLu2vz3Ev8UinbLA9dDOqxwCH9NJOT+x3zhWj8fdJNnS7KXNsktPkigj0o1jphMDUMZbfzXxrMSBQP8oDtyXlYHb8UeALw+/MDV4ivu7xnUQSt1qBhD9bEMJphyF3v3ObaiR+PMCVjVWIfn+FPDd0wPNzSZjQzQqbtLBSiaj+E5wjMU9i+y+1YBJJ/nFskq88NUtBxam8hTV12ezernamTf+U3idLmwg9lzEYwr86qa8MBZk49/17J2f7Cv6Di6sa0InVN8AkJ6UCbRxrtiw7Byv4C4+2FXwA6ltChonKJjg/QWX1QVu9FXtkUbMkUYL7rhgyAvEJwlP5Ktj7LjscTnaq46AKgWOtZLofbCjoXuH7";

    [Fact]
    public void TheKeyBuiltIntoTheAppVerifiesWhatTheKmsUpdateKeySigned()
    {
        using var key = UpdateKey.Load();
        Assert.NotNull(key);
        Assert.Equal(3072, key!.KeySize);
        Assert.True(UpdateSignature.Verify(new MemoryStream(Encoding.UTF8.GetBytes("Hive BDC update key check")), KmsCheckSignature, key));
        Assert.False(UpdateSignature.Verify(new MemoryStream(Encoding.UTF8.GetBytes("Hive BDC update key check!")), KmsCheckSignature, key));
    }

    /// <summary>build/azure-update-statement.ps1 must build the same text: the signing host signs it, the instance checks it.</summary>
    public const string SampleEntry = """{"version":"1.8.1","url":"https://x/a.zip","size":23449190,"sha256":"ABCDEF0123","signature":"","entrySignature":"","runtime":"DOTNETCORE|10.0","notes":["DocuWare-Datumsfelder für 18.11.2026.","Zweite Zeile"],"releaseDate":"2026-11-17"}""";
    public const string SampleStatement = "hive-bdc-azure-update\n1.8.1\n23449190\nabcdef0123\nDOTNETCORE|10.0\n2026-11-17\nDocuWare-Datumsfelder für 18.11.2026.\nZweite Zeile";

    [Fact]
    public void TheSignedTextOfAFeedEntryIsFixed()
        => Assert.Equal(SampleStatement, JsonSerializer.Deserialize<UpdateManifest>(SampleEntry)!.Statement());

    [Fact]
    public void TheFeedSignatureIsCheckedWithTheEmbeddedKeyOnly()
    {
        var zip = Package("1.8.1");
        string good = Convert.ToBase64String(_signer.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var key = PublicKey();
        Assert.True(UpdateSignature.Verify(new MemoryStream(zip), good, key));
        Assert.False(UpdateSignature.Verify(new MemoryStream(zip), "not base64", key));
        zip[^1] ^= 0xFF;
        Assert.False(UpdateSignature.Verify(new MemoryStream(zip), good, key));
        Assert.Equal("hivebdc-package.json", SelfUpdate.PackageInfoName);
    }
}