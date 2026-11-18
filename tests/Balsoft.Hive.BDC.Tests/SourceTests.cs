using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Inbound flows read from any document system: the processor only knows <see cref="IRecordSource"/>.</summary>
public sealed class SourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-sources-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public SourceTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder => Path.Combine(_root, "erp-in");

    // ── a source of a kind the processor has never heard of ────────────────────

    private sealed class FakeSource : IRecordSource
    {
        public List<string> Ready { get; } = new() { "a1" };
        public List<string> Marked { get; } = new();
        public bool ReadFailsLater { get; set; }
        public string Feature { get; set; } = "sharepoint";

        public SourceDescriptor Descriptor => new("fake", "Fake DMS", "test source", "fake", Feature, "record", "FK", "{ID}.csv",
            new[]
            {
                new SourceSetting("Box", "Box", SourceSteps.Which, Required: true),
                new SourceSetting("Done", "Set to", SourceSteps.After, Required: true),
            });

        public IReadOnlyList<string> Problems(SourceOptions source)
            => source.Setting("Box").Length == 0 ? new[] { "Choose the box." } : Array.Empty<string>();

        public Task<IReadOnlyList<string>> ReadyAsync(SourceOptions source, int max)
            => Task.FromResult<IReadOnlyList<string>>(Ready.Except(Marked).Take(max).ToList());

        public Task<SourceRecord> ReadAsync(SourceOptions source, string id)
        {
            if (ReadFailsLater) throw new ConnectorException("the box is busy.", retryLater: true);
            return Task.FromResult(new SourceRecord(id,
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ID"] = id, ["Vendor"] = "70010" },
                new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>()));
        }

        public Task<DocumentFile?> FileAsync(SourceOptions source, string id)
            => Task.FromResult<DocumentFile?>(DocumentFile.FromBytes("scan.pdf", Encoding.ASCII.GetBytes("%PDF fake")));

        public Task<string> MarkDoneAsync(SourceOptions source, string id)
        {
            Marked.Add(id);
            return Task.FromResult($"Marked {id} done in the fake box.");
        }

        public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, SourceOptions source)
            => Task.FromResult<IReadOnlyList<SourceChoice>>(new[] { new SourceChoice("b1", "Box one") });

        public string RecordKey(SourceOptions source, string id) => source.Setting("Box") + "|" + id;
        public string Scope(SourceOptions source) => source.Setting("Box");
    }

    private BDCOptions Options(string kind = "fake", Action<FlowOptions>? change = null)
    {
        var options = new BDCOptions();
        var flow = new FlowOptions
        {
            Id = "vendor-invoices-a", Name = "Vendor invoices", DocumentType = "VendorInvoice", DataAreaId = "DEMF",
            Source = new SourceOptions { Kind = kind, Settings = { ["Box"] = "b1", ["Done"] = "in ERP" } },
            Produce = new ProduceOptions { Columns = { new ColumnOptions { Name = "Company", Value = "{DataAreaId}" }, new ColumnOptions { Name = "Vendor", Value = "{Vendor}" } } },
            Targets = { new TargetOptions { Kind = "folder", Settings = { ["Folder"] = Folder, ["SaveFile"] = "true" } } },
        };
        change?.Invoke(flow);
        options.Flows.Add(flow);
        options.Normalize();
        return options;
    }

    private ImportProcessor Processor(FakeSource source)
    {
        var registry = ConnectorRegistry.BuiltIn();
        registry.RegisterSource("fake", _ => source);
        return new ImportProcessor(NullLogger.Instance, _ledger, registry);
    }

    [Fact]
    public void AnyRegisteredSourceRunsThroughTheSameImport()
    {
        var source = new FakeSource();

        var result = Processor(source).RunOnce(Options(), License);

        Assert.Equal(1, result.Processed);
        Assert.Equal("Company;Vendor\r\nDEMF;70010\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Folder, "a1.csv"))[3..]));
        Assert.Equal("%PDF fake", File.ReadAllText(Path.Combine(Folder, "FKa1.pdf")));
        Assert.Equal(new[] { "a1" }, source.Marked);
        var history = _ledger.Events("a1", 50);
        Assert.Equal(new[] { "received", "delivered", "done" }, history.Select(e => e.Step));
        Assert.Equal("Found record a1 in Fake DMS, ready for flow \"Vendor invoices\".", history[0].Message);
        Assert.Equal("Marked a1 done in the fake box.", history[2].Message);
    }

    [Fact]
    public void AnUnknownKindAMissingSettingOrLicenceTouchesNothing()
    {
        var source = new FakeSource();
        Assert.Equal(0, Processor(source).RunOnce(Options(kind: "shareflex-x"), License).Processed);
        Assert.Equal(0, Processor(source).RunOnce(Options(change: f => f.Source.Settings["Box"] = ""), License).Processed);
        source.Feature = "dms-plus";
        Assert.Equal(0, Processor(source).RunOnce(Options(), License).Processed);

        Assert.Empty(source.Marked);
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
    }

    [Fact]
    public void ASourceThatIsBusyWaitsAndWritesNothing()
    {
        var source = new FakeSource { ReadFailsLater = true };

        var result = Processor(source).RunOnce(Options(), License);

        Assert.Equal(0, result.Processed);
        Assert.Contains("Fake DMS: the box is busy. Hive BDC tries again on the next scan.",
            _ledger.Events("a1", 50).Single(e => e.Step == "waiting").Message);
        Assert.Empty(source.Marked);
    }

    [Fact]
    public void TwoSwitchedOnInboundFlowsReadingTheSameDocumentsAreRefused()
    {
        var registry = ConnectorRegistry.BuiltIn();
        registry.RegisterSource("fake", _ => new FakeSource());
        var a = Options().Flows[0];
        var b = Options(change: f => { f.Id = "vendor-invoices-b"; f.DataAreaId = "USMF"; }).Flows[0];
        var runtime = new SourceRuntime(new BDCOptions(), NullLogger.Instance);

        var problems = InboundChecks.Problems(new[] { a, b }, registry, runtime);

        Assert.Contains(problems, p => p.Contains("Vendor invoices") && p.Contains("same documents"));
        b.Enabled = false;
        Assert.Empty(InboundChecks.Problems(new[] { a, b }, registry, runtime));
    }

    [Fact]
    public void AnOlderInboundEntryWithoutTypeOrCompanyStillLoads()
    {
        var flow = new FlowOptions { Id = "x", Name = "Requisitions from DocuWare" };
        flow.Normalize();

        Assert.Equal("", flow.DocumentType);
        Assert.Equal("", flow.DataAreaId);
        Assert.Equal("Requisitions from DocuWare", flow.TypeLabel);
    }

    // ── the source catalog the dashboard builds its screens from ───────────────

    [Theory]
    [InlineData("archive-test", "secret", true)]
    [InlineData("archive-test", "", false)]
    public void ADocuWareConnectionOfAnyNameMakesTheSourceConnected(string name, string password, bool connected)
    {
        var options = new BDCOptions
        {
            Connections = { new ConnectionOptions { Id = name, Kind = "docuware", Secret = password, Settings = { ["Url"] = "https://contoso.docuware.cloud", ["UserName"] = "ax" } } },
        };
        var catalog = SourceCatalog.Describe(ConnectorRegistry.BuiltIn(), new SourceRuntime(options, NullLogger.Instance));
        Assert.Equal(connected, catalog.Single(s => s.Kind == "docuware").Connected);
    }

    [Fact]
    public void TheCatalogListsEverySourceWithItsSettingsAndWhetherItIsConnected()
    {
        var options = new BDCOptions { SharePointAuth = new SharePointAuthOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" } };
        var catalog = SourceCatalog.Describe(ConnectorRegistry.BuiltIn(), new SourceRuntime(options, NullLogger.Instance));

        var docuWare = catalog.Single(s => s.Kind == "docuware");
        var sharePoint = catalog.Single(s => s.Kind == "sharepoint");
        Assert.False(docuWare.Connected);
        Assert.True(sharePoint.Connected);
        Assert.Equal(new[] { "FileCabinet", "ReadyField", "ReadyValue", "DoneValue" }, docuWare.Descriptor.Settings.Select(s => s.Key));
        Assert.Equal(new[] { "SiteUrl", "Library", "ReadyColumn", "ReadyValue", "DoneValue" }, sharePoint.Descriptor.Settings.Select(s => s.Key));
        Assert.Equal("Library", sharePoint.Descriptor.Settings.Single(s => s.Key == "ReadyColumn").DependsOn);
    }

    [Fact]
    public async Task ChoicesComeFromTheSourceAndAFailureComesBackAsItsMessage()
    {
        var registry = ConnectorRegistry.BuiltIn();
        registry.RegisterSource("fake", _ => new FakeSource());
        var runtime = new SourceRuntime(new BDCOptions(), NullLogger.Instance);

        var ok = await SourceCatalog.ChoicesAsync(registry, runtime, "fake", "boxes", new SourceOptions { Kind = "fake" });
        Assert.Equal("Box one", Assert.Single(ok.Choices).Label);
        Assert.Null(ok.Error);

        var unknown = await SourceCatalog.ChoicesAsync(registry, runtime, "nope", "x", new SourceOptions());
        Assert.Contains("not known", unknown.Error);

        var noApp = await SourceCatalog.ChoicesAsync(registry, runtime, "sharepoint", "libraries",
            new SourceOptions { Kind = "sharepoint", Settings = { ["SiteUrl"] = "https://tenant.sharepoint.com/sites/Finance" } });
        Assert.Empty(noApp.Choices);
        Assert.Contains("Connections", noApp.Error);
    }

    // ── SharePoint library as a source, against a fake Graph ───────────────────

    private sealed class FakeGraph : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body, string Prefer)> Calls { get; } = new();
        public HttpStatusCode ItemsStatus { get; set; } = HttpStatusCode.OK;
        public string ItemsError { get; set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Calls.Add((request.Method.Method, url, body, request.Headers.TryGetValues("Prefer", out var p) ? string.Join(",", p) : ""));
            if (url.Contains("login.microsoftonline.com")) return Json("""{"access_token":"tok","expires_in":3600}""");
            if (url.Contains("/sites/tenant.sharepoint.com:/sites/Finance")) return Json("""{"id":"site-1"}""");
            if (url.EndsWith("/sites/site-1/lists?$select=id,name,displayName,list&$top=200"))
                return Json("""{"value":[{"id":"list-1","name":"IncomingInvoices","displayName":"Incoming invoices","list":{"template":"documentLibrary"}},{"id":"list-2","name":"Tasks","displayName":"Tasks","list":{"template":"genericList"}}]}""");
            if (url.Contains("/lists/list-1/columns"))
                return Json("""{"value":[{"name":"Status","displayName":"Status","choice":{"choices":["New","Approved","In ERP"]}},{"name":"InvoiceDate","displayName":"Invoice date","dateTime":{}},{"name":"GrossAmount","displayName":"Gross amount","number":{}},{"name":"Title","displayName":"Title","text":{}},{"name":"_ComplianceFlags","displayName":"Flags","hidden":true,"text":{}}]}""");
            if (url.Contains("/lists/list-1/items?") && request.Method == HttpMethod.Get)
                return ItemsStatus == HttpStatusCode.OK
                    ? Json("""{"value":[{"id":"45"},{"id":"46"}]}""")
                    : Json(ItemsError, ItemsStatus);
            if (url.Contains("/lists/list-1/items/45/driveItem/content"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF sp")) };
            if (url.Contains("/lists/list-1/items/45/fields") && request.Method == HttpMethod.Patch) return Json("{}");
            if (url.Contains("/lists/list-1/items/45?"))
                return Json("""{"id":"45","webUrl":"https://tenant.sharepoint.com/sites/Finance/Incoming/INV-1.pdf","fields":{"@odata.etag":"\"1\"","FileLeafRef":"INV-1.pdf","Title":"Invoice 1","VendorNumber":"70010","GrossAmount":1234.5,"InvoiceDate":"2026-11-04T23:00:00Z","Approved":true,"Tags":["a","b"],"Status":"Approved"}}""");
            return Json("""{"error":{"message":"not in the fake"}}""", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static readonly SourceOptions Library = new()
    {
        Kind = "sharepoint",
        Settings =
        {
            ["SiteUrl"] = "https://tenant.sharepoint.com/sites/Finance", ["Library"] = "Incoming invoices",
            ["ReadyColumn"] = "Status", ["ReadyValue"] = "Approved", ["DoneValue"] = "In ERP",
        },
    };

    private static SharePointRecordSource SharePoint(FakeGraph graph)
        => new(new SharePointAuthOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" }, graph);

    [Fact]
    public async Task ReadyItemsAreTheOnesWhoseColumnHasTheReadyValue()
    {
        var graph = new FakeGraph();

        var ids = await SharePoint(graph).ReadyAsync(Library, 50);

        Assert.Equal(new[] { "45", "46" }, ids);
        var query = graph.Calls.Single(c => c.Url.Contains("/items?"));
        Assert.Contains("$filter=fields/Status eq 'Approved'", query.Url);
        Assert.Contains("$top=50", query.Url);
        Assert.Contains("HonorNonIndexedQueriesWarningMayFailRandomly", query.Prefer);
    }

    [Fact]
    public async Task AnItemBecomesARecordWithItsColumnsTypedAndItsFile()
    {
        var graph = new FakeGraph();
        var source = SharePoint(graph);

        var record = await source.ReadAsync(Library, "45");
        var file = await source.FileAsync(Library, "45");

        Assert.Equal("45", record.Values["ID"]);
        Assert.Equal("INV-1.pdf", record.Values["FileName"]);
        Assert.Equal(1234.5m, record.Values["GrossAmount"]);
        Assert.Equal(new DateTime(2026, 11, 5), record.Values["InvoiceDate"]);
        Assert.Equal(true, record.Values["Approved"]);
        Assert.Equal("a, b", record.Values["Tags"]);
        Assert.False(record.Values.ContainsKey("@odata.etag"));
        Assert.Equal("INV-1.pdf", file!.Name);
        Assert.Equal("%PDF sp", Encoding.ASCII.GetString(file.ReadAllBytes()));
    }

    [Fact]
    public async Task MarkingWritesOnlyTheStatusColumn()
    {
        var graph = new FakeGraph();

        string line = await SharePoint(graph).MarkDoneAsync(Library, "45");

        var patch = graph.Calls.Single(c => c.Method == "PATCH");
        Assert.EndsWith("/items/45/fields", patch.Url);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"Status":"In ERP"}"""), JsonNode.Parse(patch.Body)));
        Assert.Equal("Marked Status = In ERP in SharePoint, so it is not imported again.", line);
    }

    [Fact]
    public async Task AColumnSharePointCannotFilterOnSaysWhatToDo()
    {
        var graph = new FakeGraph
        {
            ItemsStatus = HttpStatusCode.BadRequest,
            ItemsError = """{"error":{"code":"invalidRequest","message":"Field 'Status' cannot be referenced in filter or orderby as it is not indexed."}}""",
        };

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => SharePoint(graph).ReadyAsync(Library, 50));

        Assert.False(ex.RetryLater);
        Assert.Contains("Index the column Status", ex.Message);
    }

    [Fact]
    public async Task ABusySharePointIsTriedAgainLater()
    {
        var graph = new FakeGraph { ItemsStatus = HttpStatusCode.ServiceUnavailable, ItemsError = """{"error":{"message":"busy"}}""" };

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => SharePoint(graph).ReadyAsync(Library, 50));

        Assert.True(ex.RetryLater);
    }

    [Fact]
    public async Task TheDropdownsListLibrariesColumnsAndTheValuesOfAChoiceColumn()
    {
        var source = SharePoint(new FakeGraph());

        var libraries = await source.ChoicesAsync("libraries", Library);
        var columns = await source.ChoicesAsync("columns", Library);
        var values = await source.ChoicesAsync("values", Library);
        var fields = await source.ChoicesAsync("fields", Library);

        Assert.Equal(new[] { "Incoming invoices" }, libraries.Select(c => c.Value));
        Assert.Equal(new[] { "Status", "InvoiceDate", "GrossAmount", "Title" }, columns.Select(c => c.Value));
        Assert.Equal("date", columns.Single(c => c.Value == "InvoiceDate").Detail);
        Assert.Equal(new[] { "New", "Approved", "In ERP" }, values.Select(c => c.Value));
        Assert.Contains(fields, c => c.Value == "FileName");
        Assert.Contains(fields, c => c.Value == "ID");
    }

    [Fact]
    public void ALibraryWithoutTheSharePointAppOrItsSettingsSaysWhatIsMissing()
    {
        var unset = new SharePointRecordSource(null, new FakeGraph());
        Assert.Contains(unset.Problems(Library), p => p.Contains("Connections"));

        var odd = new SourceOptions { Kind = "sharepoint", Settings = new(Library.Settings, StringComparer.OrdinalIgnoreCase) { ["ReadyColumn"] = "Status eq 'x' or true" } };
        Assert.Contains(SharePoint(new FakeGraph()).Problems(odd), p => p.Contains("internal name"));

        var partial = new SourceOptions { Kind = "sharepoint", Settings = { ["SiteUrl"] = "https://tenant.sharepoint.com/sites/Finance" } };
        Assert.Contains(SharePoint(new FakeGraph()).Problems(partial), p => p.Contains("library"));
        Assert.Empty(SharePoint(new FakeGraph()).Problems(Library));
    }
}