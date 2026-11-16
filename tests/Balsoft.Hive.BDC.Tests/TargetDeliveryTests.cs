using System.Text.Json;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class TargetDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-targets-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public TargetDeliveryTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A target that keeps what it was given; "archive" creates a document per call unless it gets its earlier id.</summary>
    private sealed class FakeTarget(string kind, bool replaces) : IDocumentTarget
    {
        public List<DeliveryContext> Calls { get; } = new();
        public string? UnavailableReason { get; set; }
        public bool Fail { get; set; }
        public int Created { get; private set; }

        public string Kind => kind;
        public bool ReplacesOnRepeat => replaces;
        public string? Unavailable(TargetOptions target) => UnavailableReason;

        public DeliveryResult Deliver(DeliveryContext ctx)
        {
            if (Fail) throw new InvalidOperationException(kind + " is down");
            Calls.Add(ctx);
            if (ctx.PreviousRemoteId is not null) return new DeliveryResult(kind + "://store", ctx.PreviousRemoteId);
            Created++;
            return new DeliveryResult(kind + "://store", "doc-" + Created);
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
            Id = "PurchaseOrder", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { route },
        });
        options.Normalize();
        return options;
    }

    private static string Pair(DocumentRoute route, string id)
    {
        string path = Path.Combine(route.Input, id + "_1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { documentType = "PurchaseOrder", dataAreaId = "A", documentNumber = "PO-1" }));
        File.WriteAllText(Path.ChangeExtension(path, ".pdf"), "Synthetic pass-through fixture");
        return path;
    }

    private DocumentProcessor.ScanResult Scan(BDCOptions options, ConnectorRegistry connectors)
        => new DocumentProcessor(NullLogger.Instance,
            new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger, connectors)
            .ScanOnce(options, License, null, null);

    private static ConnectorRegistry Registry(params FakeTarget[] targets)
    {
        var registry = ConnectorRegistry.BuiltIn();
        foreach (var t in targets) registry.RegisterTarget(t.Kind, _ => t);
        return registry;
    }

    private static TargetOptions Target(string kind, string cabinet = "one") => new()
    {
        Kind = kind,
        Settings = { ["Cabinet"] = cabinet },
        Fields = { ["NUMBER"] = "{documentNumber}" },
    };

    [Fact]
    public void EveryEnabledTargetOfTheEntryGetsTheDocument()
    {
        var archive = new FakeTarget("archive", replaces: false);
        var share = new FakeTarget("share", replaces: true);
        var off = Target("share", "off");
        off.Enabled = false;
        var route = Route(Target("archive"), Target("share"), off);
        Pair(route, "order");

        Assert.Equal(1, Scan(Options(route), Registry(archive, share)).Processed);

        Assert.Single(archive.Calls);
        Assert.Single(share.Calls);
        Assert.Equal("PO-1.pdf", archive.Calls[0].Output.Name);
        Assert.Equal("{documentNumber}", archive.Calls[0].Target.Fields["NUMBER"]);
    }

    [Fact]
    public void TwoTargetsOfTheSameKindAreTwoDeliveries()
    {
        var archive = new FakeTarget("archive", replaces: false);
        var route = Route(Target("archive", "one"), Target("archive", "two"));
        Pair(route, "order");

        Scan(Options(route), Registry(archive));

        Assert.Equal(new[] { "one", "two" }, archive.Calls.Select(c => c.Target.Setting("Cabinet")));
        Assert.Equal(2, archive.Created);
    }

    [Fact]
    public void RetryAfterAFailedTargetUpdatesTheEarlierDeliveryAndCreatesNoSecondDocument()
    {
        var archive = new FakeTarget("archive", replaces: false);
        var share = new FakeTarget("share", replaces: true) { Fail = true };
        var route = Route(Target("archive"), Target("share"));
        string json = Pair(route, "order");
        var options = Options(route);
        var registry = Registry(archive, share);

        var first = Scan(options, registry);
        Assert.Equal(1, first.Errors);
        Assert.True(File.Exists(Path.Combine(route.Input, "error", Path.GetFileName(json))));

        share.Fail = false;
        Pair(route, "order");
        Assert.Equal(1, Scan(options, registry).Processed);

        Assert.Equal(2, archive.Calls.Count);
        Assert.Null(archive.Calls[0].PreviousRemoteId);
        Assert.Equal("doc-1", archive.Calls[1].PreviousRemoteId);
        Assert.Equal(1, archive.Created);
        Assert.Null(share.Calls[0].PreviousRemoteId);
    }

    [Fact]
    public void UnknownTargetKindHoldsThatEntryOnly()
    {
        var route = Route(Target("from-a-newer-version"));
        string json = Pair(route, "order");

        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var plain = new DocumentRoute { DataAreaId = "B", Input = other, Output = Path.Combine(other, "out") };
        File.WriteAllText(Path.Combine(other, "second_1.json"), JsonSerializer.Serialize(new { documentType = "PurchaseOrder", dataAreaId = "B" }));
        File.WriteAllText(Path.Combine(other, "second_1.pdf"), "Synthetic pass-through fixture");

        var options = Options(route);
        options.DocumentTypes[0].Companies.Add(plain);
        options.Normalize();

        var result = Scan(options, ConnectorRegistry.BuiltIn());

        Assert.Equal(1, result.Held);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Errors);
        Assert.True(File.Exists(json));
        Assert.False(Directory.Exists(Path.Combine(route.Input, "out")));
    }

    [Fact]
    public void UnavailableTargetHoldsTheDocumentBeforeAnythingIsProduced()
    {
        var archive = new FakeTarget("archive", replaces: false) { UnavailableReason = "Archive sign-in failed." };
        var share = new FakeTarget("share", replaces: true);
        var route = Route(Target("share"), Target("archive"));
        string json = Pair(route, "order");

        var result = Scan(Options(route), Registry(archive, share));

        Assert.Equal(1, result.Held);
        Assert.True(File.Exists(json));
        Assert.Empty(share.Calls);
        Assert.False(Directory.Exists(Path.Combine(route.Input, "out")));
    }

    [Fact]
    public void SharePointBlockComesFirstAndCountsAsASharePointTarget()
    {
        var route = Route(Target("archive"));
        route.SharePoint = new SharePointTargetOptions
        {
            Enabled = true, SiteUrl = "https://tenant.example/sites/Docs/", Library = "/Invoices/", Folder = "2026",
            Fields = { ["Title"] = "{DocumentNumber}" },
        };
        route.Normalize();

        var targets = route.EffectiveTargets().ToList();

        Assert.Equal(new[] { "sharepoint", "archive" }, targets.Select(t => t.Kind));
        var sp = targets[0].ToSharePoint().ResolveTarget(out _);
        Assert.Equal(new SharePointTarget("https://tenant.example/sites/Docs", "Invoices", "2026"), sp);
        Assert.Equal("{DocumentNumber}", targets[0].Fields["Title"]);
        Assert.True(route.DeliversTo("sharepoint"));
    }

    [Fact]
    public void SharePointEnabledButNotConnectedStillHoldsWithTheSameReason()
    {
        var target = new SharePointDocumentTarget(null);
        Assert.Equal("SharePoint is enabled but its connection is unavailable.", target.Unavailable(new TargetOptions()));

        var mail = new MailDocumentTarget(new MailOptions { Enabled = true }, null);
        Assert.Equal("Mail is enabled but its connection is unavailable.", mail.Unavailable(new TargetOptions()));
        Assert.Null(new MailDocumentTarget(new MailOptions { Enabled = false }, null).Unavailable(new TargetOptions()));
    }

    [Fact]
    public void ARenamedFileCabinetIsStillTheSameDestination()
    {
        var before = new TargetOptions { Kind = "docuware", Connection = "dms", Settings = { ["FileCabinet"] = "c-1", ["FileCabinetName"] = "Invoices" } };
        var after = new TargetOptions { Kind = "docuware", Connection = "dms", Settings = { ["FileCabinet"] = "c-1", ["FileCabinetName"] = "Outgoing invoices" } };
        var other = new TargetOptions { Kind = "docuware", Connection = "dms", Settings = { ["FileCabinet"] = "c-2", ["FileCabinetName"] = "Invoices" } };

        Assert.Equal(before.Key, after.Key);
        Assert.NotEqual(before.Key, other.Key);
    }

    [Fact]
    public void ASecondSharePointLibraryAsAFurtherTargetNeedsTheUploader()
    {
        var second = new TargetOptions
        {
            Kind = "SharePoint",
            Settings = { ["SiteUrl"] = "https://tenant.example/sites/Archive", ["Library"] = "Orders" },
        };
        var options = Options(Route(second));

        Assert.True(options.AnySharePointEnabled());
        Assert.Equal(1, options.SharePointEntryCount());
    }
}