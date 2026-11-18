using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// Uploads the produced file into a blob container, for example the one an F&amp;O receive flow reads: Settings Container
/// and Prefix. With Status = true it also writes {prefix}{document id}.status.json with the document's number, type,
/// company, file name and format, for an ERP that wants to know what happened rather than the file itself.
/// </summary>
public sealed class BlobDocumentTarget(BDCOptions options, Func<ConnectionOptions, string, IBlobStore> stores) : IDocumentTarget
{
    public string Kind => "blob";

    public bool ReplacesOnRepeat => true;

    public string? Unavailable(TargetOptions target)
    {
        var connection = options.Connection(target.Connection);
        if (connection is null)
            return $"The blob target uses the connection '{target.Connection}', which is not set up. Add it in {SettingsPaths.Pick("Settings > Connections", "Connections")}.";
        var problems = connection.Problems();
        if (problems.Count > 0) return string.Join(" ", problems);
        return target.Setting("Container").Length == 0 ? "The blob target has no container." : null;
    }

    public DeliveryResult Deliver(DeliveryContext ctx)
    {
        var store = stores(options.Connection(ctx.Target.Connection)!, ctx.Target.Setting("Container"));
        string prefix = ctx.Target.Setting("Prefix");
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        string name = prefix + ctx.Output.Name;
        store.Upload(name, ctx.Output.ReadAllBytes(), ctx.Output.ContentType);
        string summary = $"Uploaded {ctx.Output.Name} to {store.ContainerUri.GetLeftPart(UriPartial.Path)}/{name}";

        if (string.Equals(ctx.Target.Setting("Status"), "true", StringComparison.OrdinalIgnoreCase))
        {
            string status = prefix + ctx.DocumentId + ".status.json";
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                documentId = ctx.DocumentId,
                documentNumber = ctx.DocumentNumber,
                documentType = ctx.Type.Id,
                dataAreaId = ctx.Route.DataAreaId,
                file = name,
                contentType = ctx.Output.ContentType,
                deliveredAt = DateTimeOffset.UtcNow.ToString("o"),
            });
            store.Upload(status, body, "application/json");
            summary += $" and the status as {status}";
        }
        ctx.Log.LogInformation("[blob] {Id} -> {Container}/{Name}", ctx.DocumentId, store.ContainerUri, name);
        return new DeliveryResult(store.ContainerUri.GetLeftPart(UriPartial.Path), name, summary + ".");
    }
}