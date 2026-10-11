namespace Balsoft.Hive.BDC.Configuration;

/// <summary>
/// Root configuration for the service, bound from the "HiveBDC" config
/// section (appsettings.json / env vars HiveBDC__* / CLI overrides).
///
/// DOCUMENT TYPES (v2.0): <see cref="DocumentTypes"/> lists what the service dispatches
/// (sales invoice as e-invoice, confirmations, quotations, collection letters, purchase
/// orders, payment advices as PDF, plus anything the ERP sends that was registered on
/// first sight). Each type carries its own list of AX companies with folders and a
/// SharePoint target (<see cref="DocumentTypeOptions.Companies"/>); that is the ONLY
/// routing model since 2.0.3.
///
/// BACKWARD COMPAT: the 1.x / 2.0.0-2.0.2 shapes still bind: a top-level
/// <see cref="Companies"/> list (per-company folders + target, optionally with per-type
/// overrides), and the even older single-config Input/Output/SharePoint node. Both are
/// folded into per-type company entries by <see cref="Normalize"/>; the next dashboard save
/// persists the new shape.
/// </summary>
public sealed class BDCOptions
{
    public int PollSeconds { get; set; } = 30;

    /// <summary>Folder for rolling daily log files. Blank = %ProgramData%\HiveBDC\logs.</summary>
    public string LogDir { get; set; } = "";

    /// <summary>Licensing ("HiveBDC:License" section). File path + enforcement toggle.</summary>
    public LicenseOptions License { get; set; } = new();

    /// <summary>SHARED SharePoint auth (TenantId/ClientId/ClientSecret) used for every upload.</summary>
    public SharePointAuthOptions SharePointAuth { get; set; } = new();

    /// <summary>SHARED mail config ("HiveBDC:Mail"): own app registration, sender mailbox, redirect list.</summary>
    public MailOptions Mail { get; set; } = new();

    /// <summary>The document types the service dispatches, each with its company entries. Missing built-ins are re-added by <see cref="Normalize"/>.</summary>
    public List<DocumentTypeOptions> DocumentTypes { get; set; } = new();

    // ── LEGACY members (older schemas), folded into DocumentTypes by Normalize ─────────

    /// <summary>LEGACY (1.4 to 2.0.2): company-level folders + target. Migrated into every type's Companies.</summary>
    public List<CompanyProfile> Companies { get; set; } = new();

    /// <summary>LEGACY (pre-1.4): old top-level Input folder.</summary>
    public string? Input { get; set; }

    /// <summary>LEGACY (pre-1.4): old top-level Output folder.</summary>
    public string? Output { get; set; }

    /// <summary>LEGACY (pre-1.4): single SharePoint node carrying both auth and target.</summary>
    public LegacySharePointConfig? SharePoint { get; set; }

    /// <summary>
    /// Fold the pre-1.4 single-config members into one legacy company. Idempotent.
    /// </summary>
    public void MigrateLegacy()
    {
        if (Companies.Count > 0) { ClearLegacy(); return; }

        bool hasLegacy = !string.IsNullOrWhiteSpace(Input) || SharePoint is not null;
        if (!hasLegacy) return;

        var legacy = SharePoint;
        if (legacy is not null
            && string.IsNullOrWhiteSpace(SharePointAuth.TenantId)
            && string.IsNullOrWhiteSpace(SharePointAuth.ClientId)
            && string.IsNullOrWhiteSpace(SharePointAuth.ClientSecret))
        {
            SharePointAuth = new SharePointAuthOptions
            {
                TenantId = legacy.TenantId ?? "",
                ClientId = legacy.ClientId ?? "",
                ClientSecret = legacy.ClientSecret ?? "",
            };
        }

        Companies.Add(new CompanyProfile
        {
            DataAreaId = "default",
            Input = Input ?? "",
            Output = Output ?? "",
            SharePoint = new SharePointTargetOptions
            {
                Enabled = legacy?.Enabled ?? false,
                SiteUrl = legacy?.SiteUrl ?? "",
                Library = legacy?.Library ?? "",
                Folder = legacy?.Folder ?? "",
                Fields = legacy?.Fields ?? new(),
            },
        });

        ClearLegacy();
    }

    private void ClearLegacy()
    {
        Input = null;
        Output = null;
        SharePoint = null;
    }

    /// <summary>Fill in derived defaults and migrate older shapes. Call after binding. Idempotent.</summary>
    public void Normalize()
    {
        MigrateLegacy();
        if (PollSeconds < 1)
            PollSeconds = 1;
        Mail ??= new MailOptions();
        Mail.Normalize();
        NormalizeDocumentTypes();
        MigrateCompaniesIntoTypes();
    }

    /// <summary>
    /// Trim every configured type, drop blank/duplicate ids (first wins), and append any
    /// built-in type that is missing, so a hand-edited appsettings without the six defaults
    /// still lists all of them. Order: as configured, then the missing built-ins.
    /// </summary>
    private void NormalizeDocumentTypes()
    {
        DocumentTypes ??= new();
        var clean = new List<DocumentTypeOptions>();
        foreach (var t in DocumentTypes)
        {
            if (t is null) continue;
            t.Normalize();
            if (t.Id.Length == 0) continue;
            if (clean.Any(x => x.Matches(t.Id))) continue;
            clean.Add(t);
        }
        foreach (var b in DocumentTypeCatalog.BuiltIn)
            if (!clean.Any(x => x.Matches(b.Id)))
                clean.Add(b);
        DocumentTypes = clean;
    }

