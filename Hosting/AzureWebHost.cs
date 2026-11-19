using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>The app roles of an Azure instance (app roles of the instance's Entra app registration).</summary>
public static class ApiRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Administrator = "Administrator";

    /// <summary>Reading needs Viewer, a retry Operator, every other change Administrator (failure case 28).</summary>
    public static string Needed(ApiCall call)
        => call.Method == "GET" ? Viewer : call.Path == "/history/retry" ? Operator : Administrator;

    public static bool Allows(IReadOnlyCollection<string> roles, string needed)
    {
        bool Has(string role) => roles.Contains(role, StringComparer.OrdinalIgnoreCase);
        return needed switch
        {
            Viewer => Has(Viewer) || Has(Operator) || Has(Administrator),
            Operator => Has(Operator) || Has(Administrator),
            _ => Has(Administrator),
        };
    }
}

/// <summary>
/// The signed-in user as App Service authentication hands it to the app: the X-MS-CLIENT-PRINCIPAL header, Base64 JSON
/// with auth_typ, name_typ, role_typ and claims. External requests cannot set the header; only App Service does.
/// </summary>
public sealed record AppServicePrincipal(string Name, string? TenantId, IReadOnlyList<string> Roles)
{
    private const string LongRoleClaim = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
    private const string LongTenantClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static AppServicePrincipal? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(header.Trim()));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "auth_typ").Length == 0
                || !root.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                return null;
            string roleType = Text(root, "role_typ"), nameType = Text(root, "name_typ");
            var list = claims.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object)
                .Select(c => (Type: Text(c, "typ"), Value: Text(c, "val")))
                .Where(c => c.Value.Length > 0)
                .ToList();
            var roles = list.Where(c => c.Type == "roles" || c.Type == LongRoleClaim || (roleType.Length > 0 && c.Type == roleType))
                .Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string name = list.FirstOrDefault(c => (nameType.Length > 0 && c.Type == nameType) || c.Type is "name" or "preferred_username").Value ?? "";
            string? tenant = list.FirstOrDefault(c => c.Type is "tid" or LongTenantClaim).Value;
            return new AppServicePrincipal(name.Length > 0 ? name : "signed-in user", tenant, roles);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>
/// The web host of an Azure instance: Kestrel on the port App Service gives (PORT, default 8080), behind App Service
/// authentication. /healthz answers without sign-in; everything else is served only while App Service authentication is
/// on (failure case 25: never without sign-in), to a signed-in user of the instance's tenant with the role the call
/// needs (case 28). The calls themselves are the same handlers as the local API (<see cref="LocalStatusServer.Route"/>).
/// Every change is logged with the user who made it.
/// </summary>
public sealed class AzureWebHost : BackgroundService
{
    private readonly LocalStatusServer _api;
    private readonly ILogger _log;
    private readonly InstanceTenant? _tenant;
    private readonly Func<bool> _signInEnabled;
    private readonly IPEndPoint _listen;
    private readonly string _version;
    private readonly Func<BDCOptions> _options;
    private readonly HttpsInbound _inbound;
    private readonly ConsoleSite _console;
    private bool _warnedSignInOff;

