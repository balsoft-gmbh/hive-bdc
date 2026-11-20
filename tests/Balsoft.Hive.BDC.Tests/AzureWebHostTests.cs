using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The web host of an Azure instance behind App Service authentication (failure cases 25 and 28).</summary>
public sealed class AzureWebHostTests : IAsyncLifetime
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-web-" + Guid.NewGuid());
    private readonly HttpClient _http = new();
    private readonly MemoryETagBlob _settings = new();
    private bool _signInOn = true;
    private AzureWebHost _host = null!;

    private sealed class NoFeed : IUpdateFeed
    {
        public Task<UpdateManifest> Latest(CancellationToken ct) => throw new HttpRequestException("No feed in this test.");
        public Task<Stream> Open(string url, CancellationToken ct) => throw new HttpRequestException("No feed in this test.");
    }

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _settings.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        var options = new BDCOptions { PollSeconds = 30 };
        options.DocumentTypes.Add(new DocumentTypeOptions { Id = "SalesInvoice" });
        var tenant = new InstanceTenant(() => Jwt(Tenant));
        var api = new LocalStatusServer(new Monitor(options), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            NullLogger<LocalStatusServer>.Instance, null, "9.9.9", Path.Combine(_root, "unused.json"), _root,
            new BlobSettingsDocument(_settings), new BlobLicenseStore(new MemoryETagBlob()), tenant);
        api.Updates = new SelfUpdate(new UpdatePlaces(_root), new NoFeed(), "9.9.9", null, () => null, () => { }, TimeProvider.System,
            NullLogger.Instance);
        _host = new AzureWebHost(api, NullLogger.Instance, "9.9.9", tenant, () => _signInOn, new IPEndPoint(IPAddress.Loopback, 0));
        await _host.StartAsync(CancellationToken.None);
        for (int i = 0; i < 600 && _host.BoundPort == 0; i++) await Task.Delay(50);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Jwt(string tenant)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Part("{}") + "." + Part($$"""{"tid":"{{tenant}}"}""") + ".c2ln";
    }

    private static string Principal(string? tenant = Tenant, string roleType = "roles", params string[] roles)
    {
        var claims = new JsonArray { new JsonObject { ["typ"] = "name", ["val"] = "anna@contoso.example" } };
        if (tenant is not null) claims.Add(new JsonObject { ["typ"] = "http://schemas.microsoft.com/identity/claims/tenantid", ["val"] = tenant });
        foreach (var role in roles) claims.Add(new JsonObject { ["typ"] = roleType, ["val"] = role });
        var principal = new JsonObject { ["auth_typ"] = "aad", ["name_typ"] = "name", ["role_typ"] = roleType, ["claims"] = claims };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(principal.ToJsonString()));
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? principal, string? body = null)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_host.BoundPort}{path}");
        if (principal is not null) request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL", principal);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task HealthAnswersWithoutSignInAndNothingElseDoes()
    {
        var health = await Call(HttpMethod.Get, "/healthz", null);
        Assert.Equal((HttpStatusCode.OK, "ok", "9.9.9"), (health.Status, (string)health.Body!["status"]!, (string)health.Body["version"]!));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/status", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/status", "not base64 json")).Status);
    }

    [Fact]
    public async Task WithoutAppServiceSignInNothingButHealthIsServedEvenWithAHeader()
    {
        _signInOn = false;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Call(HttpMethod.Get, "/api/status", Principal(roles: ApiRoles.Administrator))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/healthz", null)).Status);
    }

    [Fact]
    public async Task AViewerReadsButCannotChangeOrRetry()
    {
        string viewer = Principal(roles: ApiRoles.Viewer);

        var status = await Call(HttpMethod.Get, "/api/status", viewer);
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal("9.9.9", (string)status.Body!["version"]!);

        var change = await Call(HttpMethod.Post, "/api/config", viewer, "{}");
        Assert.Equal((HttpStatusCode.Forbidden, "This needs the role Administrator."), (change.Status, (string)change.Body!["error"]!));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/history/retry", viewer, "{}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/status", Principal())).Status);
    }

    [Fact]
    public async Task AnOperatorMayRetryButNotChangeSettings()
    {
        string op = Principal(roles: ApiRoles.Operator);

        Assert.NotEqual(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/history/retry", op, """{"documentId":"none"}""")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/license", op, """{"content":"x"}""")).Status);
    }

    [Fact]
    public async Task AnAdministratorSavesTheSettingsIntoTheBlob()
    {
        string admin = Principal(roleType: "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", roles: ApiRoles.Administrator);
        var current = (await Call(HttpMethod.Get, "/api/config", admin)).Body!.AsObject();
        current["pollSeconds"] = 45;

        var saved = await Call(HttpMethod.Post, "/api/config", admin, current.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal(45, (int)JsonNode.Parse(_settings.Text!)!["HiveBDC"]!["PollSeconds"]!);
    }

    [Fact]
    public async Task AUserOfAnotherTenantIsRefused()
    {
        var other = await Call(HttpMethod.Get, "/api/status", Principal(tenant: "99999999-9999-9999-9999-999999999999", roles: ApiRoles.Administrator));

        Assert.Equal((HttpStatusCode.Forbidden, "This instance belongs to another organisation."), (other.Status, (string)other.Body!["error"]!));
    }

    [Fact]
    public async Task TheUpdatePageOfAnOlderVersionStillReachesTheUpdateView()
    {
        // A page of 1.8.1 or older polls /service/updates during the restart and reloads once another version answers
        // (design 2026-11-19-update-page-after-restart.md, U1 and U4).
        string viewer = Principal(roles: ApiRoles.Viewer);
        var current = await Call(HttpMethod.Get, "/api/service/updates", viewer);
        var older = await Call(HttpMethod.Get, "/service/updates", viewer);

        Assert.Equal((HttpStatusCode.OK, "9.9.9"), (current.Status, (string)current.Body!["version"]!));
        Assert.Equal((current.Status, current.Body.ToJsonString()), (older.Status, older.Body!.ToJsonString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/service/updates", null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/service/updates", Principal())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/service/updates/run", viewer, "{}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/service/updates/check", viewer, "{}")).Status);
        Assert.Null((await Call(HttpMethod.Get, "/status", viewer)).Body?["version"]);
    }

    [Theory]
    [InlineData("""{"claims":[]}""")]
    [InlineData("""{"auth_typ":"aad"}""")]
    [InlineData("""[1,2]""")]
    public void APrincipalWithoutProviderOrClaimsIsNoUser(string json)
        => Assert.Null(AppServicePrincipal.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(json))));

    [Fact]
    public void ThePrincipalNamesTheUserTenantAndRoles()
    {
        var user = AppServicePrincipal.Parse(Principal(roles: new[] { ApiRoles.Viewer, ApiRoles.Operator }))!;

        Assert.Equal(("anna@contoso.example", Tenant), (user.Name, user.TenantId));
        Assert.Equal(new[] { ApiRoles.Viewer, ApiRoles.Operator }, user.Roles);
    }
}