using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure cases 51 to 53 of the product design (part 14): the web console served by the Azure web host.</summary>
public sealed class WebConsoleTests : IAsyncLifetime
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-console-" + Guid.NewGuid());
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private AzureWebHost _host = null!;

    private sealed class Monitor(BDCOptions value) : IOptionsMonitor<BDCOptions>
    {
        public BDCOptions CurrentValue => value;
        public BDCOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<BDCOptions, string?> listener) => null;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var settings = new MemoryETagBlob();
        settings.Put("""{ "HiveBDC": { "PollSeconds": 30 } }""");
        var tenant = new InstanceTenant(() => Jwt(Tenant));
        var api = new LocalStatusServer(new Monitor(new BDCOptions { PollSeconds = 30 }), new TableStateStore(new MemoryTableRows(), NullLogger.Instance),
            NullLogger<LocalStatusServer>.Instance, null, "9.9.9", Path.Combine(_root, "unused.json"), _root,
            new BlobSettingsDocument(settings), new BlobLicenseStore(new MemoryETagBlob()), tenant);
        _host = new AzureWebHost(api, NullLogger.Instance, "9.9.9", tenant, () => true, new IPEndPoint(IPAddress.Loopback, 0));
        await _host.StartAsync(CancellationToken.None);
        for (int i = 0; i < 100 && _host.BoundPort == 0; i++) await Task.Delay(50);
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

    private async Task<HttpResponseMessage> Get(string path, string? principal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_host.BoundPort}{path}");
        if (principal is not null) request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL", principal);
        return await _http.SendAsync(request);
    }

    [Fact]
    public async Task AViewerGetsThePageWithItsSecurityHeaders()
    {
        using var page = await Get("/", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        string csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Contains("/console/app.js", await page.Content.ReadAsStringAsync());

        using var script = await Get("/console/app.js", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        using var style = await Get("/console/app.css", Principal(ApiRoles.Viewer));
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("", HttpStatusCode.Forbidden)]
    public async Task TheConsoleNeedsSignInAndARole(string? role, HttpStatusCode expected)
    {
        string? principal = role is null ? null : role.Length == 0 ? Principal() : Principal(role);
        using var page = await Get("/", principal);
        Assert.Equal(expected, page.StatusCode);
        using var script = await Get("/console/app.js", principal);
        Assert.Equal(expected, script.StatusCode);
    }

    [Theory]
    [InlineData("/console/../appsettings.json")]
    [InlineData("/console/%2e%2e/appsettings.json")]
    [InlineData("/console/unknown.js")]
    [InlineData("/console/")]
    [InlineData("/console/App.JS.map")]
    public async Task OnlyTheEmbeddedFilesAreServed(string path)
    {
        using var answer = await Get(path, Principal(ApiRoles.Administrator));
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Theory]
    [InlineData("/console/logo.svg", "image/svg+xml")]
    [InlineData("/console/dm-sans.ttf", "font/ttf")]
    [InlineData("/console/manrope.ttf", "font/ttf")]
    [InlineData("/console/dm-sans-OFL.txt", "text/plain")]
    [InlineData("/console/manrope-OFL.txt", "text/plain")]
    public async Task TheLogoTheFontsAndTheirLicencesComeFromTheInstance(string path, string type)
    {
        using var answer = await Get(path, Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(type, answer.Content.Headers.ContentType?.MediaType);
        Assert.True((await answer.Content.ReadAsByteArrayAsync()).Length > 1000);
    }

    [Fact]
    public void NothingOfThePageLoadsFromOutsideTheInstance()
    {
        string css = ConsoleFiles.Text("app.css");
        Assert.DoesNotMatch(new Regex(@"url\(\s*(?!['""]?/console/)", RegexOptions.IgnoreCase), css);
        Assert.DoesNotMatch(new Regex(@"@import", RegexOptions.IgnoreCase), css);
        Assert.Contains("url(\"/console/dm-sans.ttf\")", css);
        Assert.Contains("url(\"/console/manrope.ttf\")", css);
        string page = ConsoleFiles.Text("index.html");
        foreach (Match m in Regex.Matches(page, @"\b(?:src|href)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase))
        {
            string target = m.Groups[1].Value;
            Assert.True(target.StartsWith("/console/", StringComparison.Ordinal) || target.StartsWith('#') || target == "/.auth/logout", $"index.html loads {target}");
        }
        Assert.DoesNotContain("<script", ConsoleFiles.Text("logo.svg"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MeNamesTheUserAndTheRoles()
    {
        using var answer = await Get("/me", Principal(ApiRoles.Operator));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var me = JsonNode.Parse(await answer.Content.ReadAsStringAsync())!;
        Assert.Equal("anna@contoso.example", me["name"]!.GetValue<string>());
        Assert.Equal(new[] { "Operator" }, me["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.True(me["canRetry"]!.GetValue<bool>());
        Assert.False(me["canChange"]!.GetValue<bool>());
        Assert.Equal("9.9.9", me["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheApiStillAnswersBesideTheConsole()
    {
        using var status = await Get("/status", Principal(ApiRoles.Viewer));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("application/json", status.Content.Headers.ContentType?.MediaType);
        using var health = await Get("/healthz", null);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public void TheConsoleFilesSetNoHtmlFromDataAndRunNoInlineScript()
    {
        foreach (var name in ConsoleFiles.Names)
        {
            string text = ConsoleFiles.Text(name);
            if (name.EndsWith(".js", StringComparison.Ordinal))
                foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
                    Assert.False(text.Contains(sink, StringComparison.Ordinal), $"{name} uses {sink}");
            if (name.EndsWith(".html", StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), text);
                Assert.DoesNotMatch(new Regex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase), text);
                Assert.DoesNotMatch(new Regex(@"\sstyle\s*=", RegexOptions.IgnoreCase), text);
            }
        }
        // replaceChildren prints "null" for a skipped element; only put() in ui.js calls it, after dropping empty children.
        var calls = ConsoleFiles.Names.Where(n => n.EndsWith(".js", StringComparison.Ordinal))
            .SelectMany(n => Regex.Matches(ConsoleFiles.Text(n), @"\.replaceChildren\(").Select(_ => n)).ToList();
        Assert.Equal(new[] { "ui.js" }, calls);
        foreach (var name in new[] { "index.html", "app.js", "logic.js", "ui.js", "setup.js", "app.css" })
            Assert.Contains(name, ConsoleFiles.Names);
        // The page sets styles only through classes: an inline style is refused by the Content Security Policy.
        foreach (var name in ConsoleFiles.Names.Where(n => n.EndsWith(".js", StringComparison.Ordinal)))
            Assert.DoesNotMatch(new Regex(@"['""]style['""]\s*:|\.style\b|setAttribute\(\s*['""]style"), ConsoleFiles.Text(name));
    }

    [Fact]
    public void AConnectionCardBeingEditedTakesTheWholeRow()
    {
        Assert.Matches(new Regex(@"\.conn-card-editing\s*\{\s*grid-column:\s*1\s*/\s*-1;"), ConsoleFiles.Text("app.css"));
        Assert.Equal(3, Regex.Matches(ConsoleFiles.Text("connections.js"), @"class: cardClass\([^)]*, editing\)").Count);
    }
}