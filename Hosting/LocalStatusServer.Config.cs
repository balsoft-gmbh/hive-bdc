using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>GET /config and POST /config: the settings contract the dashboard edits.</summary>
public sealed partial class LocalStatusServer
{
    // ── GET /config ─────────────────────────────────────────────────────────
    //
    // Read contract:
    //   { pollSeconds, logDir, license:{file},
    //     sharePointAuth:{tenantId, clientId, secretSet},           // secret NEVER returned
    //     mail:{...},
    //     documentTypes:[ {id, displayName, party, handler, enabled, mail, discovered,
    //                      companies:[ {dataAreaId, input, output,
    //                                   sharePoint:{enabled, siteUrl, library, folder, fields}} ]} ] }
    // Structure comes from Snapshot(), which migrates every older shape (top-level companies,
    // single-config) on the fly, so an old file reads back in the new form.

    private void GetConfig(HttpListenerContext ctx) => Respond(ctx, 200, BuildConfigResponse());

    private object BuildConfigResponse()
    {
        var opts = Snapshot();
        var auth = opts.SharePointAuth;
        return new
        {
            pollSeconds = opts.PollSeconds,
            logDir = opts.LogDir,
            license = new { file = opts.License.File },
            sharePointAuth = new
            {
                tenantId = auth.TenantId,
                clientId = auth.ClientId,
                secretSet = !string.IsNullOrWhiteSpace(auth.ClientSecret),   // bool only; secret never returned
            },
            mail = new
            {
                enabled = opts.Mail.Enabled,
                tenantId = opts.Mail.TenantId,          // blank => the SharePoint tenant is used
                clientId = opts.Mail.ClientId,
                secretSet = !string.IsNullOrWhiteSpace(opts.Mail.ClientSecret),   // bool only; secret never returned
                from = opts.Mail.From,
                redirectTo = opts.Mail.CleanRedirectTo(),
                saveToSentItems = opts.Mail.SaveToSentItems,
            },
            documentTypes = opts.DocumentTypes.Select(t => new
            {
                id = t.Id,
                displayName = t.DisplayName,
                party = t.Party.ToString(),
                handler = t.Handler.ToString(),
                enabled = t.Enabled,
                mail = t.Mail,
                discovered = t.Discovered,
                companies = t.Companies.Select(r => new
                {
                    dataAreaId = r.DataAreaId,
                    input = r.Input,
                    output = r.Output,
                    sharePoint = new
                    {
                        enabled = r.SharePoint.Enabled,
                        siteUrl = r.SharePoint.SiteUrl,
                        library = r.SharePoint.Library,
                        folder = r.SharePoint.Folder,
                        fields = r.SharePoint.Fields,
                    },
                }).ToArray(),
            }).ToArray(),
        };
    }

    // ── POST /config ────────────────────────────────────────────────────────
    //
    // Write contract = the GET shape; sharePointAuth / mail MAY include "clientSecret"
    // (blank/absent => keep the stored one). Validates, DPAPI-encrypts freshly provided
    // secrets, writes the HiveBDC section (PollSeconds/LogDir/License/SharePointAuth/
    // Mail/DocumentTypes[]) to appsettings.json ATOMICALLY. 200 {ok:true} | 400
    // {ok:false,errors:[...]}. The reload watcher then hot-applies it (no restart).
    // documentTypes absent from the POST => the stored list is kept; present => replaced
    // as sent, so a removed built-in stays removed; an empty list is refused, because the
    // service seeds the six built-ins into an empty one on load. Any legacy "Companies" list is
    // dropped on write, since it was migrated into the types on read.

