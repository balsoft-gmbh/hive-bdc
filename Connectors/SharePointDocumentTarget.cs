using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>Uploads the document to a SharePoint library and fills its columns from the field templates.</summary>
public sealed class SharePointDocumentTarget(SharePointUploader? uploader) : IDocumentTarget
{
    public string Kind => TargetKinds.SharePoint;

    public bool ReplacesOnRepeat => true;

    public string? Unavailable(TargetOptions target)
        => uploader is null ? "SharePoint is enabled but its connection is unavailable." : null;

    public DeliveryResult Deliver(DeliveryContext ctx)
    {
        var sp = ctx.Target.ToSharePoint();
        var target = sp.ResolveTarget(out var reason);
        if (target is null)
            throw new InvalidOperationException($"SharePoint upload for '{ctx.Type.Id}' / company '{ctx.Route.DataAreaId}': {reason} " +
                $"Set the site URL and library on that company {SettingsPaths.Pick("in Settings > Outbound", "under Outbound")}.");

        if (target.Folder.Contains('{'))
            target = target with { Folder = SharePointFieldTemplate.ResolvePath(ctx.Document, target.Folder) };
        var fields = SharePointFieldTemplate.Resolve(ctx.Document, sp.Fields, ctx.Warn);
        var empty = sp.Fields.Keys.Where(k => !fields.ContainsKey(k)).ToList();
        if (empty.Count > 0)
            ctx.Log.LogInformation("[sharepoint] {Id}: column(s) left empty because the document has no value for them: {Columns}",
                ctx.DocumentId, string.Join(", ", empty));
        uploader!.Upload(ctx.Output, target, fields);
        ctx.Log.LogInformation("[sharepoint] {Id} ({Type}, {Area}) -> {Site}/{Library}/{Folder}{Name} ({Fields} column(s) requested)",
            ctx.DocumentId, ctx.Type.Id, ctx.Route.DataAreaId, target.SiteUrl, target.Library,
            string.IsNullOrWhiteSpace(target.Folder) ? "" : target.Folder.TrimEnd('/') + "/",
            ctx.Output.Name, fields.Count);
        string place = string.IsNullOrWhiteSpace(target.Folder) ? target.Library : target.Library + "/" + target.Folder.Trim('/');
        return new DeliveryResult(target.SiteUrl,
            Summary: $"Uploaded {ctx.Output.Name} to {place} on {target.SiteUrl}, {fields.Count} column(s) set.");
    }
}