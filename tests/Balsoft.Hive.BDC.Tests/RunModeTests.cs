using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure cases 17, 18 and 37 of the product design: what the Azure run mode refuses and when it waits.</summary>
public sealed class RunModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-runmode-" + Guid.NewGuid());

    public RunModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static DocumentRoute BlobRoute(string area = "1000") => new()
    {
        DataAreaId = area,
        Input = "/tmp/hivebdc/in",
        Output = "/tmp/hivebdc/out",
        Source = new SourceOptions { Kind = "blob", Connection = "fo-storage" },
        Targets = { new TargetOptions { Kind = "docuware", Connection = "dw" } },
    };

    private static BDCOptions Azure(params DocumentRoute[] routes) => new()
    {
        DocumentTypes = { new DocumentTypeOptions { Id = "SalesInvoice", Mail = false, Companies = routes.ToList() } },
    };

    [Fact]
    public void ABlobSourcedCompanyThatDeliversToDocuWareIsUsableInAzure()
        => Assert.Empty(AzureSettingsCheck.Problems(Azure(BlobRoute())));

    [Fact]
    public void ACompanyThatReadsAFolderIsNamedWithItsTypeAndCompany()
    {
        var route = BlobRoute("2000");
        route.Source = null;

        var problem = Assert.Single(AzureSettingsCheck.Problems(Azure(route)));
        Assert.Contains("Outbound SalesInvoice for company 2000 reads the folder '/tmp/hivebdc/in'", problem);
    }

    [Fact]
    public void WordInputIsRefusedInAzure()
    {
        var route = BlobRoute();
        route.InputFormat = InputFormats.Word;

        Assert.Contains("reads Word files from a folder", Assert.Single(AzureSettingsCheck.Problems(Azure(route))));
    }

    [Fact]
    public void ACompanyThatOnlyWritesToItsOutputFolderIsRefused()
    {
        var route = BlobRoute();
        route.Targets.Clear();

        Assert.Contains("only writes to the folder '/tmp/hivebdc/out'", Assert.Single(AzureSettingsCheck.Problems(Azure(route))));

        var mailed = Azure(route);
        mailed.DocumentTypes[0].Mail = true;
        mailed.Mail.Enabled = true;
        Assert.Empty(AzureSettingsCheck.Problems(mailed));
    }

    [Fact]
    public void AnInboundFlowWritingAFolderIsNamed()
    {
        var opts = Azure(BlobRoute());
        opts.Flows.Add(new FlowOptions
        {
            Id = "orders", Name = "Purchase orders",
            Source = new SourceOptions { Kind = "docuware", Connection = "dw" },
            Targets = { new TargetOptions { Kind = "folder", Settings = { ["Folder"] = @"\\erp\import" } } },
        });

        Assert.Contains(@"Inbound flow 'Purchase orders' writes to the folder '\\erp\import'", Assert.Single(AzureSettingsCheck.Problems(opts)));
    }

    [Fact]
    public void SecretsMustBeKeyVaultReferencesInAzure()
    {
        var opts = Azure(BlobRoute());
        opts.SharePointAuth.ClientSecret = "enc:AQAAANCMnd8BFdERjHoAwE";
        opts.Mail.ClientSecret = "plain-secret-value";
        opts.Connections.Add(new ConnectionOptions { Id = "dw", Kind = "docuware", Secret = "kv:connection-dw" });

        var problems = AzureSettingsCheck.Problems(opts);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("the SharePoint app"));
        Assert.Contains(problems, p => p.Contains("the mail app"));
        Assert.DoesNotContain(problems, p => p.Contains("plain-secret-value") || p.Contains("AQAAANCMnd8BFdERjHoAwE"));
    }

    [Fact]
    public void SwitchedOffTypesCompaniesAndFlowsAreNotChecked()
    {
        var off = BlobRoute("3000");
        off.Source = null;
        off.Enabled = false;
        var opts = Azure(BlobRoute(), off);
        opts.DocumentTypes.Add(new DocumentTypeOptions { Id = "Packing", Enabled = false, Companies = { new DocumentRoute { DataAreaId = "1000", Input = "/x" } } });
        opts.Flows.Add(new FlowOptions { Id = "old", Enabled = false, Targets = { new TargetOptions { Kind = "folder" } } });

        Assert.Empty(AzureSettingsCheck.Problems(opts));
    }

    [Fact]
    public void AWindowsPathIsRefusedOnLinuxAndMac()
    {
        var route = BlobRoute();
        route.Input = @"C:\Export\in";
        var problems = AzureSettingsCheck.Problems(Azure(route));
        if (OperatingSystem.IsWindows()) Assert.Empty(problems);
        else Assert.Contains(@"uses a Windows path ('C:\Export\in')", Assert.Single(problems));
    }

    private sealed class Vault(bool down) : ISecretVault
    {
        public bool Down { get; set; } = down;

        public int Reads { get; private set; }

        public string? Get(string name)
        {
            Reads++;
            return Down ? throw new InvalidOperationException("vault 503") : null;
        }

        public void Set(string name, string value) { }
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void TheGateWaitsForTableStorageThenForKeyVaultAndOpensWhenBothAnswer()
    {
        var rows = new MemoryTableRows();
        var vault = new Vault(down: true);
        var gate = new AzureProcessingGate(new TableStateStore(rows, NullLogger.Instance), new SecretResolver(vault));
        var opts = Azure(BlobRoute());

        rows.FailOn = (_, _) => true;
        Assert.StartsWith("Table Storage cannot be reached: ", gate.Hold(opts));

        rows.FailOn = null;
        Assert.Equal("Key Vault cannot be reached: vault 503", gate.Hold(opts));

        var open = new AzureProcessingGate(new TableStateStore(rows, NullLogger.Instance), new SecretResolver(new Vault(down: false)));
        Assert.Null(open.Hold(opts));
        Assert.Null(new AzureProcessingGate(new TableStateStore(rows, NullLogger.Instance), new SecretResolver(null)).Hold(opts));
    }

    [Fact]
    public void TheGateNamesLocalOnlySettingsBeforeAnythingElse()
    {
        var route = BlobRoute();
        route.Source = null;
        var gate = new AzureProcessingGate(new TableStateStore(new MemoryTableRows(), NullLogger.Instance), new SecretResolver(null));

        Assert.StartsWith("the settings use what only a local install has: Outbound SalesInvoice for company 1000", gate.Hold(Azure(route)));
    }

    [Fact]
    public void KeyVaultIsAskedAtMostOnceAMinute()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var vault = new Vault(down: true);
        var resolver = new SecretResolver(vault, clock);

        Assert.False(resolver.VaultReachable(out var first));
        vault.Down = false;
        clock.Now = clock.Now.AddSeconds(30);
        Assert.False(resolver.VaultReachable(out var cached));
        Assert.Equal(first, cached);
        Assert.Equal(1, vault.Reads);

        clock.Now = clock.Now.AddSeconds(31);
        Assert.True(resolver.VaultReachable(out _));
        Assert.Equal(2, vault.Reads);
    }

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;

        public BDCOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    private sealed class Lines : ILogger<Worker>
    {
        public List<string> All { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (All) All.Add(formatter(state, exception));
        }

        public bool Has(string text) { lock (All) return All.Any(l => l.Contains(text)); }
    }

    private sealed class SwitchGate : IProcessingGate
    {
        public string? Reason { get; set; } = "Table Storage cannot be reached: test";

        public int Calls { get; private set; }

        public string? Hold(BDCOptions opts)
        {
            Calls++;
            return Reason;
        }
    }

    [Fact]
    public async Task TheWorkerTakesNothingWhileTheGateHoldsAndSaysWhenItGoesOn()
    {
        var opts = Azure(BlobRoute());
        opts.PollSeconds = 1;
        var lines = new Lines();
        var gate = new SwitchGate();
        string settings = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(settings, "{}");
        var worker = new Worker(new Monitor(opts), lines, new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            new DocumentTypeRegistry(settings, NullLogger.Instance), gate);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => gate.Calls >= 2);
            Assert.True(lines.Has("cycle: no document taken, Table Storage cannot be reached: test"));
            Assert.Equal(1, lines.All.Count(l => l.StartsWith("cycle: no document taken")));
            Assert.False(lines.Has("license"));

            gate.Reason = null;
            await Until(() => lines.Has("cycle: taking documents again"));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.False(lines.Has("scan cycle failed"));
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met within 15 s");
            await Task.Delay(100);
        }
    }
}