    /// <summary>
    /// 2.0.2 and older kept folders and targets on a company list, optionally with per-type
    /// overrides. Give every type that has no company entries one entry per legacy company
    /// (folder + target, overrides applied), then forget the legacy list. A type that already
    /// has entries is left alone, so a half-migrated file is not doubled.
    /// </summary>
    private void MigrateCompaniesIntoTypes()
    {
        Companies ??= new();
        if (Companies.Count == 0) return;
        foreach (var c in Companies) c.Normalize();

        foreach (var t in DocumentTypes)
        {
            if (t.Companies.Count > 0) continue;
            foreach (var c in Companies)
            {
                var o = c.RoutingFor(t.Id);
                var sp = c.SharePointFor(t);
                t.Companies.Add(new DocumentRoute
                {
                    DataAreaId = c.DataAreaId,
                    Input = !string.IsNullOrWhiteSpace(o?.Input) ? o!.Input : c.Input,
                    Output = !string.IsNullOrWhiteSpace(o?.Output) ? o!.Output : c.Output,
                    SharePoint = sp,
                });
            }
        }
        Companies = new();
    }

    /// <summary>Every distinct folder to scan, with the (type, company) entries pointing at it.</summary>
    public IReadOnlyList<ScanFolder> ScanFolders() => ScanFolder.Build(DocumentTypes);

    /// <summary>All company entries of all types.</summary>
    public IEnumerable<DocumentRoute> AllRoutes() => DocumentTypes.SelectMany(t => t.Companies);

    /// <summary>Company entries of ENABLED types only: the ones that can actually deliver anything.</summary>
    public IEnumerable<DocumentRoute> ActiveRoutes() => DocumentTypes.Where(t => t.Enabled).SelectMany(t => t.Companies);

    /// <summary>Distinct dataAreaIds across every type, in first-seen order.</summary>
    public IReadOnlyList<string> CompanyIds()
        => AllRoutes().Select(r => r.DataAreaId).Where(id => id.Length > 0)
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Whether any company entry of an ENABLED type uploads to SharePoint (=> the shared uploader
    /// is needed). A disabled type never delivers, so its SharePoint flag must not pull up the
    /// Graph client or report "SharePoint on".
    /// </summary>
    public bool AnySharePointEnabled() => ActiveRoutes().Any(r => r.SharePoint.Enabled);

    /// <summary>Company entries of enabled types that upload to SharePoint.</summary>
    public int SharePointEntryCount() => ActiveRoutes().Count(r => r.SharePoint.Enabled);
}

/// <summary>
/// LEGACY (1.4 to 2.0.2) company profile: its own scan folders and SharePoint target, with
/// optional per-type overrides. Bound only to migrate an older appsettings; not used at
/// runtime after <see cref="BDCOptions.Normalize"/>.
/// </summary>
public sealed class CompanyProfile
{
    public string DataAreaId { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public SharePointTargetOptions SharePoint { get; set; } = new();
    public Dictionary<string, CompanyDocumentTypeOptions> DocumentTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Input))
            Input = @"C:\Temp\HiveBDC";
        SharePoint ??= new SharePointTargetOptions();
        var clean = new Dictionary<string, CompanyDocumentTypeOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in DocumentTypes ?? new())
        {
            string id = DocumentTypeCatalog.CleanId(key);
            if (id.Length == 0 || value is null) continue;
            value.Normalize();
            clean[id] = value;
        }
        DocumentTypes = clean;
    }

    public CompanyDocumentTypeOptions? RoutingFor(string typeId)
        => DocumentTypes.TryGetValue(typeId, out var r) ? r : null;

    /// <summary>The effective legacy target for one type: company target with the per-type override layered on top.</summary>
    public SharePointTargetOptions SharePointFor(DocumentTypeOptions type)
    {
        var r = RoutingFor(type.Id)?.SharePoint;
        var fields = new Dictionary<string, string>(SharePoint.Fields, StringComparer.OrdinalIgnoreCase);
        if (r is not null) foreach (var (k, v) in r.Fields) fields[k] = v;
        return new SharePointTargetOptions
        {
            Enabled = r?.Enabled ?? SharePoint.Enabled,
            SiteUrl = !string.IsNullOrWhiteSpace(r?.SiteUrl) ? r!.SiteUrl : SharePoint.SiteUrl,
            Library = !string.IsNullOrWhiteSpace(r?.Library) ? r!.Library : SharePoint.Library,
            Folder = !string.IsNullOrWhiteSpace(r?.Folder) ? r!.Folder : SharePoint.Folder,
            Fields = fields,
        };
    }
}

/// <summary>LEGACY (2.0.0 to 2.0.2) per-company, per-type routing override.</summary>
public sealed class CompanyDocumentTypeOptions
{
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public CompanyDocumentSharePointOptions SharePoint { get; set; } = new();

    public void Normalize()
    {
        Input = Input?.Trim() ?? "";
        Output = Output?.Trim() ?? "";
        SharePoint ??= new CompanyDocumentSharePointOptions();
        SharePoint.Fields ??= new();
    }
}

/// <summary>LEGACY (2.0.0 to 2.0.2) SharePoint override; null Enabled = inherit.</summary>
public sealed class CompanyDocumentSharePointOptions
{
    public bool? Enabled { get; set; }
    public string SiteUrl { get; set; } = "";
    public string Library { get; set; } = "";
    public string Folder { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();
}

/// <summary>
/// LEGACY (pre-1.4) single SharePoint node: carries BOTH auth (TenantId/ClientId/ClientSecret)
/// AND target (SiteUrl/Library/Folder/Fields). Bound only to migrate an old appsettings.
/// </summary>
public sealed class LegacySharePointConfig
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string Library { get; set; } = "";
    public string Folder { get; set; } = "";
    public string DriveId { get; set; } = "";   // tolerated old key; unused by the new model
    public Dictionary<string, string> Fields { get; set; } = new();
}