    public AzureWebHost(LocalStatusServer api, ILogger log, string version, InstanceTenant? tenant = null,
        Func<bool>? signInEnabled = null, IPEndPoint? listen = null, Func<BDCOptions>? options = null, ConsoleSite? console = null)
    {
        _api = api;
        _console = console ?? new ConsoleSite(ConsoleSite.DefaultRoot);
        _options = options ?? (() => new BDCOptions());
        _inbound = new HttpsInbound(log);
        _log = log;
        _version = version;
        _tenant = tenant;
        _signInEnabled = signInEnabled ?? (() => string.Equals(Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED"), "True", StringComparison.OrdinalIgnoreCase));
        _listen = listen ?? new IPEndPoint(IPAddress.Any, int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port) ? port : 8080);
    }

    /// <summary>The port Kestrel bound, once started (tests ask for port 0).</summary>
    public int BoundPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WebApplication app;
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
            {
                k.Listen(_listen);
                k.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
            });
            app = builder.Build();
            app.Run(Serve);
            await app.StartAsync(stoppingToken);
            BoundPort = new Uri(app.Urls.First().Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1")).Port;
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "The web host could not start on {Endpoint}.", _listen);
            return;
        }
        _log.LogInformation("Web host listening on port {Port}; sign-in through App Service authentication is {State}.",
            BoundPort, _signInEnabled() ? "on" : "OFF, so nothing but /healthz is served");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
    }

    private async Task Serve(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers["Content-Security-Policy"] = _console.PolicyFor("");
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "same-origin";   // App Service refuses a signed-in POST without a same-origin Referer
        headers["Cache-Control"] = "no-store";
        string path = (http.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        if (path == "/healthz")
        {
            await Write(http, 200, new { status = "ok", version = _version });
            return;
        }
        // The HTTPS source: a machine caller with the key of the company's https connection, not a signed-in user.
        if (path.StartsWith("/inbound/", StringComparison.Ordinal) && HttpMethods.IsPost(http.Request.Method))
        {
            var parts = (http.Request.Path.Value ?? "").Trim('/').Split('/');
            if (parts.Length != 3)
            {
                await Write(http, 404, new { error = "Use POST /inbound/{documentType}/{company}." });
                return;
            }
            InboundAnswer answer;
            try
            {
                answer = _inbound.Receive(_options(), Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]),
                    name => http.Request.Headers[name].ToString() is { Length: > 0 } v ? v : null,
                    http.Request.Query["id"].ToString() is { Length: > 0 } id ? id : null,
                    http.Request.ContentLength,
                    () => new StreamReader(http.Request.Body, Encoding.UTF8).ReadToEndAsync(http.RequestAborted).GetAwaiter().GetResult());
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
            {
                answer = new InboundAnswer(413, new { error = $"The document is larger than {HttpsInbound.MaxBytes / (1024 * 1024)} MB." });
            }
            await Write(http, answer.Status, answer.Body);
            return;
        }
        if (!_signInEnabled())
        {
            if (!_warnedSignInOff)
            {
                _warnedSignInOff = true;
                _log.LogCritical("App Service authentication is off; the web host serves nothing but /healthz until it is on.");
            }
            await Write(http, 503, new { error = "Sign-in is not set up for this instance, so nothing is served." });
            return;
        }
        var user = AppServicePrincipal.Parse(http.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString());
        if (user is null)
        {
            await Write(http, 401, new { error = "Sign in first." });
            return;
        }
        if (user.TenantId is { } userTenant && _tenant is not null && _tenant.Resolve(out var instanceTenant, out _)
            && !string.Equals(userTenant, instanceTenant, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Web host: {User} from tenant {Tenant} was refused; this instance belongs to {Instance}.", LocalStatusServer.ForLog(user.Name), userTenant, instanceTenant);
            await Write(http, 403, new { error = "This instance belongs to another organisation." });
            return;
        }

        // Pages and the API share names (/history/ and /history), so the API answers only under /api/ (design
        // 2026-11-19-web-console-nextjs.md, decision 3a); everything else is a file of the console or not found.
        string raw = http.Request.Path.Value ?? "";
        if (!raw.Equals("/api", StringComparison.Ordinal) && !raw.StartsWith("/api/", StringComparison.Ordinal))
        {
            await ServeConsole(http, user, raw);
            return;
        }
        string apiPath = raw.Length <= 4 ? "/" : raw[4..];
        path = apiPath.TrimEnd('/').ToLowerInvariant();

        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(http.RequestAborted);
        var query = http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var call = new ApiCall(http.Request.Method, apiPath, query, body) { User = user.Name };
        string needed = ApiRoles.Needed(call);
        if (!ApiRoles.Allows(user.Roles, needed))
        {
            _log.LogWarning("Web host: {User} has no role for {Method} {Path} (needs {Role}).", LocalStatusServer.ForLog(user.Name), call.Method, LocalStatusServer.ForLog(call.Path), needed);
            await Write(http, 403, new { error = $"This needs the role {needed}." });
            return;
        }

        if (HttpMethods.IsGet(http.Request.Method))
        {
            // Who is signed in, for the console's account menu and its role checks.
            if (path == "/me")
            {
                await Write(http, 200, new
                {
                    name = user.Name,
                    roles = user.Roles,
                    canRetry = ApiRoles.Allows(user.Roles, ApiRoles.Operator),
                    canChange = ApiRoles.Allows(user.Roles, ApiRoles.Administrator),
                    version = _version,
                });
                return;
            }
        }

        await _api.Route(call);
        if (call.Method != "GET")
            _log.LogInformation("Web host: {User} {Method} {Path} answered {Status}.", LocalStatusServer.ForLog(user.Name), call.Method, LocalStatusServer.ForLog(call.Path), call.Status);
        http.Response.StatusCode = call.Status;
        http.Response.ContentType = "application/json";
        http.Response.ContentLength = call.Answer.Length;
        await http.Response.Body.WriteAsync(call.Answer, http.RequestAborted);
    }

    /// <summary>
    /// A page or file of the web console for GET and HEAD (Next.js prefetches with HEAD) to a user with any role; an
    /// unknown path gets the console's 404 page. The page carries its own content security policy.
    /// </summary>
    private async Task ServeConsole(HttpContext http, AppServicePrincipal user, string raw)
    {
        bool head = HttpMethods.IsHead(http.Request.Method);
        if (!HttpMethods.IsGet(http.Request.Method) && !head)
        {
            await Write(http, 404, new { error = "Not found. The API answers under /api/." });
            return;
        }
        if (!ApiRoles.Allows(user.Roles, ApiRoles.Viewer))
        {
            await Write(http, 403, new { error = $"This needs the role {ApiRoles.Viewer}." });
            return;
        }
        if (!_console.Available)
        {
            await Write(http, 404, new { error = "This package carries no web console." });
            return;
        }
        int status = 200;
        if (!_console.TryResolve(raw, out var name))
        {
            status = 404;
            if (!_console.TryResolve("/404.html", out name))
            {
                await Write(http, 404, new { error = "Not found." });
                return;
            }
        }
        byte[] content = _console.Content(name);
        http.Response.StatusCode = status;
        http.Response.ContentType = ConsoleSite.ContentType(name);
        http.Response.Headers["Content-Security-Policy"] = _console.PolicyFor(name);
        http.Response.Headers["Cache-Control"] = ConsoleSite.CacheControlFor(name);
        http.Response.ContentLength = content.Length;
        if (!head) await http.Response.Body.WriteAsync(content, http.RequestAborted);
    }

    private static async Task Write(HttpContext http, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        http.Response.ContentLength = bytes.Length;
        await http.Response.Body.WriteAsync(bytes, http.RequestAborted);
    }
}