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

public sealed class ImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-import-" + Guid.NewGuid());
    private readonly ExportLedger _ledger;
    private readonly FakeDocuWare _server = new();

    private static readonly LicenseResult License = new()
    {
        State = LicenseState.Valid,
        Info = new LicenseInfo { Features = new() { "documents", "sharepoint" } },
    };

    public ImportTests()
    {
        Directory.CreateDirectory(_root);
        _ledger = ExportLedger.CreateIfMissing(NullLogger.Instance, Path.Combine(_root, "hive.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A DocuWare cabinet with documents 17 and 18 ready; 17 has two table rows.</summary>
    private sealed class FakeDocuWare : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = new();
        public List<string> Ready { get; } = new() { "17" };
        public bool FailDoneMark { get; set; }

        private static string Document(string id) => $$"""
            {"Id":{{id}},"ContentType":"application/pdf","Fields":[
              {"FieldName":"VENDOR","Item":"70010","ItemElementName":"String","IsNull":false},
              {"FieldName":"TEXT","Item":"Steel; \"cut\"","ItemElementName":"String","IsNull":false},
              {"FieldName":"DOCDATE","Item":"/Date(1793833200000)/","ItemElementName":"Date","IsNull":false},
              {"FieldName":"NOTES","Item":{"$type":"DocumentIndexFieldKeywords","Keyword":["LS-1","LS-2"]},"ItemElementName":"Keywords","IsNull":false},
              {"FieldName":"EMPTY","Item":null,"ItemElementName":"String","IsNull":true},
              {"FieldName":"ITEMS","Item":{"$type":"DocumentIndexFieldTable","Row":[
                {"ColumnValue":[{"FieldName":"ITEM","Item":"A-1","ItemElementName":"String"},{"FieldName":"QTY","Item":2,"ItemElementName":"Int"},{"FieldName":"PRICE","Item":1234.5,"ItemElementName":"Decimal"}]},
                {"ColumnValue":[{"FieldName":"ITEM","Item":"B-2","ItemElementName":"String"},{"FieldName":"QTY","Item":3,"ItemElementName":"Int"},{"FieldName":"PRICE","Item":0.25,"ItemElementName":"Decimal"}]}]},
               "ItemElementName":"Table","IsNull":false}]}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, request.RequestUri.PathAndQuery, body));
            if (path == "/DocuWare/Platform/Home/IdentityServiceInfo") return Json("""{"IdentityServiceUrl":"https://login.example/org"}""");
            if (path == "/org/.well-known/openid-configuration") return Json("""{"token_endpoint":"https://login.example/org/connect/token"}""");
            if (path == "/org/connect/token") return Json("""{"access_token":"t","expires_in":3600}""");
            if (path == "/DocuWare/Platform/FileCabinets/c-1/Dialogs") return Json("""{"Dialog":[{"Id":"d-1"}]}""");
            if (path == "/DocuWare/Platform/FileCabinets/c-1/Query/DialogExpression")
                return Json("{\"Items\":[" + string.Join(",", Ready.Select(r => $"{{\"Id\":{r}}}")) + "]}");
            if (request.Method == HttpMethod.Put && path.EndsWith("/Fields"))
            {
                if (FailDoneMark) return Json("""{"Message":"busy"}""", HttpStatusCode.ServiceUnavailable);
                Ready.Remove(path.Split('/')[^2]);
                return Json("""{"Field":[]}""");
            }
            if (path.EndsWith("/FileDownload"))
            {
                var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 test")) };
                file.Content.Headers.ContentType = new("application/pdf");
                file.Content.Headers.ContentDisposition = new("attachment") { FileName = "scan.pdf" };
                return file;
            }
            if (path.StartsWith("/DocuWare/Platform/FileCabinets/c-1/Documents/")) return Json(Document(path.Split('/')[^1]));
            return Json("""{"Message":"not in the fake"}""", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public int Count(string method, string pathPart) => Calls.Count(c => c.Method == method && c.Path.Contains(pathPart));
    }

    private string Folder => Path.Combine(_root, "pending");

    private BDCOptions Options(Action<FlowOptions>? change = null)
    {
        var options = new BDCOptions();
        options.Connections.Add(new ConnectionOptions
        {
            Id = "docuware", Kind = "docuware", Secret = "pw",
            Settings = { ["Url"] = "https://dms.example", ["UserName"] = "integration" },
        });
        var flow = new FlowOptions
        {
            Id = "requisitions",
            Name = "Requisitions from DocuWare",
            Source = new SourceOptions
            {
                Kind = "docuware", Connection = "docuware",
                Settings = { ["FileCabinet"] = "c-1", ["ReadyField"] = "STATUS", ["ReadyValue"] = "ready", ["DoneValue"] = "in ERP" },
            },
            Produce = new ProduceOptions
            {
                Settings = { ["RowsFrom"] = "ITEMS" },
                Columns =
                {
                    new ColumnOptions { Name = "DWDOCID", Value = "{DWDOCID}" },
                    new ColumnOptions { Name = "OrderAccount", Value = "{VENDOR}" },
                    new ColumnOptions { Name = "PurchLineText", Value = "{TEXT}" },
                    new ColumnOptions { Name = "ItemId", Value = "{ITEMS.ITEM}" },
                    new ColumnOptions { Name = "PurchQty", Value = "{ITEMS.QTY}" },
                    new ColumnOptions { Name = "PurchPrice", Value = "{ITEMS.PRICE}" },
                    new ColumnOptions { Name = "DeliveryDate", Value = "{DOCDATE}" },
                    new ColumnOptions { Name = "Notes", Value = "{NOTES}" },
                    new ColumnOptions { Name = "PdfLink", Value = "{FilePath}" },
                },
            },
            Targets = { new TargetOptions { Kind = "folder", Settings = { ["Folder"] = Folder, ["SaveFile"] = "true" } } },
        };
        change?.Invoke(flow);
        options.Flows.Add(flow);
        options.Normalize();
        return options;
    }

    private ImportProcessor Processor()
    {
        var client = new DocuWareClient("https://dms.example", "integration", "pw", _server);
        return new ImportProcessor(NullLogger.Instance, _ledger, _ => client);
    }

    [Fact]
    public void AReadyDocumentBecomesTheCsvTheErpReadsAndIsMarkedDone()
    {
        var result = Processor().RunOnce(Options(), License);

        Assert.Equal(1, result.Processed);
        byte[] bytes = File.ReadAllBytes(Path.Combine(Folder, "17.csv"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string pdf = Path.Combine(Folder, "DW17.pdf");
        Assert.Equal(
            "DWDOCID;OrderAccount;PurchLineText;ItemId;PurchQty;PurchPrice;DeliveryDate;Notes;PdfLink\r\n" +
            $"17;70010;\"Steel; \"\"cut\"\"\";A-1;2;1234,5;05.11.2026;LS-1, LS-2;{pdf}\r\n" +
            $"17;70010;\"Steel; \"\"cut\"\"\";B-2;3;0,25;05.11.2026;LS-1, LS-2;{pdf}\r\n",
            Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal("%PDF-1.4 test", File.ReadAllText(pdf));
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));

        var mark = _server.Calls.Single(c => c.Method == "PUT");
        Assert.Contains("/Documents/17/Fields", mark.Path);
        var field = JsonNode.Parse(mark.Body)!["Field"]![0]!;
        Assert.Equal("STATUS", (string)field["FieldName"]!);
        Assert.Equal("in ERP", (string)field["Item"]!);
        Assert.Contains("dialogId=d-1", _server.Calls.Single(c => c.Path.Contains("DialogExpression")).Path);

        var history = _ledger.Events("17", 50);
        Assert.Equal(new[] { "received", "delivered", "done" }, history.Select(e => e.Step));
        Assert.Equal($"Wrote 17.csv with 2 row(s) to {Folder} and the file as DW17.pdf.", history[1].Message);
        Assert.Equal("Requisitions from DocuWare", history[0].DocumentType);
    }

    [Fact]
    public void WhenTheDoneMarkFailsTheNextRunOnlyRepeatsTheMark()
    {
        _server.FailDoneMark = true;
        var processor = Processor();
        var options = Options();

        Assert.Equal(1, processor.RunOnce(options, License).Held);
        Assert.True(File.Exists(Path.Combine(Folder, "17.csv")));
        File.Delete(Path.Combine(Folder, "17.csv"));

        _server.FailDoneMark = false;
        Assert.Equal(1, processor.RunOnce(options, License).Processed);

        Assert.False(File.Exists(Path.Combine(Folder, "17.csv")));
        Assert.Equal(1, _server.Count("GET", "/Documents/17/FileDownload"));
        Assert.Equal(2, _server.Count("PUT", "/Documents/17/Fields"));
        Assert.Contains("tries again on the next scan", _ledger.Events("17", 50).Single(e => e.Step == "waiting").Message);
    }

    [Fact]
    public void AColumnNamingAFieldTheDocumentLacksWritesNothingAndMarksNothing()
    {
        var processor = Processor();
        var options = Options(f => f.Produce.Columns.Add(new ColumnOptions { Name = "Project", Value = "{PROJECT}" }));

        Assert.Equal(1, processor.RunOnce(options, License).Errors);

        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFiles(Folder).Any());
        Assert.Equal(0, _server.Count("PUT", "/Fields"));
        Assert.Contains("Column Project: the record has no field PROJECT.", _ledger.Events("17", 50).Single(e => e.Step == "failed").Message);

        Assert.Equal(0, processor.RunOnce(options, License).Errors);
        processor.SettingsChanged();
        Assert.Equal(1, processor.RunOnce(options, License).Errors);
    }

    [Fact]
    public void AnIncompleteFlowOrMissingLicenceTouchesNothing()
    {
        var noFolder = Options(f => f.Targets.Clear());
        var documentsOnly = new LicenseResult { State = LicenseState.Valid, Info = new LicenseInfo { Features = new() { "documents" } } };

        Assert.Equal(0, Processor().RunOnce(noFolder, License).Processed);
        Assert.Equal(0, Processor().RunOnce(Options(), documentsOnly).Processed);

        Assert.Empty(_server.Calls);
    }

    [Theory]
    [InlineData("/Date(1793833200000)/")]
    [InlineData("/Date(1793836800000)/")]
    [InlineData("2026-11-05")]
    public void ADocuWareDateIsTheDayAsStoredInGermany(string sent)
    {
        var field = System.Text.Json.JsonDocument.Parse($$"""{"FieldName":"D","Item":"{{sent}}","ItemElementName":"Date"}""").RootElement;
        Assert.Equal(new DateTime(2026, 11, 5), DocuWareClient.IndexValue(field));
    }

    [Theory]
    [InlineData("de-DE", "1234,5", "31.12.2026")]
    [InlineData("en-US", "1234.5", "12/31/2026")]
    [InlineData("", "1234.5", "2026-12-31")]
    public void NumbersAndDatesFollowTheConfiguredFormat(string culture, string number, string date)
    {
        var c = ImportProcessor.CultureFor(culture);
        Assert.Equal(number, CsvFile.Format(1234.5m, c));
        Assert.Equal(date, CsvFile.Format(new DateTime(2026, 12, 31), c));
    }
}