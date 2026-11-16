using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class DocuWareTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-docuware-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private readonly FakeDocuWare _server = new();

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public DocuWareTargetTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A DocuWare cloud with one file cabinet, answering like the real platform and recording every call.</summary>
    private sealed class FakeDocuWare : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = new();
        public bool RefuseSignIn { get; set; }
        public string? FoundId { get; set; }
        public int NextId { get; set; } = 42;

        public static string CabinetJson => """
            {"Id":"c-1","Name":"Invoices","Fields":[
              {"DBFieldName":"DOCNO","DisplayName":"Number","DWFieldType":"Text","Length":20,"NotEmpty":true,"Scope":"User","TableFieldColumns":[]},
              {"DBFieldName":"DOCDATE","DisplayName":"Date","DWFieldType":"Date","Length":-1,"NotEmpty":false,"Scope":"User","TableFieldColumns":[]},
              {"DBFieldName":"NET","DisplayName":"Net","DWFieldType":"Decimal","Length":-1,"NotEmpty":false,"Scope":"User","TableFieldColumns":[]},
              {"DBFieldName":"KEYS","DisplayName":"Delivery notes","DWFieldType":"Keyword","Length":10,"NotEmpty":false,"Scope":"User","TableFieldColumns":[]},
              {"DBFieldName":"ITEMS","DisplayName":"Items","DWFieldType":"Table","Length":-1,"NotEmpty":false,"Scope":"User","TableFieldColumns":[
                {"DBFieldName":"QTY","DisplayName":"Quantity","DWFieldType":"Numeric","Length":-1,"NotEmpty":false,"Scope":"User"},
                {"DBFieldName":"PRICE","DisplayName":"Price","DWFieldType":"Decimal","Length":-1,"NotEmpty":false,"Scope":"User"}]},
              {"DBFieldName":"DWSTOREDATETIME","DisplayName":"Stored","DWFieldType":"DateTime","Length":-1,"NotEmpty":true,"Scope":"System","TableFieldColumns":[]}]}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.PathAndQuery;
            Calls.Add((request.Method.Method, path, body));
            return (request.Method.Method, request.RequestUri.AbsolutePath) switch
            {
                ("GET", "/DocuWare/Platform/Home/IdentityServiceInfo") => Json("""{"IdentityServiceUrl":"https://login.example/org/"}"""),
                ("GET", "/org/.well-known/openid-configuration") => Json("""{"token_endpoint":"https://login.example/org/connect/token"}"""),
                ("POST", "/org/connect/token") => RefuseSignIn
                    ? Json("""{"error":"invalid_grant","error_description":"You have no permissions to log in with DocuWare credentials."}""", HttpStatusCode.BadRequest)
                    : Json("""{"access_token":"token-1","token_type":"Bearer","expires_in":3600}"""),
                ("GET", "/DocuWare/Platform/FileCabinets/c-1") => Json(CabinetJson),
                ("POST", "/DocuWare/Platform/FileCabinets/c-1/Documents") => Json($$"""{"Id":{{NextId++}}}"""),
                ("GET", "/DocuWare/Platform/FileCabinets/c-1/Dialogs") => Json("""{"Dialog":[{"Id":"d-1","DisplayName":"Search"}]}"""),
                ("POST", "/DocuWare/Platform/FileCabinets/c-1/Query/DialogExpression") =>
                    Json(FoundId is null ? """{"Items":[]}""" : $$"""{"Items":[{"Id":{{FoundId}}}]}"""),
                ("PUT", var p) when p.StartsWith("/DocuWare/Platform/FileCabinets/c-1/Documents/") && p.EndsWith("/Fields") => Json("""{"Field":[]}"""),
                _ => Json("""{"Message":"not found in the fake"}""", HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public int Count(string method, string pathStart) => Calls.Count(c => c.Method == method && c.Path.StartsWith(pathStart));

        public JsonArray StoredFields()
        {
            string body = Calls.Last(c => c.Method == "POST" && c.Path.EndsWith("/Documents")).Body;
            int start = body.IndexOf("{\"Fields\"", StringComparison.Ordinal);
            int end = body.IndexOf("\r\n--", start, StringComparison.Ordinal);
            return (JsonArray)JsonNode.Parse(body[start..end])!["Fields"]!;
        }
    }

    private sealed class FlakyTarget : IDocumentTarget
    {
        public bool Fail { get; set; } = true;
        public string Kind => "flaky";
        public bool ReplacesOnRepeat => true;
        public string? Unavailable(TargetOptions target) => null;
        public DeliveryResult Deliver(DeliveryContext ctx) => Fail ? throw new InvalidOperationException("flaky is down") : new DeliveryResult();
    }

    private DocumentRoute Route(params TargetOptions[] targets)
    {
        string input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        return new DocumentRoute { DataAreaId = "A", Input = input, Output = Path.Combine(input, "out"), Targets = targets.ToList() };
    }

    private static TargetOptions DocuWare(string? matchField = null, Dictionary<string, string>? fields = null)
    {
        var t = new TargetOptions
        {
            Kind = "docuware",
            Connection = "dms",
            Settings = { ["FileCabinet"] = "c-1" },
            Fields = fields ?? new()
            {
                ["DOCNO"] = "{documentNumber}",
                ["DOCDATE"] = "{documentDate}",
                ["NET"] = "{netAmount}",
                ["KEYS"] = "{deliveryNotes}",
                ["ITEMS.QTY"] = "{lines.quantity}",
                ["ITEMS.PRICE"] = "{lines.price}",
            },
        };
        if (matchField is not null) t.Settings["MatchField"] = matchField;
        return t;
    }

    private static BDCOptions Options(DocumentRoute route, bool withConnection = true)
    {
        var options = new BDCOptions();
        if (withConnection)
            options.Connections.Add(new ConnectionOptions
            {
                Id = "dms", Kind = "docuware", Secret = "pw",
                Settings = { ["Url"] = "https://dms.example/DocuWare/Platform/WebClient", ["UserName"] = "integration" },
            });
        options.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "PurchaseOrder", DisplayName = "Purchase order", Handler = DocumentHandlerKind.Pdf, Mail = false, Companies = { route },
        });
        options.Normalize();
        return options;
    }

    private static void Pair(DocumentRoute route, string id = "order")
    {
        string path = Path.Combine(route.Input, id + "_1.json");
        File.WriteAllText(path, """
            {"documentType":"PurchaseOrder","dataAreaId":"A","documentNumber":"PO-7","documentDate":"03.11.2026",
             "netAmount":"1.234,50","deliveryNotes":"LS-1; LS-2",
             "lines":[{"quantity":"2","price":"10.5"},{"quantity":"3","price":"1,25"}]}
            """);
        File.WriteAllText(Path.ChangeExtension(path, ".pdf"), "%PDF-1.4 synthetic");
    }

    private DocumentProcessor Processor(params IDocumentTarget[] extra)
    {
        var client = new DocuWareClient("https://dms.example", "integration", "pw", _server);
        var connectors = ConnectorRegistry.BuiltIn();
        connectors.RegisterTarget("docuware", runtime => new DocuWareDocumentTarget(runtime.Options, _ => client));
        foreach (var t in extra) connectors.RegisterTarget(t.Kind, _ => t);
        return new DocumentProcessor(NullLogger.Instance,
            new DocumentTypeRegistry(Path.Combine(_root, "settings.json"), NullLogger.Instance), _ledger, connectors);
    }

    [Fact]
    public void StoresTheFileWithIndexValuesTypedByTheCabinet()
    {
        var route = Route(DocuWare());
        Pair(route);

        Assert.Equal(1, Processor().ScanOnce(Options(route), License, null, null).Processed);

        Assert.Equal(1, _server.Count("POST", "/DocuWare/Platform/FileCabinets/c-1/Documents"));
        var fields = _server.StoredFields().ToDictionary(f => (string)f!["FieldName"]!, f => f!.AsObject());
        Assert.Equal("PO-7", (string)fields["DOCNO"]["Item"]!);
        Assert.Equal("String", (string)fields["DOCNO"]["ItemElementName"]!);
        Assert.Equal("2026-11-03", (string)fields["DOCDATE"]["Item"]!);
        Assert.Equal("Date", (string)fields["DOCDATE"]["ItemElementName"]!);
        Assert.Equal(1234.50m, (decimal)fields["NET"]["Item"]!);
        Assert.Equal("Decimal", (string)fields["NET"]["ItemElementName"]!);
        Assert.Equal("DocumentIndexFieldKeywords", (string)fields["KEYS"]["Item"]!["$type"]!);
        Assert.Equal(new[] { "LS-1", "LS-2" }, fields["KEYS"]["Item"]!["Keyword"]!.AsArray().Select(k => (string)k!));
        var rows = fields["ITEMS"]["Item"]!["Row"]!.AsArray();
        Assert.Equal(2, rows.Count);
        var second = rows[1]!["ColumnValue"]!.AsArray().ToDictionary(c => (string)c!["FieldName"]!, c => c!);
        Assert.Equal(3, (int)second["QTY"]["Item"]!);
        Assert.Equal("Int", (string)second["QTY"]["ItemElementName"]!);
        Assert.Equal(1.25m, (decimal)second["PRICE"]["Item"]!);

        var body = _server.Calls.Last(c => c.Path.EndsWith("/Documents")).Body;
        Assert.Contains("name=document; filename=document.json", body);
        Assert.Contains("name=file; filename=PO-7.pdf", body);
        var delivered = _ledger.Events("PO-7", 50).Single(e => e.Step == "delivered");
        Assert.Equal("Stored PO-7.pdf in file cabinet \"Invoices\" as document 42, 5 field(s) set.", delivered.Message);
    }

    [Fact]
    public void ARetryAfterAnotherTargetFailedUpdatesTheStoredDocumentAndStoresNoSecondCopy()
    {
        var flaky = new FlakyTarget();
        var route = Route(DocuWare(), new TargetOptions { Kind = "flaky" });
        var options = Options(route);
        var processor = Processor(flaky);

        Pair(route);
        Assert.Equal(1, processor.ScanOnce(options, License, null, null).Errors);
        flaky.Fail = false;
        Pair(route);
        Assert.Equal(1, processor.ScanOnce(options, License, null, null).Processed);

        Assert.Equal(1, _server.Count("POST", "/DocuWare/Platform/FileCabinets/c-1/Documents"));
        Assert.Equal(1, _server.Count("PUT", "/DocuWare/Platform/FileCabinets/c-1/Documents/42/Fields"));
        Assert.Contains("no second copy stored", _ledger.Events("PO-7", 50).Last(e => e.Target == "docuware").Message);
    }

    [Fact]
    public void TheMatchFieldFindsACopyTheLedgerDoesNotKnow()
    {
        _server.FoundId = "77";
        var route = Route(DocuWare(matchField: "DOCNO"));
        Pair(route);

        Assert.Equal(1, Processor().ScanOnce(Options(route), License, null, null).Processed);

        Assert.Equal(0, _server.Count("POST", "/DocuWare/Platform/FileCabinets/c-1/Documents"));
        Assert.Equal(1, _server.Count("PUT", "/DocuWare/Platform/FileCabinets/c-1/Documents/77/Fields"));
        var query = _server.Calls.Single(c => c.Path.Contains("/Query/DialogExpression"));
        Assert.Contains("dialogId=d-1", query.Path);
        Assert.Equal("PO-7", (string)JsonNode.Parse(query.Body)!["Condition"]![0]!["Value"]![0]!);
    }

    [Fact]
    public void AFieldTheCabinetDoesNotHaveStoresNothingAndNamesTheField()
    {
        var route = Route(DocuWare(fields: new() { ["DOCNO"] = "{documentNumber}", ["NOPE"] = "{documentNumber}" }));
        Pair(route);

        Assert.Equal(1, Processor().ScanOnce(Options(route), License, null, null).Errors);

        Assert.Equal(0, _server.Count("POST", "/DocuWare/Platform/FileCabinets/c-1/Documents"));
        var failed = _ledger.Events("PO-7", 50).Single(e => e.Target == "docuware");
        Assert.Contains("Field NOPE does not exist in file cabinet \"Invoices\"", failed.Message);
    }

    [Fact]
    public void ARefusedSignInHoldsTheDocumentWithDocuWaresReason()
    {
        _server.RefuseSignIn = true;
        var route = Route(DocuWare());
        Pair(route);

        var result = Processor().ScanOnce(Options(route), License, null, null);

        Assert.Equal(1, result.Held);
        Assert.True(File.Exists(Path.Combine(route.Input, "order_1.json")));
        var waiting = _ledger.Events("order", 50).Single(e => e.Step == "waiting");
        Assert.Contains("You have no permissions to log in with DocuWare credentials.", waiting.Message);
        Assert.Contains("tries again on the next scan", waiting.Message);
    }

    [Fact]
    public void WithoutTheLicenceFeatureTheDocumentWaitsBeforeAnythingIsSent()
    {
        var route = Route(DocuWare());
        Pair(route);
        var documentsOnly = new LicenseResult { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

        Assert.Equal(1, Processor().ScanOnce(Options(route), documentsOnly, null, null).Held);

        Assert.Empty(_server.Calls);
        Assert.Contains("'sharepoint' licence feature", _ledger.Events("order", 50).Single(e => e.Step == "waiting").Message);
    }

    [Fact]
    public void AMissingConnectionHoldsTheDocumentWithWhereToAddIt()
    {
        var route = Route(DocuWare());
        Pair(route);

        Assert.Equal(1, Processor().ScanOnce(Options(route, withConnection: false), License, null, null).Held);

        Assert.Empty(_server.Calls);
        Assert.Contains("Add it in Settings > Connections", _ledger.Events("order", 50).Single(e => e.Step == "waiting").Message);
    }

    [Fact]
    public void OneSignInServesSeveralDocuments()
    {
        var route = Route(DocuWare());
        var processor = Processor();
        var options = Options(route);
        Pair(route, "first");
        Pair(route, "second");

        Assert.Equal(2, processor.ScanOnce(options, License, null, null).Processed);

        Assert.Equal(1, _server.Count("POST", "/org/connect/token"));
        Assert.Equal(1, _server.Count("GET", "/DocuWare/Platform/FileCabinets/c-1"));
    }

    [Theory]
    [InlineData("https://dms.example", "https://dms.example/DocuWare/Platform")]
    [InlineData("https://dms.example/", "https://dms.example/DocuWare/Platform")]
    [InlineData("https://dms.example/DocuWare/Platform/WebClient", "https://dms.example/DocuWare/Platform")]
    [InlineData("https://dms.example/DocuWare/Platform/FileCabinets/x", "https://dms.example/DocuWare/Platform")]
    public void TheAddressMayBeCopiedFromTheBrowser(string typed, string platform)
        => Assert.Equal(platform, DocuWareClient.PlatformUrl(typed));

    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-7", -7)]
    public void NumbersInGermanAndEnglishNotationAreRead(string text, double expected)
    {
        Assert.True(DocuWareFieldValues.TryParseNumber(text, out var n));
        Assert.Equal((decimal)expected, n);
    }

    [Fact]
    public void ARequiredFieldWithoutValueAndATooLongValueAreReported()
    {
        var cabinet = new DocuWareCabinet("c-1", "Invoices", new[]
        {
            new DocuWareField("DOCNO", "Number", "Text", true, 5, Array.Empty<DocuWareField>()),
            new DocuWareField("NOTE", "Note", "Text", false, 3, Array.Empty<DocuWareField>()),
        });
        var problems = new List<string>();

        DocuWareFieldValues.Build(cabinet, new Dictionary<string, string> { ["NOTE"] = "too long" },
            new Dictionary<string, List<Dictionary<string, string>>>(), problems);

        Assert.Equal(new[]
        {
            "Field NOTE: the value has 8 characters, the field holds 3.",
            "Field DOCNO (Number) is required in file cabinet \"Invoices\" but gets no value.",
        }, problems);
    }

    [Fact]
    public void SavingSettingsKeepsAStoredSecretWhenTheDashboardSendsNone()
    {
        string marker = string.Concat("enc", ":", "stored");
        var connection = (JsonObject)JsonNode.Parse("""{"Id":"dms","Kind":"docuware","Settings":{"Url":"https://dms.example"}}""")!;
        connection["Secret"] = marker;
        var stored = new JsonObject { ["Connections"] = new JsonArray(connection) };

        var kept = LocalStatusServer.ConnectionsNode(new[]
        {
            new LocalStatusServer.ConnectionPostDtoView("DMS", "DocuWare", new() { ["Url"] = "https://dms.example", ["UserName"] = "integration" }, ""),
        }, stored).AsArray();
        Assert.Equal(marker, (string)kept[0]!["Secret"]!);
        Assert.Equal("docuware", (string)kept[0]!["Kind"]!);

        Assert.True(JsonNode.DeepEquals(stored["Connections"], LocalStatusServer.ConnectionsNode(null, stored)));
        Assert.Empty(LocalStatusServer.ConnectionsNode(Array.Empty<LocalStatusServer.ConnectionPostDtoView>(), stored).AsArray());
    }
}