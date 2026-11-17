using System.Net;
using System.Text.Json;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// The dashboard's calls for outbound destinations and connections: which destination kinds and connection kinds this
/// version has (GET /targets, GET /connections/kinds), the values of one dropdown or the field rows of a destination
/// (POST /targets/choices), and a sign-in check of a connection with the values on the form (POST /connections/test).
/// </summary>
public sealed partial class LocalStatusServer
{
    private sealed class ConnectionTestDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public Dictionary<string, string>? Settings { get; set; }
        /// <summary>Blank => the stored one of the same id.</summary>
        public string? Secret { get; set; }
    }

    private object Targets()
    {
        var opts = _optsMonitor.CurrentValue;
        opts.Normalize();
        return new
        {
            targets = DestinationCatalog.Describe(ConnectorRegistry.BuiltIn(), new SourceRuntime(opts, _log)).Select(d => new
            {
                kind = d.Kind,
                name = d.Descriptor.Name,
                description = d.Descriptor.Description,
                connectionKind = d.Descriptor.ConnectionKind,
                connected = d.Connected,
                fieldsLookup = d.Descriptor.FieldsLookup,
                settings = d.Descriptor.Settings.Select(x => new
                {
                    key = x.Key, label = x.Label, step = x.Step, required = x.Required, lookup = x.Lookup, dependsOn = x.DependsOn, hint = x.Hint,
                }).ToArray(),
            }).ToArray(),
        };
    }

    private async Task PostTargetChoices(ApiCall call)
    {
        SourceRequestDto? dto;
        try { dto = JsonSerializer.Deserialize<SourceRequestDto>(call.Body, JsonOpts); }
        catch (Exception ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var target = new TargetOptions
        {
            Kind = dto.Kind ?? "",
            Connection = dto.Connection ?? "",
            Settings = new Dictionary<string, string>(dto.Settings ?? new(), StringComparer.OrdinalIgnoreCase),
        };
        target.Normalize();
        var result = await DestinationCatalog.ChoicesAsync(ConnectorRegistry.BuiltIn(), DraftRuntime(dto), dto.Kind ?? "", dto.Lookup ?? "", target);
        Respond(call, 200, new
        {
            ok = result.Error is null,
            message = result.Error ?? "",
            choices = result.Choices.Select(c => new { value = c.Value, label = c.Label, detail = c.Detail }).ToArray(),
        });
    }

    private object ConnectionKindsList() => new
    {
        kinds = ConnectionCatalog.Describe(ConnectorRegistry.BuiltIn()).Select(k => new
        {
            kind = k.Kind,
            name = k.Name,
            description = k.Description,
            secretLabel = k.SecretLabel,
            secretHint = k.SecretHint,
            settings = k.Settings.Select(x => new { key = x.Key, label = x.Label, required = x.Required, hint = x.Hint }).ToArray(),
        }).ToArray(),
    };

    private async Task PostConnectionTest(ApiCall call)
    {
        ConnectionTestDto? dto;
        try { dto = JsonSerializer.Deserialize<ConnectionTestDto>(call.Body, JsonOpts); }
        catch (Exception ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        dto ??= new();
        var opts = _optsMonitor.CurrentValue;
        opts.Normalize();
        var draft = ConnectionCatalog.Draft(opts, dto.Id ?? "", dto.Kind ?? "", dto.Settings ?? new(), dto.Secret ?? "");
        var result = await ConnectionCatalog.TestAsync(ConnectorRegistry.BuiltIn(), draft);
        Respond(call, 200, new { ok = result.Ok, message = result.Message });
    }
}