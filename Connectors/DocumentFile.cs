namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// A produced file as targets see it: a name, a content type and a way to read it. Targets never assume the file lies
/// on a local disk, so the same target works where the output only exists in storage or in memory.
/// </summary>
public sealed record DocumentFile(string Name, string ContentType, Func<Stream> Open)
{
    public static DocumentFile FromPath(string path)
        => new(Path.GetFileName(path), ContentTypeFor(path), () => File.OpenRead(path));

    public static DocumentFile FromBytes(string name, byte[] content)
        => new(name, ContentTypeFor(name), () => new MemoryStream(content, writable: false));

    public byte[] ReadAllBytes()
    {
        using var source = Open();
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".xml" => "application/xml",
        ".csv" => "text/csv",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };
}