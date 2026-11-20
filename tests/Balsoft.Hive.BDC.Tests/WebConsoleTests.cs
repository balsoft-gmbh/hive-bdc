using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The web console served by the Azure web host from the package's console/ folder (design
/// 2026-11-19-web-console-nextjs.md, failure cases 1, 2, 3, 3b, 4 and 12), with a small fixture export in place of the
/// Next.js build.
/// </summary>
public sealed class WebConsoleTests : IAsyncLifetime
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string InlineScript = "self.__next_f=self.__next_f||[]";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-console-" + Guid.NewGuid());
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private AzureWebHost _host = null!;

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    private static string Hash(string script) => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    private string Console => Path.Combine(_root, "console");

    private void WriteExport()
    {
        void Put(string name, string text)
        {
            string path = Path.Combine(Console, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Put("index.html", $"<!DOCTYPE html><html><head><script>{InlineScript}</script><script src=\"/_next/static/chunks/main.js\"></script></head><body>Overview page</body></html>");
        Put("history/index.html", "<!DOCTYPE html><html><body>History page</body></html>");
        Put("history/index.txt", "1:history payload");
        Put("404.html", "<!DOCTYPE html><html><body>This page does not exist</body></html>");
        Put("_next/static/chunks/main.js", "console.log('main')");
        Put("fonts/dm-sans.ttf", new string('f', 2000));
        Put("csp.json", $$"""{ "index.html": ["{{Hash(InlineScript)}}"], "history/index.html": [], "404.html": [] }""");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), """{ "secret": "do-not-serve" }""");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        WriteExport();
        _host = await Start(new ConsoleSite(Console));
    }

    private async Task<AzureWebHost> Start(ConsoleSite console)
    {
        var settings = new MemoryETagBlob();
        settings.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        var tenant = new InstanceTenant(() => Jwt(Tenant));
        var api = new LocalStatusServer(new Monitor(new BDCOptions { PollSeconds = 30 }), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            NullLogger<LocalStatusServer>.Instance, null, "9.9.9", Path.Combine(_root, "unused.json"), _root,
            new BlobSettingsDocument(settings), new BlobLicenseStore(new MemoryETagBlob()), tenant);
        var host = new AzureWebHost(api, NullLogger.Instance, "9.9.9", tenant, () => true, new IPEndPoint(IPAddress.Loopback, 0), console: console);
        await host.StartAsync(CancellationToken.None);
        for (int i = 0; i < 600 && host.BoundPort == 0; i++) await Task.Delay(50);
        return host;
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

    private static string Principal(params string[] roles)
    {
        var claims = new JsonArray
        {
            new JsonObject { ["typ"] = "name", ["val"] = "anna@contoso.example" },
            new JsonObject { ["typ"] = "http://schemas.microsoft.com/identity/claims/tenantid", ["val"] = Tenant },
        };
        foreach (var role in roles) claims.Add(new JsonObject { ["typ"] = "roles", ["val"] = role });
        var principal = new JsonObject { ["auth_typ"] = "aad", ["name_typ"] = "name", ["role_typ"] = "roles", ["claims"] = claims };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(principal.ToJsonString()));
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? principal, AzureWebHost? host = null, string? body = null)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{(host ?? _host).BoundPort}{path}");
        if (principal is not null) request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL", principal);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _http.SendAsync(request);
    }

    private Task<HttpResponseMessage> Get(string path, string? principal) => Send(HttpMethod.Get, path, principal);

    private static string ScriptSrc(HttpResponseMessage answer)
        => answer.Headers.GetValues("Content-Security-Policy").Single().Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src", StringComparison.Ordinal));

    [Fact]
    public async Task APageCarriesThePolicyWithTheHashesOfItsOwnInlineScripts()
    {
        using var page = await Get("/", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Overview page", await page.Content.ReadAsStringAsync());
        Assert.Equal($"script-src 'self' {Hash(InlineScript)}", ScriptSrc(page));
        string csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());

        using var history = await Get("/history/", Principal(ApiRoles.Viewer));
        Assert.Equal("script-src 'self'", ScriptSrc(history));
    }

    [Theory]
    [InlineData("/history/")]
    [InlineData("/history")]
    [InlineData("/history/?search=RE-1")]
    public async Task ADeepLinkServesItsPage(string path)
    {
        using var page = await Get(path, Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("History page", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnknownPathGetsTheConsolesNotFoundPage()
    {
        using var page = await Get("/no-such-page/", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("This page does not exist", await page.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/../appsettings.json")]
    [InlineData("/%2e%2e/appsettings.json")]
    [InlineData("/history/../../appsettings.json")]
    [InlineData("/fonts/..%2F..%2Fappsettings.json")]
    [InlineData("/history\\..\\..\\appsettings.json")]
    [InlineData("/appsettings.json")]
    public async Task NoPathReachesAFileOutsideTheExport(string path)
    {
        using var answer = await Get(path, Principal(ApiRoles.Administrator));
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain("do-not-serve", await answer.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/./index.html")]
    [InlineData("/../appsettings.json")]
    [InlineData("/history/../index.html")]
    [InlineData("/history\\index.html")]
    [InlineData("/C:/appsettings.json")]
    public void TheSiteRefusesDotSegmentsAndForeignSeparatorsItself(string path)
    {
        // HttpClient and Kestrel normalise some of these on the way; the site refuses them even when they arrive raw.
        Assert.False(new ConsoleSite(Console).TryResolve(path, out _));
    }

    [Fact]
    public async Task BuildFilesStayCachedAndEverythingElseIsReadFresh()
    {
        using var script = await Get("/_next/static/chunks/main.js", Principal(ApiRoles.Viewer));
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", script.Headers.CacheControl?.ToString());
        using var font = await Get("/fonts/dm-sans.ttf", Principal(ApiRoles.Viewer));
        Assert.Equal("font/ttf", font.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", font.Headers.CacheControl?.ToString());
        using var payload = await Get("/history/index.txt", Principal(ApiRoles.Viewer));
        Assert.Equal("text/plain", payload.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", payload.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task HeadAnswersLikeGetWithoutTheBody()
    {
        using var answer = await Send(HttpMethod.Head, "/history/", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.True(answer.Content.Headers.ContentLength > 0);
        Assert.Empty(await answer.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("", HttpStatusCode.Forbidden)]
    public async Task TheConsoleNeedsSignInAndARole(string? role, HttpStatusCode expected)
    {
        string? principal = role is null ? null : role.Length == 0 ? Principal() : Principal(role);
        foreach (var path in new[] { "/", "/history/", "/_next/static/chunks/main.js", "/api/status" })
        {
            using var answer = await Get(path, principal);
            Assert.Equal(expected, answer.StatusCode);
        }
    }

    [Fact]
    public async Task TheApiAnswersOnlyUnderApi()
    {
        using var status = await Get("/api/status", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("application/json", status.Content.Headers.ContentType?.MediaType);

        using var old = await Get("/status", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Contains("This page does not exist", await old.Content.ReadAsStringAsync());

        using var oldPost = await Send(HttpMethod.Post, "/config", Principal(ApiRoles.Administrator), body: "{}");
        Assert.Equal(HttpStatusCode.NotFound, oldPost.StatusCode);
        Assert.Equal("application/json", oldPost.Content.Headers.ContentType?.MediaType);

        using var viewerChange = await Send(HttpMethod.Post, "/api/config", Principal(ApiRoles.Viewer), body: "{}");
        Assert.Equal(HttpStatusCode.Forbidden, viewerChange.StatusCode);

        using var health = await Get("/healthz", null);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task MeNamesTheUserAndTheRoles()
    {
        using var answer = await Get("/api/me", Principal(ApiRoles.Operator));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var me = JsonNode.Parse(await answer.Content.ReadAsStringAsync())!;
        Assert.Equal("anna@contoso.example", me["name"]!.GetValue<string>());
        Assert.Equal(new[] { "Operator" }, me["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.True(me["canRetry"]!.GetValue<bool>());
        Assert.False(me["canChange"]!.GetValue<bool>());
        Assert.Equal("9.9.9", me["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task APackageWithoutAConsoleSaysSoInsteadOfServingAnything()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var host = await Start(new ConsoleSite(empty));
        try
        {
            using var page = await Send(HttpMethod.Get, "/", Principal(ApiRoles.Viewer), host);
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
            Assert.Contains("carries no web console", await page.Content.ReadAsStringAsync());
            using var status = await Send(HttpMethod.Get, "/api/status", Principal(ApiRoles.Viewer), host);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    [Fact]
    public void ThePolicyTakesNothingButHashesFromTheBuild()
    {
        var folder = Path.Combine(_root, "tampered");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(folder, "csp.json"),
            $$"""{ "index.html": ["'unsafe-inline'", "https://evil.example", "'sha256-abc' 'unsafe-eval'", "{{Hash("x")}}"] }""");
        var site = new ConsoleSite(folder);
        string policy = site.PolicyFor("index.html");
        Assert.Contains($"script-src 'self' {Hash("x")};", policy);
        Assert.DoesNotContain("evil", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.Equal(1, policy.Split("unsafe-inline").Length - 1);   // only the style-src one
    }

    [Fact]
    public void TheHostAndTheE2eServerSendTheSamePolicy()
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "HiveBDC.sln"))) repo = Path.GetDirectoryName(repo)!;
        // A Windows checkout may turn the file into CRLF; the policy text is the same either way.
        string serve = File.ReadAllText(Path.Combine(repo, "web", "tests", "e2e", "serve.ts")).Replace("\r\n", "\n");
        string start = serve[(serve.IndexOf("BASE_POLICY =", StringComparison.Ordinal) + "BASE_POLICY =".Length)..];
        string text = string.Concat(System.Text.RegularExpressions.Regex.Matches(start[..start.IndexOf(';' + "\n", StringComparison.Ordinal)], "\"([^\"]*)\"").Select(m => m.Groups[1].Value));
        Assert.Equal(ConsoleSite.PolicyTemplate, text);
    }
}
