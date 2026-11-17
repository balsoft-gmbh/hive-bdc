namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// One request to the service's API, independent of the web server underneath: method and path (upper and lower case,
/// no trailing slash), the query, the body as text, and the answer a handler gives (status and JSON bytes).
/// </summary>
public sealed class ApiCall(string method, string path, IReadOnlyDictionary<string, string> query, string body)
{
    public string Method { get; } = method.ToUpperInvariant();

    public string Path { get; } = path.TrimEnd('/').ToLowerInvariant();

    public string Body { get; } = body;

    public int Status { get; private set; } = 200;

    public byte[] Answer { get; private set; } = Array.Empty<byte>();

    public string? Query(string name) => query.TryGetValue(name, out var value) ? value : null;

    public void Respond(int status, byte[] answer)
    {
        Status = status;
        Answer = answer;
    }
}