using System.Net;
using System.Text.Json;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// The dashboard's calls for inbound flows: which document systems this version reads from and what each needs
/// (GET /sources), the values of one dropdown (POST /sources/choices) and one ready record for the column preview
/// (POST /sources/sample). Unsaved connection values from the dashboard are used as they are; a blank password or
/// secret means the stored one.
/// </summary>
public sealed partial class LocalStatusServer
{
    private sealed class SourceRequestDto
    {
        public string? Kind { get; set; }
        public string? Lookup { get; set; }
        public string? Connection { get; set; }
        public Dictionary<string, string>? Settings { get; set; }
        public DocuWareRequestDto? DocuWare { get; set; }
        public SharePointDraftDto? SharePoint { get; set; }
    }

    private sealed class SharePointDraftDto
    {
        public string? TenantId { get; set; }
        public string? ClientId { get; set; }
        public string? Secret { get; set; }
    }

    private object Sources()
    {
        var opts = _optsMonitor.CurrentValue;
        opts.Normalize();
        return new
        {
            sources = SourceCatalog.Describe(ConnectorRegistry.BuiltIn(), new SourceRuntime(opts, _log)).Select(s => new
            {
                kind = s.Kind,
                name = s.Descriptor.Name,
                description = s.Descriptor.Description,
                connectionKind = s.Descriptor.ConnectionKind,
                connected = s.Connected,
                recordNoun = s.Descriptor.RecordNoun,
                defaultFileName = s.Descriptor.DefaultFileName,
                fieldsLookup = s.Descriptor.FieldsLookup,
                settings = s.Descriptor.Settings.Select(x => new
                {
                    key = x.Key, label = x.Label, step = x.Step, required = x.Required, lookup = x.Lookup, dependsOn = x.DependsOn, hint = x.Hint,
                }).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>The settings as saved, with the dashboard's unsaved connection values laid over them.</summary>
    private SourceRuntime DraftRuntime(SourceRequestDto dto)
    {
        var opts = _optsMonitor.CurrentValue;
        opts.Normalize();
        Func<string, ConnectionOptions?>? connection = null;
        if (dto.DocuWare is { } dw)
        {
            string id = string.IsNullOrWhiteSpace(dw.Id) ? ConnectionKinds.DocuWare : dw.Id.Trim();
            string password = dw.Secret ?? "";
            if (password.Length == 0 && opts.Connection(id) is { Secret.Length: > 0 } stored) password = stored.Secret;
            var draft = new ConnectionOptions
            {
                Id = id, Kind = ConnectionKinds.DocuWare,
                Settings = { ["Url"] = dw.Url ?? "", ["UserName"] = dw.UserName ?? "" },
                Secret = password,
            };
            draft.Normalize();
            connection = requested => requested.Length == 0 || requested.Equals(id, StringComparison.OrdinalIgnoreCase) ? draft : null;
        }
        SharePointAuthOptions? sharePoint = null;
        if (dto.SharePoint is { } sp)
            sharePoint = new SharePointAuthOptions
            {
                TenantId = sp.TenantId?.Trim() ?? "",
                ClientId = sp.ClientId?.Trim() ?? "",
                ClientSecret = string.IsNullOrEmpty(sp.Secret) ? opts.SharePointAuth?.ClientSecret ?? "" : sp.Secret,
            };
        return new SourceRuntime(opts, _log) { ConnectionOverride = connection, SharePointAuthOverride = sharePoint };
    }

    private static SourceOptions SourceOf(SourceRequestDto dto)
    {
        var source = new SourceOptions
        {
            Kind = dto.Kind ?? "",
            Connection = dto.Connection ?? "",
            Settings = new Dictionary<string, string>(dto.Settings ?? new(), StringComparer.OrdinalIgnoreCase),
        };
        source.Normalize();
        return source;
    }

    private async Task PostSourceChoices(HttpListenerContext ctx, HttpListenerRequest req)
    {
        SourceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<SourceRequestDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var result = await SourceCatalog.ChoicesAsync(ConnectorRegistry.BuiltIn(), DraftRuntime(dto), dto.Kind ?? "", dto.Lookup ?? "", SourceOf(dto));
        Respond(ctx, 200, new
        {
            ok = result.Error is null,
            message = result.Error ?? "",
            choices = result.Choices.Select(c => new { value = c.Value, label = c.Label, detail = c.Detail }).ToArray(),
        });
    }

    private async Task PostSourceSample(HttpListenerContext ctx, HttpListenerRequest req)
    {
        SourceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<SourceRequestDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var source = ConnectorRegistry.BuiltIn().Source(dto.Kind ?? "", DraftRuntime(dto));
        if (source is null) { Respond(ctx, 200, new { ok = false, message = $"The document system '{dto.Kind}' is not known to this version of Hive BDC." }); return; }
        var options = SourceOf(dto);
        var problems = source.Problems(options);
        if (problems.Count > 0) { Respond(ctx, 200, new { ok = false, message = string.Join(" ", problems) }); return; }
        try
        {
            var ready = await source.ReadyAsync(options, 1);
            if (ready.Count == 0)
            {
                Respond(ctx, 200, new { ok = false, message = $"No {source.Descriptor.RecordNoun} is ready in {source.Descriptor.Name} right now, so there is nothing to preview." });
                return;
            }
            var record = await source.ReadAsync(options, ready[0]);
            Respond(ctx, 200, new
            {
                ok = true,
                message = $"{source.Descriptor.Name} {source.Descriptor.RecordNoun} {record.Id}.",
                values = record.Values.Select(v => new { name = v.Key, value = Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "" }).ToArray(),
                tables = record.Tables.Select(t => new { name = t.Key, rows = t.Value.Count }).ToArray(),
            });
        }
        catch (ConnectorException ex) { Respond(ctx, 200, new { ok = false, message = ex.Message }); }
    }
}