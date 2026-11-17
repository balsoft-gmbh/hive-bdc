using System.Net;
using System.Text;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Outbound destinations and connections as the dashboard asks for them: which kinds exist and what each needs.</summary>
public sealed class DestinationCatalogTests
{
    private sealed class FakeDocuWare : HttpMessageHandler
    {
        private const string Cabinet = """
            {"Id":"c-1","Name":"Invoices","Fields":[
              {"DBFieldName":"DOCNO","DisplayName":"Number","DWFieldType":"Text","Length":20,"NotEmpty":true,"Scope":"User","TableFieldColumns":[]},
              {"DBFieldName":"DOCDATE","DisplayName":"Date","DWFieldType":"Date","Length":-1,"NotEmpty":false,"Scope":"User","TableFieldColumns":[]},
              {"DBFieldName":"ITEMS","DisplayName":"Items","DWFieldType":"Table","Length":-1,"NotEmpty":false,"Scope":"User","TableFieldColumns":[
                {"DBFieldName":"QTY","DisplayName":"Quantity","DWFieldType":"Numeric","Length":-1,"NotEmpty":false,"Scope":"User"}]}]}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult((request.Method.Method, request.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/DocuWare/Platform/Home/IdentityServiceInfo") => Json("""{"IdentityServiceUrl":"https://login.example/org/"}"""),
                ("GET", "/org/.well-known/openid-configuration") => Json("""{"token_endpoint":"https://login.example/org/connect/token"}"""),
                ("POST", "/org/connect/token") => Json("""{"access_token":"token-1","token_type":"Bearer","expires_in":3600}"""),
                ("GET", "/DocuWare/Platform/FileCabinets") => Json("""{"FileCabinet":[{"Id":"c-1","Name":"Invoices","IsBasket":false}]}"""),
                ("GET", "/DocuWare/Platform/FileCabinets/c-1") => Json(Cabinet),
                _ => Json("""{"Message":"not in the fake"}""", HttpStatusCode.NotFound),
            });

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static readonly ConnectionOptions DocuWare = new()
    {
        Id = "docuware", Kind = "docuware", Settings = { ["Url"] = "https://dms.example", ["UserName"] = "integration" }, Secret = "pw",
    };

    private static (ConnectorRegistry Registry, SourceRuntime Runtime) WithFakeDocuWare()
    {
        var options = new BDCOptions { Connections = { DocuWare } };
        var client = new DocuWareClient("https://dms.example", "integration", "pw", new FakeDocuWare());
        var registry = ConnectorRegistry.BuiltIn();
        registry.RegisterDestination("docuware", rt => new DocuWareDestinationKind(rt, _ => client));
        return (registry, new SourceRuntime(options, NullLogger.Instance));
    }

    [Fact]
    public void EveryDestinationKindIsListedWithItsSettingsAndWhetherItIsConnected()
    {
        var options = new BDCOptions
        {
            Connections = { new ConnectionOptions { Id = "storage", Kind = "azure-storage", Secret = "UseDevelopmentStorage=true" } },
        };
        var list = DestinationCatalog.Describe(ConnectorRegistry.BuiltIn(), new SourceRuntime(options, NullLogger.Instance));

        Assert.Equal(new[] { "sharepoint", "docuware", "blob", "https", "servicebus" }, list.Select(d => d.Kind));
        var docuWare = list.Single(d => d.Kind == "docuware");
        Assert.False(docuWare.Connected);
        Assert.Equal(new[] { "FileCabinet", "MatchField" }, docuWare.Descriptor.Settings.Select(s => s.Key));
        Assert.Equal("FileCabinet", docuWare.Descriptor.Settings.Single(s => s.Key == "MatchField").DependsOn);
        Assert.False(docuWare.Descriptor.Settings.Single(s => s.Key == "MatchField").Required);
        var blob = list.Single(d => d.Kind == "blob");
        Assert.True(blob.Connected);
        Assert.Null(blob.Descriptor.FieldsLookup);
        Assert.Equal(new[] { "Container", "Prefix", "Status" }, blob.Descriptor.Settings.Select(s => s.Key));
        var https = list.Single(d => d.Kind == "https");
        Assert.False(https.Connected);
        Assert.Equal(new[] { "Path" }, https.Descriptor.Settings.Select(s => s.Key));
    }

    [Fact]
    public async Task DocuWareListsItsCabinetsAndOneRowPerFieldWithItsKind()
    {
        var (registry, runtime) = WithFakeDocuWare();
        var target = new TargetOptions { Kind = "docuware", Connection = "docuware", Settings = { ["FileCabinet"] = "c-1" } };

        var cabinets = await DestinationCatalog.ChoicesAsync(registry, runtime, "docuware", "cabinets", target);
        var fields = await DestinationCatalog.ChoicesAsync(registry, runtime, "docuware", "fields", target);
        var match = await DestinationCatalog.ChoicesAsync(registry, runtime, "docuware", "matchfields", target);

        Assert.Equal("Invoices", Assert.Single(cabinets.Choices).Label);
        Assert.Equal(new[] { "DOCNO", "DOCDATE", "ITEMS.QTY" }, fields.Choices.Select(c => c.Value));
        Assert.Equal("Number (required)", fields.Choices[0].Label);
        Assert.Equal("date", fields.Choices[1].Detail);
        Assert.Equal("number", fields.Choices[2].Detail);
        Assert.Equal(new[] { "DOCNO" }, match.Choices.Select(c => c.Value));
    }

    [Fact]
    public async Task AYesNoSettingAndAnUnknownKindAnswerWithoutAnySystem()
    {
        var runtime = new SourceRuntime(new BDCOptions(), NullLogger.Instance);

        var yesNo = await DestinationCatalog.ChoicesAsync(ConnectorRegistry.BuiltIn(), runtime, "blob", "yesno", new TargetOptions { Kind = "blob" });
        var unknown = await DestinationCatalog.ChoicesAsync(ConnectorRegistry.BuiltIn(), runtime, "shareflex-x", "x", new TargetOptions());
        var noConnection = await DestinationCatalog.ChoicesAsync(ConnectorRegistry.BuiltIn(), runtime, "docuware", "cabinets", new TargetOptions { Kind = "docuware" });

        Assert.Equal(new[] { "true", "false" }, yesNo.Choices.Select(c => c.Value));
        Assert.Contains("not known", unknown.Error);
        Assert.Contains("Connections", noConnection.Error);
    }

    [Fact]
    public void EveryConnectionKindIsListedWithItsSettingsAndSecret()
    {
        var kinds = ConnectionCatalog.Describe(ConnectorRegistry.BuiltIn());

        var docuWare = kinds.Single(k => k.Kind == "docuware");
        Assert.Equal(new[] { "Url", "UserName" }, docuWare.Settings.Select(s => s.Key));
        Assert.Equal("Password", docuWare.SecretLabel);
        var storage = kinds.Single(k => k.Kind == "azure-storage");
        Assert.Equal(new[] { "AccountUrl" }, storage.Settings.Select(s => s.Key));
        Assert.False(storage.Settings[0].Required);
    }

    [Fact]
    public void ABlankSecretOnTheFormKeepsTheStoredOne()
    {
        var stored = new BDCOptions { Connections = { DocuWare } };

        var kept = ConnectionCatalog.Draft(stored, "docuware", "docuware", new() { ["Url"] = "https://dms.example", ["UserName"] = "other" }, "");
        var typed = ConnectionCatalog.Draft(stored, "docuware", "docuware", new() { ["Url"] = "https://dms.example", ["UserName"] = "other" }, "new");
        var fresh = ConnectionCatalog.Draft(stored, "dms-2", "docuware", new() { ["Url"] = "https://dms.example" }, "");

        Assert.Equal("pw", kept.Secret);
        Assert.Equal("other", kept.Setting("UserName"));
        Assert.Equal("new", typed.Secret);
        Assert.Equal("", fresh.Secret);
    }

    [Fact]
    public async Task AConnectionThatCannotSignInSaysWhyBeforeAnyCall()
    {
        var draft = new ConnectionOptions { Id = "docuware", Kind = "docuware", Settings = { ["Url"] = "http://dms.example", ["UserName"] = "u" }, Secret = "pw" };

        var result = await ConnectionCatalog.TestAsync(ConnectorRegistry.BuiltIn(), draft);

        Assert.False(result.Ok);
        Assert.Contains("https://", result.Message);
    }
}