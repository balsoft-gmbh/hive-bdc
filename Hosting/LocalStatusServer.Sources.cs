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

        /// <summary>POST /sources/preview: the CSV settings and columns, the company and where the CSV goes.</summary>
        public ProduceOptions? Produce { get; set; }
        public string? DataAreaId { get; set; }
        public TargetOptions? Target { get; set; }
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
            destinations = InboundDestinations(ConnectorRegistry.BuiltIn(), opts, SettingsPaths.Azure),
        };
    }

    /// <summary>Where an inbound flow can deliver in this run mode (a folder only on Windows), for the "Goes to" choice.</summary>
    internal static object[] InboundDestinations(ConnectorRegistry registry, BDCOptions opts, bool azure)
        => registry.InboundKindNames
            .Select(kind => registry.Inbound(kind, opts)!.Descriptor)
            .Where(d => !(azure && d.LocalOnly))
            .Select(d => (object)new
            {
                kind = d.Kind,
                name = d.Name,
                description = d.Description,
                connectionKind = d.ConnectionKind,
                connected = d.ConnectionKind.Length == 0 || opts.Connections.Any(c => c.Kind == d.ConnectionKind && c.Problems().Count == 0),
                settings = d.Settings.Select(x => new
                {
                    key = x.Key, label = x.Label, step = x.Step, required = x.Required, lookup = x.Lookup, dependsOn = x.DependsOn, hint = x.Hint,
                }).ToArray(),
            })
            .ToArray();

    /// <summary>
    /// POST /sources/preview: the CSV one ready record makes with the columns on screen, built by the same code as a run,
    /// so both dashboards show what the ERP will get. Nothing is written and nothing is marked.
    /// </summary>
    private async Task PostSourcePreview(ApiCall call)
    {
        SourceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<SourceRequestDto>(call.Body, JsonOpts); }
        catch (Exception ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var runtime = DraftRuntime(dto);
        var source = ConnectorRegistry.BuiltIn().Source(dto.Kind ?? "", runtime);
        if (source is null) { Respond(call, 200, new { ok = false, message = $"The document system '{dto.Kind}' is not known to this version of Hive BDC." }); return; }
        var options = SourceOf(dto);
        var problems = source.Problems(options);
        if (problems.Count > 0) { Respond(call, 200, new { ok = false, message = string.Join(" ", problems) }); return; }
        var produce = dto.Produce ?? new ProduceOptions();
        produce.Normalize();
        if (produce.Columns.Count == 0) { Respond(call, 200, new { ok = false, message = "Add at least one CSV column." }); return; }
        try
        {
            var ready = await source.ReadyAsync(options, 1);
            if (ready.Count == 0)
            {
                Respond(call, 200, new { ok = false, message = $"No {source.Descriptor.RecordNoun} is ready in {source.Descriptor.Name} right now, so there is nothing to preview." });
                return;
            }
            var record = await source.ReadAsync(options, ready[0]);
            var preview = InboundPreview.Build(source.Descriptor, record, produce, dto.DataAreaId ?? "", dto.Target, runtime.Options);
            Respond(call, 200, new
            {
                ok = preview.Problems.Count == 0,
                message = preview.Problems.Count == 0
                    ? $"{source.Descriptor.Name} {source.Descriptor.RecordNoun} {record.Id}. {preview.Columns} column(s), {preview.Rows} row(s)."
                    : string.Join(" ", preview.Problems),
                fileName = preview.FileName,
                lines = preview.Lines,
                values = record.Values.Select(v => new { name = v.Key, value = Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "" }).ToArray(),
            });
        }
        catch (ConnectorException ex) { Respond(call, 200, new { ok = false, message = ex.Message }); }
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

    private async Task PostSourceChoices(ApiCall call)
    {
        SourceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<SourceRequestDto>(call.Body, JsonOpts); }
        catch (Exception ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var result = await SourceCatalog.ChoicesAsync(ConnectorRegistry.BuiltIn(), DraftRuntime(dto), dto.Kind ?? "", dto.Lookup ?? "", SourceOf(dto));
        Respond(call, 200, new
        {
            ok = result.Error is null,
            message = result.Error ?? "",
            choices = result.Choices.Select(c => new { value = c.Value, label = c.Label, detail = c.Detail }).ToArray(),
        });
    }

    private async Task PostSourceSample(ApiCall call)
    {
        SourceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<SourceRequestDto>(call.Body, JsonOpts); }
        catch (Exception ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var source = ConnectorRegistry.BuiltIn().Source(dto.Kind ?? "", DraftRuntime(dto));
        if (source is null) { Respond(call, 200, new { ok = false, message = $"The document system '{dto.Kind}' is not known to this version of Hive BDC." }); return; }
        var options = SourceOf(dto);
        var problems = source.Problems(options);
        if (problems.Count > 0) { Respond(call, 200, new { ok = false, message = string.Join(" ", problems) }); return; }
        try
        {
            var ready = await source.ReadyAsync(options, 1);
            if (ready.Count == 0)
            {
                Respond(call, 200, new { ok = false, message = $"No {source.Descriptor.RecordNoun} is ready in {source.Descriptor.Name} right now, so there is nothing to preview." });
                return;
            }
            var record = await source.ReadAsync(options, ready[0]);
            Respond(call, 200, new
            {
                ok = true,
                message = $"{source.Descriptor.Name} {source.Descriptor.RecordNoun} {record.Id}.",
                values = record.Values.Select(v => new { name = v.Key, value = Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "" }).ToArray(),
                tables = record.Tables.Select(t => new { name = t.Key, rows = t.Value.Count }).ToArray(),
            });
        }
        catch (ConnectorException ex) { Respond(call, 200, new { ok = false, message = ex.Message }); }
    }
}