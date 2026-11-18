using System.Text;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// The web console of an Azure instance: the files under Hosting/Console, embedded in the service. Only these names
/// are served (failure case 53), so no path can reach another file of the package.
/// </summary>
public static class ConsoleFiles
{
    private const string Prefix = "Balsoft.Hive.BDC.Console.";

    /// <summary>Script and style only from this instance, no inline code, never inside a frame (failure case 52).</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
        + "font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Files = new(Load);

    public static IReadOnlyCollection<string> Names => Files.Value.Keys.ToList();

    public static bool TryGet(string name, out byte[] content)
    {
        if (Files.Value.TryGetValue(name, out var found))
        {
            content = found;
            return true;
        }
        content = [];
        return false;
    }

    public static string Text(string name) => Encoding.UTF8.GetString(Files.Value[name]);

    public static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static IReadOnlyDictionary<string, byte[]> Load()
    {
        var assembly = typeof(ConsoleFiles).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[resource[Prefix.Length..]] = buffer.ToArray();
        }
        return files;
    }
}