using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class BlobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-blob-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private readonly MemoryBlobStore _store = new();

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public BlobTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class MemoryBlobStore : IBlobStore
    {
        public Dictionary<string, (byte[] Content, string Type)> Blobs { get; } = new(StringComparer.Ordinal);
        public List<string> Downloads { get; } = new();
        public Uri ContainerUri { get; } = new("https://account.blob.core.windows.net/fo-out");
        public IReadOnlyList<string> List(string prefix) => Blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k).ToList();
        public bool Exists(string name) => Blobs.ContainsKey(name);
        public byte[] Download(string name) { Downloads.Add(name); return Blobs[name].Content; }
        public void Upload(string name, byte[] content, string contentType) => Blobs[name] = (content, contentType);
        public void Move(string name, string newName) { Blobs[newName] = Blobs[name]; Blobs.Remove(name); }
    }

    private DocumentRoute Route(bool enabled = true)
    {
        string input = Path.Combine(_root, "staging");
        return new DocumentRoute
        {
            DataAreaId = "USMF", Input = input, Output = Path.Combine(input, "out"),
            Source = new SourceOptions { Kind = "blob", Connection = "fo-storage", Settings = { ["Container"] = "fo-out", ["Prefix"] = "SalesConfirmation/" } },
        };
    }

    private static BDCOptions Options(DocumentRoute route, bool typeEnabled = true)
    {
        var options = new BDCOptions();
        options.Connections.Add(new ConnectionOptions { Id = "fo-storage", Kind = "azure-storage", Secret = "UseDevelopmentStorage=true" });
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "SalesConfirmation", DisplayName = "Order confirmation", Handler = DocumentHandlerKind.Pdf, Mail = false,
            Enabled = typeEnabled, Companies = { route },
        });
        options.Normalize();
        return options;
    }

    private void FoPair(string id = "5637144576", string? pdfLink = null)
    {
        string link = pdfLink ?? $"https://account.blob.core.windows.net/fo-out/SalesConfirmation/{id}.pdf";
        _store.Upload($"SalesConfirmation/{id}", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            documentType = "SalesConfirmation", dataAreaId = "USMF", documentNumber = "SO-1001", pdfLink = link,
        })), "application/json");
        _store.Upload($"SalesConfirmation/{id}.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 confirmation"), "application/pdf");
    }

    private BlobStager Stager() => new(NullLogger.Instance, _ledger, (_, _) => _store);

    private DocumentProcessor Processor() => new(NullLogger.Instance,
        new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger);

    [Fact]
    public void AnFoPairIsStagedProcessedAndItsBlobsMovedToProcessed()
    {
        var route = Route();
        var options = Options(route);
        FoPair();

        Stager().Run(options);
        Assert.True(File.Exists(Path.Combine(route.Input, "5637144576_blob.json")));
        Assert.Equal("%PDF-1.4 confirmation", File.ReadAllText(Path.Combine(route.Input, "5637144576_blob.pdf")));

        Assert.Equal(1, Processor().ScanOnce(options, License, null, null).Processed);
        Stager().Run(options);

        Assert.Equal(new[] { "processed/SalesConfirmation/5637144576", "processed/SalesConfirmation/5637144576.pdf" },
            _store.Blobs.Keys.OrderBy(k => k));
        Assert.True(File.Exists(Path.Combine(route.Output, "SO-1001.pdf")));
        Assert.Empty(Directory.GetFiles(Path.Combine(route.Input, ".hive-sources")));
    }

    [Fact]
    public void AFailedPairIsMovedToTheErrorPrefix()
    {
        var route = Route();
        var staging = Options(route);
        // The type is switched off after the pair was staged, so the pipeline sends it to error. A second, enabled type
        // keeps the folder scanned (a folder only disabled types use is not scanned at all).
        var processing = Options(route, typeEnabled: false);
        processing.DocumentTypes.Add(new DocumentTypeOptions { Id = "Other", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { new DocumentRoute { DataAreaId = "USMF", Input = route.Input } } });
        processing.Normalize();
        FoPair();

        Stager().Run(staging);
        Assert.Equal(1, Processor().ScanOnce(processing, License, null, null).Errors);
        Stager().Run(staging);

        Assert.Equal(new[] { "error/SalesConfirmation/5637144576", "error/SalesConfirmation/5637144576.pdf" }, _store.Blobs.Keys.OrderBy(k => k));
    }

    [Fact]
    public void APdfLinkOutsideTheContainerIsNotFetched()
    {
        var route = Route();
        FoPair(pdfLink: "https://elsewhere.example/steal.pdf");

        Stager().Run(Options(route));

        Assert.False(File.Exists(Path.Combine(route.Input, "5637144576_blob.json")));
        Assert.Equal(new[] { "SalesConfirmation/5637144576" }, _store.Downloads);
        Assert.Contains("pdfLink points outside the container", _ledger.Events("5637144576", 10).Single().Message);
    }

    [Fact]
    public void AStagedPairIsNotDownloadedAgainAndSettlesAfterARestart()
    {
        var route = Route();
        var options = Options(route);
        FoPair();

        Stager().Run(options);
        Stager().Run(options);
        Assert.Equal(2, _store.Downloads.Count);

        Processor().ScanOnce(options, License, null, null);
        Stager().Run(options);
        Assert.True(_store.Exists("processed/SalesConfirmation/5637144576"));
    }

    [Fact]
    public void ANoteWithoutStagedFilesIsStagedAgain()
    {
        var route = Route();
        var options = Options(route);
        FoPair();
        Stager().Run(options);
        File.Delete(Path.Combine(route.Input, "5637144576_blob.json"));
        File.Delete(Path.Combine(route.Input, "5637144576_blob.pdf"));

        Stager().Run(options);

        Assert.True(File.Exists(Path.Combine(route.Input, "5637144576_blob.json")));
        Assert.True(File.Exists(Path.Combine(route.Input, "5637144576_blob.pdf")));
    }

    [Fact]
    public void TheBlobTargetUploadsTheFileAndAStatusForTheErp()
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        var route = new DocumentRoute
        {
            DataAreaId = "USMF", Input = input, Output = Path.Combine(input, "out"),
            Targets = { new TargetOptions { Kind = "blob", Connection = "fo-storage", Settings = { ["Container"] = "fo-in", ["Prefix"] = "Returned", ["Status"] = "true" } } },
        };
        var options = Options(route);
        options.DocumentTypes[0].Companies[0].Source = null;
        File.WriteAllText(Path.Combine(input, "doc_1.json"), JsonSerializer.Serialize(new { documentType = "SalesConfirmation", dataAreaId = "USMF", documentNumber = "SO-2" }));
        File.WriteAllText(Path.Combine(input, "doc_1.pdf"), "%PDF-1.4 two");
        var connectors = ConnectorRegistry.BuiltIn();
        connectors.RegisterTarget("blob", runtime => new BlobDocumentTarget(runtime.Options, (_, _) => _store));

        Assert.Equal(1, new DocumentProcessor(NullLogger.Instance,
            new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger, connectors)
            .ScanOnce(options, License, null, null).Processed);

        Assert.Equal("%PDF-1.4 two", Encoding.ASCII.GetString(_store.Blobs["Returned/SO-2.pdf"].Content));
        Assert.Equal("application/pdf", _store.Blobs["Returned/SO-2.pdf"].Type);
        var status = JsonNode.Parse(_store.Blobs["Returned/doc.status.json"].Content)!;
        Assert.Equal("SO-2", (string)status["documentNumber"]!);
        Assert.Equal("Returned/SO-2.pdf", (string)status["file"]!);
    }

    [Fact]
    public void TheSourceIsWrittenAndKeptOnADashboardSave()
    {
        var route = Route();
        var node = SettingsFile.RouteNode(route);
        Assert.Equal("blob", (string)node["Source"]!["Kind"]!);
        Assert.Equal("fo-out", (string)node["Source"]!["Settings"]!["Container"]!);

        var section = new JsonObject
        {
            ["DocumentTypes"] = new JsonArray(new JsonObject
            {
                ["Id"] = "SalesConfirmation",
                ["Companies"] = new JsonArray(node),
            }),
        };
        var kept = SettingsFile.StoredSource(section, "salesconfirmation", "usmf", route.Input + "/");
        Assert.Equal("fo-storage", kept!.Connection);
        Assert.Equal("SalesConfirmation/", kept.Setting("Prefix"));
        Assert.Null(SettingsFile.StoredSource(section, "SalesConfirmation", "OTHER", route.Input));
        Assert.False(SettingsFile.RouteNode(new DocumentRoute { DataAreaId = "A", Input = "x" }).ContainsKey("Source"));
    }

    [Fact]
    public void AnAzureStorageConnectionNeedsAConnectionStringOrAnAccountAddress()
    {
        var none = new ConnectionOptions { Id = "s", Kind = "azure-storage" };
        var identity = new ConnectionOptions { Id = "s", Kind = "azure-storage", Settings = { ["AccountUrl"] = "https://account.blob.core.windows.net" } };
        Assert.Single(none.Problems());
        Assert.Empty(identity.Problems());
    }
}