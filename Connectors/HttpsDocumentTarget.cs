using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// The address and key of an HTTPS connection. A Logic App or Power Automate trigger URL carries its key in the query
/// (sig=), so such a URL is kept as the secret with an empty address; log lines and history name only host and path.
/// </summary>
public sealed record HttpsEndpoint(Uri Url, string? HeaderName, string? Key)
{
    public string Shown => Url.GetLeftPart(UriPartial.Path);

    public static HttpsEndpoint From(ConnectionOptions connection, string path = "")
    {
        string secret = SecretProtector.Unprotect(connection.Secret);
        string address = connection.Setting("Url");
        if (address.Length == 0)
            return new HttpsEndpoint(Https(secret), null, null);
        string full = path.Length == 0 ? address : address.TrimEnd('/') + "/" + path.TrimStart('/');
        string header = connection.Setting("HeaderName");
        return new HttpsEndpoint(Https(full), header.Length > 0 ? header : "x-api-key", secret.Length > 0 ? secret : null);
    }

    private static Uri Https(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : throw new ConnectorException("The HTTPS connection has no https:// address.", retryLater: false);

    public HttpRequestMessage Request(HttpMethod method, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, Url) { Content = content };
        if (HeaderName is not null && Key is not null) request.Headers.TryAddWithoutValidation(HeaderName, Key);
        return request;
    }
}

/// <summary>
/// Sends the produced file as JSON to an HTTPS address: a Logic App, a Power Automate flow or the customer's own API.
/// Body: documentId, documentNumber, documentType, dataAreaId, fileName, contentType, content (base64), fields (the
/// target's templates, filled from the document) and, on a repeat, previousId (the id the endpoint answered the first
/// time as {"id": ...}). 408, 429, 5xx, a timeout or no connection leave the document pending for the next scan (case
/// 20); any other 4xx is an error with the endpoint's answer.
/// </summary>
public sealed class HttpsDocumentTarget(BDCOptions options, HttpMessageHandler? handler = null) : IDocumentTarget
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly HttpClient _http = handler is null ? Shared : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };

    public string Kind => ConnectionKinds.Https;

    public bool ReplacesOnRepeat => false;

    public string? Unavailable(TargetOptions target)
    {
        var connection = options.Connection(target.Connection);
        if (connection is null)
            return $"The HTTPS target uses the connection '{target.Connection}', which is not set up. Add it in Settings > Connections.";
        var problems = connection.Problems();
        return problems.Count > 0 ? string.Join(" ", problems) : null;
    }

    public DeliveryResult Deliver(DeliveryContext ctx)
    {
        var endpoint = HttpsEndpoint.From(options.Connection(ctx.Target.Connection)!, ctx.Target.Setting("Path"));
        var fields = new JsonObject();
        foreach (var (name, value) in SharePointFieldTemplate.Resolve(ctx.Document, ctx.Target.Fields, ctx.Warn)) fields[name] = value;
        var body = new JsonObject
        {
            ["documentId"] = ctx.DocumentId,
            ["documentNumber"] = ctx.DocumentNumber,
            ["documentType"] = ctx.Type.Id,
            ["dataAreaId"] = ctx.Route.DataAreaId,
            ["fileName"] = ctx.Output.Name,
            ["contentType"] = ctx.Output.ContentType,
            ["content"] = Convert.ToBase64String(ctx.Output.ReadAllBytes()),
            ["fields"] = fields,
        };
        if (ctx.PreviousRemoteId is { } previous) body["previousId"] = previous;

        HttpResponseMessage response;
        try
        {
            using var request = endpoint.Request(HttpMethod.Post, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            response = _http.SendAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ConnectorException($"{endpoint.Shown} did not answer ({(ex is TaskCanceledException ? "no answer within 60 seconds" : ex.Message)}).", retryLater: true);
        }

        using (response)
        {
            string answer = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                bool later = status is 408 or 429 || status >= 500;
                string text = answer.Length > 300 ? answer[..300] + "…" : answer;
                throw new ConnectorException($"{endpoint.Shown} answered HTTP {status}{(text.Length > 0 ? ": " + text : ".")}", retryLater: later);
            }
            string? id = RemoteId(answer);
            ctx.Log.LogInformation("[https] {Id} -> {Endpoint}: HTTP {Status}{RemoteId}", ctx.DocumentId, endpoint.Shown, status,
                id is null ? "" : ", id " + id);
            return new DeliveryResult(endpoint.Shown, id,
                $"Sent {ctx.Output.Name} to {endpoint.Shown} (HTTP {status}{(id is null ? "" : ", id " + id)}).");
        }
    }

    /// <summary>The "id" the endpoint answered, as text, or null when it answered none.</summary>
    private static string? RemoteId(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var id)) return null;
            return id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The Settings side of the HTTPS target: an optional path after the connection's address.</summary>
public sealed class HttpsDestinationKind(SourceRuntime runtime) : IDestinationKind
{
    public TargetDescriptor Descriptor { get; } = new(
        ConnectionKinds.Https, "HTTPS endpoint",
        "Sent as JSON with the file and its fields to a Logic App, a Power Automate flow or your own API.",
        ConnectionKinds.Https,
        new[] { new SourceSetting("Path", "Path after the address (optional)", SourceSteps.Which, Required: false, Hint: "For example /invoices.") },
        null);

    public bool IsConnected => runtime.Options.Connections.Any(c => c.Kind == ConnectionKinds.Https && c.Problems().Count == 0);

    public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, TargetOptions target)
        => Task.FromResult<IReadOnlyList<SourceChoice>>(Array.Empty<SourceChoice>());
}

/// <summary>An HTTPS connection: the address, the header for the key, and the key (or a whole trigger URL) as the secret.</summary>
public sealed class HttpsConnectionKind(HttpMessageHandler? handler = null) : IConnectionKind
{
    public ConnectionDescriptor Descriptor { get; } = new(
        ConnectionKinds.Https, "HTTPS endpoint", "Send documents to an HTTPS address: a Logic App, a Power Automate flow or your own API.",
        new[]
        {
            new SourceSetting("Url", "Address", SourceSteps.Which, Required: false,
                Hint: "https://... For a Logic App or Power Automate trigger leave it empty and paste the whole trigger URL as the secret."),
            new SourceSetting("HeaderName", "Header for the key (optional)", SourceSteps.Which, Required: false,
                Hint: "Default x-api-key; Azure Functions use x-functions-key."),
        },
        "Key or trigger URL", "Stored encrypted on this machine.");

    /// <summary>Asks the address with GET; any answer but 401 or 403 means it is reachable, nothing is sent to it.</summary>
    public async Task<string> TestAsync(ConnectionOptions connection)
    {
        var endpoint = HttpsEndpoint.From(connection);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            using var request = endpoint.Request(HttpMethod.Get);
            using var response = await http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ConnectorException($"{endpoint.Shown} refused the key (HTTP {(int)response.StatusCode}).", retryLater: false);
            return $"{endpoint.Shown} answers (HTTP {(int)response.StatusCode}). Documents are sent there with POST.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ConnectorException($"{endpoint.Shown} cannot be reached: {ex.Message}", retryLater: false);
        }
    }
}