using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>What the HTTPS source answers: a status code and a small JSON body.</summary>
public sealed record InboundAnswer(int Status, object Body);

/// <summary>
/// The HTTPS source (failure case 21): a caller, an ERP, a Logic App or a Power Automate flow, posts one document to
/// POST /inbound/{documentType}/{company} with the key of the company entry's https connection in its header (default
/// x-api-key). Body: {"document": {the ERP export JSON}, "pdf": "base64, optional"}; the query ?id= may name the
/// document, else its documentNumber does. The document is staged into the entry's input folder as {id}_https.json (and
/// .pdf), where the normal pipeline takes it. Nothing is stored for a missing or wrong key (401), a body over the limit
/// (413), a body that is not such JSON (400) or a type and company without an https source (404). A document whose id
/// arrived before is answered 200 without a second copy; a new one 202.
/// </summary>
public sealed partial class HttpsInbound(ILogger log)
{
    public const long MaxBytes = 50 * 1024 * 1024;
    private const string StagedSuffix = "_https";

    [GeneratedRegex(@"[^A-Za-z0-9\-]")]
    private static partial Regex UnsafeIdChars();

    public InboundAnswer Receive(BDCOptions opts, string typeId, string company, Func<string, string?> header, string? idParam,
        long? contentLength, Func<string> readBody)
    {
        var type = opts.DocumentTypes.FirstOrDefault(t => t.Enabled && t.Matches(typeId));
        var route = type?.Companies.FirstOrDefault(r => r.IsOn && r.Source is { Kind: ConnectionKinds.Https }
            && string.Equals(r.DataAreaId, company, StringComparison.OrdinalIgnoreCase));
        if (type is null || route is null)
            return new InboundAnswer(404, new { error = "No company of this document type takes documents over HTTPS." });

        var connection = opts.Connection(route.Source!.Connection);
        string expected = connection is null ? "" : SecretProtector.Unprotect(connection.Secret);
        string headerName = connection?.Setting("HeaderName") is { Length: > 0 } name ? name : "x-api-key";
        string given = header(headerName) ?? "";
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
        {
            log.LogWarning("[https-in] {Type}/{Company}: refused, missing or wrong key.", type.Id, route.DataAreaId);
            return new InboundAnswer(401, new { error = "Missing or wrong key." });
        }
        if (contentLength > MaxBytes)
            return new InboundAnswer(413, new { error = $"The document is larger than {MaxBytes / (1024 * 1024)} MB." });

        JsonObject document;
        byte[]? pdf = null;
        try
        {
            var body = JsonNode.Parse(readBody()) as JsonObject ?? throw new JsonException("not an object");
            document = body["document"] as JsonObject ?? throw new JsonException("no \"document\" object");
            if (body["pdf"] is JsonValue pdfValue && pdfValue.TryGetValue<string>(out var base64) && base64.Length > 0)
                pdf = Convert.FromBase64String(base64);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new InboundAnswer(400, new { error = "The body must be {\"document\": {...}, \"pdf\": \"base64\"}: " + ex.Message });
        }

        string id = IdFor(idParam ?? (string?)document["documentNumber"] ?? (string?)document["invoiceNumber"]);
        string staged = id + StagedSuffix + ".json";
        if (new[] { route.Input, Path.Combine(route.Input, "processed"), Path.Combine(route.Input, "error") }
            .Any(folder => File.Exists(Path.Combine(folder, staged))))
            return new InboundAnswer(200, new { status = "already received", id });

        Directory.CreateDirectory(route.Input);
        if (pdf is not null) CsvFile.WriteAtomic(Path.Combine(route.Input, id + StagedSuffix + ".pdf"), pdf);
        CsvFile.WriteAtomic(Path.Combine(route.Input, staged), Encoding.UTF8.GetBytes(document.ToJsonString()));
        log.LogInformation("[https-in] {Type}/{Company}: document {Id} staged{Pdf}.", type.Id, route.DataAreaId, id, pdf is null ? "" : " with its PDF");
        return new InboundAnswer(202, new { status = "accepted", id });
    }

    private static string IdFor(string? text)
    {
        string id = UnsafeIdChars().Replace(text ?? "", "-").Trim('-');
        if (id.Length > 100) id = id[..100];
        return id.Length > 0 ? id : Guid.NewGuid().ToString("N");
    }
}