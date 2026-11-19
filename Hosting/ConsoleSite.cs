using System.Text;
using System.Text.Json;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// The web console of an Azure instance: the static export of <c>web/</c> (Next.js), shipped in the package's
/// <c>console/</c> folder. The files are read once at start and only those names are served, so no path can reach
/// another file of the package (failure case 3 of the console design). Each page gets a content security policy with
/// the hashes of its own inline scripts from <c>csp.json</c>, written at build time (failure case 1).
/// </summary>
public sealed class ConsoleSite
{
    /// <summary>
    /// Scripts only from the instance or with a listed hash; styles may be inline because Fluent UI writes them at run
    /// time (a style runs no code); never inside a frame. {hashes} takes the page's hashes.
    /// </summary>
    public const string PolicyTemplate =
        "default-src 'self'; script-src 'self'{hashes}; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; "
        + "font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _policies = new(StringComparer.Ordinal);

    /// <summary>The console folder next to the service in the Azure package.</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "console");

    public ConsoleSite(string root)
    {
        if (Directory.Exists(root))
        {
            string full = Path.GetFullPath(root);
            foreach (var path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                _files[Path.GetRelativePath(full, path).Replace('\\', '/')] = File.ReadAllBytes(path);
        }
        if (_files.TryGetValue("csp.json", out var csp))
        {
            using var doc = JsonDocument.Parse(csp);
            foreach (var page in doc.RootElement.EnumerateObject())
            {
                var hashes = page.Value.EnumerateArray().Select(h => h.GetString() ?? "").Where(IsHash);
                _policies[page.Name] = PolicyTemplate.Replace("{hashes}", string.Concat(hashes.Select(h => " " + h)));
            }
        }
    }

    /// <summary>Whether this package carries a console at all (the Windows build does not).</summary>
    public bool Available => _files.ContainsKey("index.html");

    public IReadOnlyCollection<string> Names => _files.Keys;

    /// <summary>
    /// The file a request path names: "/" is index.html, "/history/" history/index.html, "/history" also
    /// history/index.html, a file name itself. Anything else, and any path with "." or ".." segments, is no file.
    /// </summary>
    public bool TryResolve(string requestPath, out string name)
    {
        name = "";
        string path = requestPath.TrimStart('/');
        if (path.Contains('\\') || path.Contains(':') || path.Split('/').Any(s => s is "." or ".."))
            return false;
        string[] candidates = path.Length == 0 ? ["index.html"]
            : path.EndsWith('/') ? [path + "index.html"]
            : [path, path + "/index.html", path + ".html"];
        foreach (var candidate in candidates)
        {
            if (_files.ContainsKey(candidate))
            {
                name = candidate;
                return true;
            }
        }
        return false;
    }

    public byte[] Content(string name) => _files[name];

    public string Text(string name) => Encoding.UTF8.GetString(_files[name]);

    /// <summary>The page's own policy; a file without inline scripts, or not listed, gets the policy without hashes.</summary>
    public string PolicyFor(string name)
        => _policies.TryGetValue(name, out var policy) ? policy : PolicyTemplate.Replace("{hashes}", "");

    /// <summary>Content-hashed build files may stay in the browser; everything else is read fresh (failure case 12).</summary>
    public static string CacheControlFor(string name)
        => name.StartsWith("_next/static/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-store";

    public static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".ttf" => "font/ttf",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static bool IsHash(string value)
        => value.StartsWith("'sha256-", StringComparison.Ordinal) && value.EndsWith('\'') && value.Length < 100
           && value[8..^1].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=');
}
