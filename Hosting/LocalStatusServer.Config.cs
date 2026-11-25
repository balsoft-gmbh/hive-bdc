using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
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

    // The answer and its version come from one read of the stored settings, so an editor never pairs older content
    // with a newer version (failure case 54); the read is remembered as the start of a later save.
    private void GetConfig(ApiCall call)
    {
        JsonObject? stored = null;
        try { stored = _settings.ReadSection(); }
        catch (InvalidOperationException) { /* unreadable: answer from the running settings, without a version */ }
        if (stored is null)
        {
            Respond(call, 200, BuildConfigResponse(Snapshot(), "", null));
            return;
        }
        string version = ConfigMerge.Version(stored);
        _versions.Remember(version, stored);
        // A local install answers from the running settings as it always did (command line and environment included);
        // the Azure settings blob answers from the read that gave the version.
        var options = _settings is BlobSettingsDocument ? OptionsFrom(stored) : Snapshot();
        Respond(call, 200, BuildConfigResponse(options, version, stored[ConfigMerge.LastSavedKey] as JsonObject));
    }

    private static BDCOptions OptionsFrom(JsonObject section)
    {
        var root = new JsonObject { [SettingsFile.Section] = section.DeepClone() };
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
        var options = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonStream(stream).Build()
            .GetSection(SettingsFile.Section).Get<BDCOptions>() ?? new BDCOptions();
        options.Normalize();
        return options;
    }

    private object BuildConfigResponse(BDCOptions opts, string version, JsonObject? lastSaved)
    {
        var auth = opts.SharePointAuth;
        return new
        {
            pollSeconds = opts.PollSeconds,
            logDir = opts.LogDir,
            paused = opts.Paused,
            setupOpen = opts.SetupOpen,
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
            flows = opts.Flows,
            connections = opts.Connections.Select(c => new
            {
                id = c.Id,
                kind = c.Kind,
                settings = c.Settings,
                hasSecret = c.Secret.Length > 0,
            }).ToArray(),
            documentTypes = opts.DocumentTypes.Select(t => new
            {
                id = t.Id,
                displayName = t.DisplayName,
                party = t.Party.ToString(),
                handler = t.Handler.ToString(),
                enabled = t.Enabled,
                mail = t.Mail,
                compressPdf = t.CompressPdf,
                discovered = t.Discovered,
                companies = t.Companies.Select(r => new
                {
                    dataAreaId = r.DataAreaId,
                    input = r.Input,
                    inputFormat = r.InputFormat,
                    output = r.Output,
                    enabled = r.IsOn,
                    mail = r.MailsFor(t),
                    compressPdf = r.CompressesFor(t),
                    mailCc = r.MailCc,
                    mailBcc = r.MailBcc,
                    sharePoint = new
                    {
                        enabled = r.SharePoint.Enabled,
                        siteUrl = r.SharePoint.SiteUrl,
                        library = r.SharePoint.Library,
                        folder = r.SharePoint.Folder,
                        fields = r.SharePoint.Fields,
                    },
                    targets = r.Targets.Select(x => new
                    {
                        kind = x.Kind,
                        connection = x.Connection,
                        enabled = x.Enabled,
                        settings = x.Settings,
                        fields = x.Fields,
                    }).ToArray(),
                    source = r.Source is null ? null : new { kind = r.Source.Kind, connection = r.Source.Connection, settings = r.Source.Settings },
                }).ToArray(),
            }).ToArray(),
            version,
            lastSaved = lastSaved is null ? null : new { by = (string?)lastSaved["By"] ?? "", at = (string?)lastSaved["At"] ?? "" },
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

    private void PostConfig(ApiCall call)
    {
        ConfigPostDto? dto;
        try { dto = JsonSerializer.Deserialize<ConfigPostDto>(call.Body, JsonOpts); }
        catch (Exception ex) { Respond(call, 400, new { ok = false, errors = new[] { "Invalid JSON: " + ex.Message } }); return; }
        dto ??= new ConfigPostDto();

        // Stored shared secret — reused when the POST omits/blanks clientSecret.
        JsonObject? prevSection;
        try { prevSection = _settings.ReadSection(); }
        catch (InvalidOperationException ex)
        {
            // Never replace unreadable settings with near-empty ones: everything not in this
            // POST (and any hand edits) would be lost without a trace.
            Respond(call, 500, new { ok = false, errors = new[] { ex.Message } });
            return;
        }
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
            errors.Add("Keep at least one outbound document type. To stop one from being processed, switch it off instead of removing it.");
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

        // Inbound flows: two switched-on flows must not pick up the same records.
        if (dto.Flows is { Count: > 0 } postedFlows)
        {
            try
            {
                var flows = JsonSerializer.Deserialize<List<FlowOptions>>(postedFlows.ToJsonString(), JsonOpts) ?? new();
                foreach (var f in flows) f.Normalize();
                errors.AddRange(InboundChecks.Problems(flows, ConnectorRegistry.BuiltIn(), new SourceRuntime(_optsMonitor.CurrentValue, _log)));
            }
            catch (JsonException ex) { errors.Add("The inbound flows could not be read: " + ex.Message); }
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

        foreach (var t in types ?? Enumerable.Empty<DocumentTypePostDto>())
            foreach (var r in t.Companies ?? new())
            {
                foreach (var bad in MailAddresses.Clean(r.MailCc).Where(a => !MailAddresses.LooksLikeAddress(a)))
                    errors.Add($"{t.Id} › {r.DataAreaId}: the Cc address '{bad}' is not a valid e-mail address.");
                foreach (var bad in MailAddresses.Clean(r.MailBcc).Where(a => !MailAddresses.LooksLikeAddress(a)))
                    errors.Add($"{t.Id} › {r.DataAreaId}: the Bcc address '{bad}' is not a valid e-mail address.");
            }

        if (errors.Count > 0) { Respond(call, 400, new { ok = false, errors }); return; }

        // ── build the new HiveBDC section ─────────────────────────────
        // Built twice when the editor started from an older version: first with placeholders, so a refused save never
        // puts a newly typed secret into Key Vault (failure case 54), then for real.
        JsonObject BuildSection(Func<string, string?, string> protect)
        {
        string secretToStore = !string.IsNullOrWhiteSpace(postedSecret)
            ? protect(postedSecret, "sharepoint-client-secret")
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
                    CompressPdf = t.CompressPdf ?? SettingsFile.StoredCompressPdf(prevSection, t.Id!.Trim()),
                    Discovered = false,   // saved from Settings => reviewed
                    Companies = (t.Companies ?? new()).Select(r => new DocumentRoute
                    {
                        DataAreaId = r.DataAreaId!.Trim(),
                        Input = r.Input?.Trim() ?? "",
                        InputFormat = InputFormats.IsWord(r.InputFormat) ? InputFormats.Word : InputFormats.Json,
                        Output = r.Output?.Trim() ?? "",
                        Enabled = r.Enabled,
                        Mail = r.Mail,
                        SharePoint = new SharePointTargetOptions
                        {
                            Enabled = r.SharePoint?.Enabled ?? false,
                            SiteUrl = SharePointTargetOptions.CleanSiteUrl(r.SharePoint?.SiteUrl),
                            Library = SharePointTargetOptions.CleanLibrary(r.SharePoint?.Library),
                            Folder = SharePointTargetOptions.CleanFolder(r.SharePoint?.Folder),
                            Fields = r.SharePoint?.Fields ?? new(),
                        },
                        Targets = r.Targets?.Select(x => new TargetOptions
                        {
                            Kind = x.Kind ?? "",
                            Connection = x.Connection ?? "",
                            Enabled = x.Enabled ?? true,
                            Settings = x.Settings ?? new(),
                            Fields = x.Fields ?? new(),
                        }).ToList() ?? new(),
                    }).ToList(),
                };
                // A dashboard that does not send targets (every version before the target
                // list) must not delete the ones the settings file has.
                var posted = t.Companies ?? new();
                for (int i = 0; i < opt.Companies.Count; i++)
                {
                    if (posted[i].Targets is null)
                        opt.Companies[i].Targets = SettingsFile.StoredTargets(prevSection, opt.Id, opt.Companies[i].DataAreaId, opt.Companies[i].Input);
                    opt.Companies[i].Source = posted[i].Source ?? SettingsFile.StoredSource(prevSection, opt.Id, opt.Companies[i].DataAreaId, opt.Companies[i].Input);
                    opt.Companies[i].CompressPdf = posted[i].CompressPdf
                        ?? SettingsFile.StoredRouteCompressPdf(prevSection, opt.Id, opt.Companies[i].DataAreaId, opt.Companies[i].Input);
                    opt.Companies[i].MailCc = posted[i].MailCc is { } cc
                        ? MailAddresses.Clean(cc).ToList()
                        : SettingsFile.StoredRouteMailCc(prevSection, opt.Id, opt.Companies[i].DataAreaId, opt.Companies[i].Input);
                    opt.Companies[i].MailBcc = posted[i].MailBcc is { } bcc
                        ? MailAddresses.Clean(bcc).ToList()
                        : SettingsFile.StoredRouteMailBcc(prevSection, opt.Id, opt.Companies[i].DataAreaId, opt.Companies[i].Input);
                }
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
                    ? protect(postedMailSecret, "mail-client-secret")
                    : storedMailSecret,
                ["From"] = mail?.From ?? "",
                ["RedirectTo"] = new JsonArray(MailAddresses.Clean(mail?.RedirectTo)
                    .Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["SaveToSentItems"] = mail?.SaveToSentItems ?? true,
            },
            ["DocumentTypes"] = typesNode,
        };

        if (dto.Paused is bool paused) section["Paused"] = paused;
        else if (prevSection?["Paused"] is JsonNode storedPause) section["Paused"] = storedPause.DeepClone();
        if (dto.SetupOpen is bool open) section["SetupOpen"] = open;
        else if (prevSection?["SetupOpen"] is JsonNode storedOpen) section["SetupOpen"] = storedOpen.DeepClone();
        section["Connections"] = ConnectionsNode(dto.Connections, prevSection, protect);
        section["Flows"] = dto.Flows?.DeepClone() ?? prevSection?["Flows"]?.DeepClone() ?? new JsonArray();
        return section;
        }

        string? baseVersion = string.IsNullOrWhiteSpace(dto.BaseVersion) ? null : dto.BaseVersion.Trim();
        bool saveBoth = dto.SaveBoth == true;
        if (baseVersion is not null && ConfigMerge.Version(prevSection) != baseVersion)
        {
            var draft = BuildSection(static (_, name) => "(new value for " + name + ")");
            var start = _versions.Get(baseVersion);
            var preview = start is null ? null : ConfigMerge.Merge3(start, prevSection, draft);
            if (preview is null || !saveBoth || preview.Conflicts.Count > 0)
            {
                Respond(call, 409, Refusal(start, prevSection, draft, preview));
                return;
            }
        }
        var section = BuildSection(SecretProtector.Protect);

        object? refused = null;
        JsonObject? written = null;
        // The product section is replaced as a whole (a legacy 1.x section is renamed on the way); the rest of the
        // file or blob stays as it is.
        try
        {
            _settings.Update(stored =>
            {
                refused = null;
                var write = section;
                if (baseVersion is not null && ConfigMerge.Version(stored) != baseVersion)
                {
                    var start = _versions.Get(baseVersion);
                    var merge = start is null ? null : ConfigMerge.Merge3(start, stored, section);
                    if (merge is null || !saveBoth || merge.Conflicts.Count > 0)
                    {
                        refused = Refusal(start, stored, section, merge);
                        return false;
                    }
                    write = merge.Result!;
                }
                stored.Clear();
                foreach (var (key, value) in write) stored[key] = value?.DeepClone();
                stored[ConfigMerge.LastSavedKey] = new JsonObject
                {
                    ["By"] = call.User ?? "Hive BDC dashboard",
                    ["At"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                };
                written = (JsonObject)stored.DeepClone();
                return true;
            });
        }
        catch (Exception ex)
        { Respond(call, 500, new { ok = false, errors = new[] { $"Failed to write the settings ({_settings.Where}): " + ex.Message } }); return; }
        if (refused is not null)
        {
            _log.LogWarning("Settings save by {User} refused: someone saved after the editor opened them.", ForLog(call.User ?? "dashboard"));
            Respond(call, 409, refused);
            return;
        }
        string newVersion = "";
        if (written is not null)
        {
            newVersion = ConfigMerge.Version(written);
            _versions.Remember(newVersion, written);
        }

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
        Respond(call, 200, new { ok = true, version = newVersion });
    }

    /// <summary>The 409 answer: who saved, what each side changed, and which settings both changed.</summary>
    private static object Refusal(JsonObject? start, JsonObject? theirs, JsonObject mine, SettingsMerge? merge)
    {
        var last = theirs?[ConfigMerge.LastSavedKey] as JsonObject;
        object Shown(SettingChange c) => new { path = c.Path, before = c.Before, after = c.After };
        var (their, yours) = start is null ? ([], []) : ConfigMerge.ChangeLists(start, theirs, mine);
        return new
        {
            ok = false,
            conflict = true,
            baseKnown = start is not null,
            savedBy = (string?)last?["By"] ?? "",
            savedAt = (string?)last?["At"] ?? "",
            version = ConfigMerge.Version(theirs),
            theirChanges = their.Select(Shown).ToArray(),
            yourChanges = yours.Select(Shown).ToArray(),
            overlaps = merge?.Conflicts.ToArray() ?? [],
            errors = new[] { start is null
                ? "The settings were saved by someone else after you opened them. Load them again, then make your change."
                : "The settings were saved by someone else after you opened them. Nothing of yours was saved yet." },
        };
    }

    /// <summary>The settings as GET /config or a save last gave them, by version: where an editor started from.</summary>
    private sealed class SettingsVersions
    {
        private const int Keep = 32;
        private readonly Dictionary<string, JsonObject> _byVersion = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public void Remember(string version, JsonObject section)
        {
            lock (_byVersion)
            {
                if (_byVersion.ContainsKey(version)) return;
                _byVersion[version] = (JsonObject)section.DeepClone();
                _order.Enqueue(version);
                while (_order.Count > Keep) _byVersion.Remove(_order.Dequeue());
            }
        }

        public JsonObject? Get(string version)
        {
            lock (_byVersion) return _byVersion.TryGetValue(version, out var s) ? (JsonObject)s.DeepClone() : null;
        }
    }

    private readonly SettingsVersions _versions = new();

    // ── request DTOs (camelCase JSON, case-insensitive binding) ─────────────

    /// <summary>POST /config body — the settings payload (same shape as GET /config).</summary>
    /// <summary>
    /// The Connections array to store: the posted list with new secrets protected and blank ones taken from the stored
    /// connection of the same id, or the stored list unchanged when the dashboard sent none.
    /// </summary>
    internal static JsonNode ConnectionsNode(IReadOnlyList<ConnectionPostDtoView>? posted, JsonObject? stored, Func<string, string?, string>? protect = null)
    {
        if (posted is null) return stored?["Connections"]?.DeepClone() ?? new JsonArray();
        var previous = (stored?["Connections"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var result = new JsonArray();
        foreach (var c in posted)
        {
            string id = c.Id?.Trim() ?? "";
            if (id.Length == 0) continue;
            string secret = !string.IsNullOrWhiteSpace(c.Secret)
                ? (protect ?? SecretProtector.Protect)(c.Secret, "connection-" + id)
                : previous.FirstOrDefault(p => string.Equals((string?)p["Id"], id, StringComparison.OrdinalIgnoreCase))?["Secret"]?.GetValue<string>() ?? "";
            var settings = new JsonObject();
            foreach (var (k, v) in c.Settings ?? new()) settings[k] = v ?? "";
            result.Add(new JsonObject
            {
                ["Id"] = id,
                ["Kind"] = TargetKinds.Clean(c.Kind),
                ["Settings"] = settings,
                ["Secret"] = secret,
            });
        }
        return result;
    }

    private static JsonNode ConnectionsNode(List<ConnectionPostDto>? posted, JsonObject? stored, Func<string, string?, string> protect)
        => ConnectionsNode(posted?.Select(c => new ConnectionPostDtoView(c.Id, c.Kind, c.Settings, c.Secret)).ToList(), stored, protect);

    internal sealed record ConnectionPostDtoView(string? Id, string? Kind, Dictionary<string, string>? Settings, string? Secret);

    private sealed class ConfigPostDto
    {
        public int? PollSeconds { get; set; }
        public string? LogDir { get; set; }
        public LicDto? License { get; set; }
        public AuthPostDto? SharePointAuth { get; set; }
        public MailPostDto? Mail { get; set; }
        /// <summary>Absent => keep the stored document types untouched.</summary>
        public List<DocumentTypePostDto>? DocumentTypes { get; set; }
        /// <summary>Absent => keep the stored connections untouched. A blank secret keeps the stored one.</summary>
        public List<ConnectionPostDto>? Connections { get; set; }
        /// <summary>Absent => keep the stored flows untouched. Stored as sent; the service validates them when it runs them.</summary>
        public JsonArray? Flows { get; set; }
        /// <summary>The version GET /config gave the editor; absent (the Windows app) => saved as before.</summary>
        public string? BaseVersion { get; set; }
        /// <summary>After a refusal: merge with the newer settings when no setting overlaps.</summary>
        public bool? SaveBoth { get; set; }
        /// <summary>Absent (the Windows app) => the stored pause is kept.</summary>
        public bool? Paused { get; set; }
        /// <summary>Absent => the stored marker of the set-up assistant is kept.</summary>
        public bool? SetupOpen { get; set; }
    }

    private sealed class ConnectionPostDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public Dictionary<string, string>? Settings { get; set; }
        public string? Secret { get; set; }
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
        /// <summary>Absent => the stored switch stays (a dashboard before 1.12.0).</summary>
        public bool? CompressPdf { get; set; }
        public List<RoutePostDto>? Companies { get; set; }
    }

    /// <summary>One company entry of a document type on POST.</summary>
    private sealed class RoutePostDto
    {
        public string? DataAreaId { get; set; }
        public string? Input { get; set; }
        public string? InputFormat { get; set; }
        public string? Output { get; set; }
        /// <summary>Absent => on (a dashboard before 1.5.0).</summary>
        public bool? Enabled { get; set; }
        /// <summary>Absent => the entry follows the type's Mail (a dashboard before 1.5.0).</summary>
        public bool? Mail { get; set; }
        /// <summary>Absent => the stored switch stays (a dashboard before 1.12.0).</summary>
        public bool? CompressPdf { get; set; }
        /// <summary>Absent => the stored Cc addresses stay (a dashboard before 1.13.0).</summary>
        public List<string>? MailCc { get; set; }
        /// <summary>Absent => the stored Bcc addresses stay (a dashboard before 1.14.0).</summary>
        public List<string>? MailBcc { get; set; }
        public CompanySpDto? SharePoint { get; set; }
        public List<TargetPostDto>? Targets { get; set; }
        /// <summary>Absent => keep the stored source (a dashboard that does not know sources).</summary>
        public SourceOptions? Source { get; set; }
    }

    /// <summary>One further target of a company entry on POST. Absent list = keep the stored targets.</summary>
    private sealed class TargetPostDto
    {
        public string? Kind { get; set; }
        public string? Connection { get; set; }
        public bool? Enabled { get; set; }
        public Dictionary<string, string>? Settings { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
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