    private void PostConfig(HttpListenerContext ctx, HttpListenerRequest req)
    {
        ConfigPostDto? dto;
        try { dto = JsonSerializer.Deserialize<ConfigPostDto>(ReadBody(req), JsonOpts); }
        catch (Exception ex) { Respond(ctx, 400, new { ok = false, errors = new[] { "Invalid JSON: " + ex.Message } }); return; }
        dto ??= new ConfigPostDto();

        // Stored (on-disk) shared secret — reused when the POST omits/blanks clientSecret.
        var root = ReadRoot();
        if (root is null && File.Exists(_settingsPath))
        {
            // Never replace an unreadable settings file with a near-empty one: everything
            // not in this POST (and any hand edits) would be lost without a trace.
            Respond(ctx, 500, new { ok = false, errors = new[] { "appsettings.json could not be read, so it was not overwritten. Fix or restore the file, then save again." } });
            return;
        }
        root ??= new JsonObject();
        var prevSection = SettingsFile.SectionOf(root);
        string storedSecret = (prevSection?["SharePointAuth"] as JsonObject)?["ClientSecret"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(storedSecret))   // legacy single-config kept it under SharePoint:ClientSecret
            storedSecret = (prevSection?["SharePoint"] as JsonObject)?["ClientSecret"]?.GetValue<string>() ?? "";

        string postedSecret = dto.SharePointAuth?.ClientSecret ?? "";
        bool haveSecret = !string.IsNullOrWhiteSpace(postedSecret) || !string.IsNullOrWhiteSpace(storedSecret);

        // Same contract for the mail secret: blank/absent on POST keeps the stored one.
        string storedMailSecret = (prevSection?["Mail"] as JsonObject)?["ClientSecret"]?.GetValue<string>() ?? "";
        string postedMailSecret = dto.Mail?.ClientSecret ?? "";
        bool haveMailSecret = !string.IsNullOrWhiteSpace(postedMailSecret) || !string.IsNullOrWhiteSpace(storedMailSecret);

        // ── validate ──────────────────────────────────────────────────────
        var errors = new List<string>();
        int poll = dto.PollSeconds ?? 0;
        if (poll < 5) errors.Add("pollSeconds must be at least 5.");

        var types = dto.DocumentTypes;
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anySharePoint = false;
        if (types is { Count: 0 })
            errors.Add("Keep at least one document type. To stop one from being processed, untick Enabled instead of removing it.");
        if (types is not null)
        {
            for (int i = 0; i < types.Count; i++)
            {
                var t = types[i];
                string label = string.IsNullOrWhiteSpace(t.DisplayName) ? (t.Id ?? $"#{i + 1}") : t.DisplayName!;
                if (!DocumentTypeCatalog.IsValidId(t.Id))
                    errors.Add($"Document type '{label}': the ID must be letters, digits, '-' or '_' only (it is used as a folder name).");
                else if (!seenTypes.Add(t.Id!.Trim()))
                    errors.Add($"Document type '{label}': duplicate ID '{t.Id!.Trim()}'.");
                if (!Enum.TryParse<DocumentParty>(t.Party ?? "", ignoreCase: true, out _))
                    errors.Add($"Document type '{label}': 'Sent to' must be Customer or Vendor.");
                if (!Enum.TryParse<DocumentHandlerKind>(t.Handler ?? "", ignoreCase: true, out _))
                    errors.Add($"Document type '{label}': 'Produced as' must be EInvoice or Pdf.");

                var seenAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in t.Companies ?? new())
                {
                    string co = string.IsNullOrWhiteSpace(r.DataAreaId) ? "(no company id)" : r.DataAreaId!.Trim();
                    if (string.IsNullOrWhiteSpace(r.DataAreaId))
                        errors.Add($"Document type '{label}': a company entry has no company (dataAreaId).");
                    else if (!seenAreas.Add(co))
                        errors.Add($"Document type '{label}': company '{co}' is listed twice.");
                    if (string.IsNullOrWhiteSpace(r.Input))
                        errors.Add($"Document type '{label}', company '{co}': the input folder is required.");
                    var sp = r.SharePoint;
                    if (sp?.Enabled == true)
                    {
                        anySharePoint = true;
                        if (string.IsNullOrWhiteSpace(sp.SiteUrl))
                            errors.Add($"Document type '{label}', company '{co}': SharePoint is on but the site URL is empty.");
                        else if (!Uri.TryCreate(sp.SiteUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                            errors.Add($"Document type '{label}', company '{co}': the site URL must be a valid https URL.");
                        if (string.IsNullOrWhiteSpace(sp.Library))
                            errors.Add($"Document type '{label}', company '{co}': SharePoint is on but the library is empty.");
                    }
                }
            }
        }
        if (anySharePoint)
        {
            if (string.IsNullOrWhiteSpace(dto.SharePointAuth?.TenantId))
                errors.Add("SharePoint is on for at least one company entry, so the SharePoint app needs a tenant ID (Connections).");
            if (string.IsNullOrWhiteSpace(dto.SharePointAuth?.ClientId))
                errors.Add("SharePoint is on for at least one company entry, so the SharePoint app needs a client ID (Connections).");
            if (!haveSecret)
                errors.Add("SharePoint is on for at least one company entry, so the SharePoint app needs a client secret (Connections).");
        }

        // Mail: only validated when it is switched on. The redirect list is required then,
        // because a non-production document with nowhere safe to go would otherwise reach a customer.
        var mail = dto.Mail;
        if (mail?.Enabled == true)
        {
            string mailTenant = string.IsNullOrWhiteSpace(mail.TenantId) ? dto.SharePointAuth?.TenantId ?? "" : mail.TenantId;
            if (string.IsNullOrWhiteSpace(mailTenant))
                errors.Add("mail.tenantId is required when document mail is enabled (or set the SharePoint tenant).");
            if (string.IsNullOrWhiteSpace(mail.ClientId))
                errors.Add("mail.clientId is required when document mail is enabled.");
            if (!haveMailSecret)
                errors.Add("mail.clientSecret is required (none stored) when document mail is enabled.");
            if (string.IsNullOrWhiteSpace(mail.From))
                errors.Add("mail.from is required when document mail is enabled.");
            else if (!MailAddresses.LooksLikeAddress(mail.From))
                errors.Add($"mail.from '{mail.From}' is not a valid e-mail address.");

            var redirect = MailAddresses.Clean(mail.RedirectTo);
            if (redirect.Count == 0)
                errors.Add("mail.redirectTo needs at least one address: it is where documents from a non-production AX are sent.");
            foreach (var bad in redirect.Where(a => !MailAddresses.LooksLikeAddress(a)))
                errors.Add($"mail.redirectTo contains '{bad}', which is not a valid e-mail address.");
        }

        if (errors.Count > 0) { Respond(ctx, 400, new { ok = false, errors }); return; }

        // ── build the new HiveBDC section ─────────────────────────────
        string secretToStore = !string.IsNullOrWhiteSpace(postedSecret)
            ? SecretProtector.Protect(postedSecret)
            : storedSecret;

        JsonNode? typesNode;
        if (types is not null)
        {
            var arr = new JsonArray();
            foreach (var t in types)
            {
                var opt = new DocumentTypeOptions
                {
                    Id = t.Id!.Trim(),
                    DisplayName = t.DisplayName ?? "",
                    Party = Enum.Parse<DocumentParty>(t.Party!, ignoreCase: true),
                    Handler = Enum.Parse<DocumentHandlerKind>(t.Handler!, ignoreCase: true),
                    Enabled = t.Enabled ?? true,
                    Mail = t.Mail ?? true,
                    Discovered = false,   // saved from Settings => reviewed
                    Companies = (t.Companies ?? new()).Select(r => new DocumentRoute
                    {
                        DataAreaId = r.DataAreaId!.Trim(),
                        Input = r.Input?.Trim() ?? "",
                        Output = r.Output?.Trim() ?? "",
                        SharePoint = new SharePointTargetOptions
                        {
                            Enabled = r.SharePoint?.Enabled ?? false,
                            SiteUrl = SharePointTargetOptions.CleanSiteUrl(r.SharePoint?.SiteUrl),
                            Library = SharePointTargetOptions.CleanLibrary(r.SharePoint?.Library),
                            Folder = SharePointTargetOptions.CleanFolder(r.SharePoint?.Folder),
                            Fields = r.SharePoint?.Fields ?? new(),
                        },
                    }).ToList(),
                };
                arr.Add(SettingsFile.DocumentTypeNode(opt));
            }
            typesNode = arr;
        }
        else
        {
            typesNode = prevSection?["DocumentTypes"]?.DeepClone() ?? new JsonArray();
        }

        var section = new JsonObject
        {
            ["PollSeconds"] = poll,
            ["LogDir"] = dto.LogDir ?? "",
            ["License"] = new JsonObject
            {
                ["File"] = dto.License?.File ?? "",
            },
            ["SharePointAuth"] = new JsonObject
            {
                ["TenantId"] = dto.SharePointAuth?.TenantId ?? "",
                ["ClientId"] = dto.SharePointAuth?.ClientId ?? "",
                ["ClientSecret"] = secretToStore,
            },
            ["Mail"] = new JsonObject
            {
                ["Enabled"] = mail?.Enabled ?? false,
                ["TenantId"] = mail?.TenantId ?? "",
                ["ClientId"] = mail?.ClientId ?? "",
                ["ClientSecret"] = !string.IsNullOrWhiteSpace(postedMailSecret)
                    ? SecretProtector.Protect(postedMailSecret)
                    : storedMailSecret,
                ["From"] = mail?.From ?? "",
                ["RedirectTo"] = new JsonArray(MailAddresses.Clean(mail?.RedirectTo)
                    .Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["SaveToSentItems"] = mail?.SaveToSentItems ?? true,
            },
            ["DocumentTypes"] = typesNode,
        };

        root.Remove(LegacySettingsSection);      // migrate away from the 1.x section on write
        root[SettingsSection] = section;

        try { SettingsFile.AtomicWrite(_settingsPath, root); }
        catch (Exception ex)
        { Respond(ctx, 500, new { ok = false, errors = new[] { "Failed to write appsettings.json: " + ex.Message } }); return; }

        bool secretChanged = !string.IsNullOrWhiteSpace(postedSecret);
        string mailSummary = mail?.Enabled == true
            ? $"on, from={mail.From}, redirect={string.Join(",", MailAddresses.Clean(mail.RedirectTo))}, " +
              $"secret={(string.IsNullOrWhiteSpace(postedMailSecret) ? "unchanged" : "changed")}"
            : "off";
        string typeSummary = types is null ? "unchanged" : string.Join("; ", types.Select(t =>
            $"{t.Id}: {(t.Companies ?? new()).Count} company entr(y/ies)"));
        _log.LogInformation(
            "Configuration updated via /config — poll={Poll}s, logDir={LogDir}, sharedSecret={Secret}, mail={Mail}, documentTypes: {Types}",
            poll, string.IsNullOrWhiteSpace(dto.LogDir) ? "(default)" : dto.LogDir,
            secretChanged ? "changed" : "unchanged", mailSummary, typeSummary);
        Respond(ctx, 200, new { ok = true });
    }

    // ── request DTOs (camelCase JSON, case-insensitive binding) ─────────────

    /// <summary>POST /config body — the settings payload (same shape as GET /config).</summary>
    private sealed class ConfigPostDto
    {
        public int? PollSeconds { get; set; }
        public string? LogDir { get; set; }
        public LicDto? License { get; set; }
        public AuthPostDto? SharePointAuth { get; set; }
        public MailPostDto? Mail { get; set; }
        /// <summary>Absent => keep the stored document types untouched.</summary>
        public List<DocumentTypePostDto>? DocumentTypes { get; set; }
    }

    /// <summary>One document type on POST (same shape as GET /config documentTypes[]).</summary>
    private sealed class DocumentTypePostDto
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Party { get; set; }
        public string? Handler { get; set; }
        public bool? Enabled { get; set; }
        public bool? Mail { get; set; }
        public List<RoutePostDto>? Companies { get; set; }
    }

    /// <summary>One company entry of a document type on POST.</summary>
    private sealed class RoutePostDto
    {
        public string? DataAreaId { get; set; }
        public string? Input { get; set; }
        public string? Output { get; set; }
        public CompanySpDto? SharePoint { get; set; }
    }

    /// <summary>Shared mail config on POST. ClientSecret is optional; blank/absent => keep the stored one.</summary>
    private sealed class MailPostDto
    {
        public bool? Enabled { get; set; }
        public string? TenantId { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? From { get; set; }
        public List<string>? RedirectTo { get; set; }
        public bool? SaveToSentItems { get; set; }
    }

    /// <summary>POST /config/test-mail body: the mail settings to test; a blank secret uses the stored one.</summary>
    private sealed class TestMailDto
    {
        public MailPostDto? Mail { get; set; }
        public string? TenantId { get; set; }
    }

    /// <summary>Shared SharePoint auth on POST. ClientSecret is optional; blank/absent => keep the stored one.</summary>
    private sealed class AuthPostDto
    {
        public string? TenantId { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }

    private sealed class CompanySpDto
    {
        public bool? Enabled { get; set; }
        public string? SiteUrl { get; set; }
        public string? Library { get; set; }
        public string? Folder { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
    }

    private sealed class LicDto
    {
        public string? File { get; set; }
    }

    /// <summary>POST /license body: the .lic file text (+ optional original file name for the log).</summary>
    private sealed class LicenseUploadDto
    {
        public string? Content { get; set; }
        public string? FileName { get; set; }
    }

    // POST /config/test-sharepoint body: { sharePoint:{ siteUrl, library, folder }, clientSecret? }.
    private sealed class TestSpDto
    {
        public SpTargetDto? SharePoint { get; set; }
        public string? ClientSecret { get; set; }   // blank/absent => use the stored shared secret
    }

    private sealed class SpTargetDto
    {
        public string? SiteUrl { get; set; }
        public string? Library { get; set; }
        public string? Folder { get; set; }
    }

}
