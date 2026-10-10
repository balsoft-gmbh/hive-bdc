namespace Balsoft.Hive.BDC.Configuration;

/// <summary>
/// Immutable SharePoint upload target (site + library + folder). In the multi-company
/// model this is built from a company's <see cref="SharePointTargetOptions"/> — the
/// company config is authoritative; there is no per-document JSON target.
/// </summary>
public sealed record SharePointTarget(string SiteUrl, string Library, string Folder)
{
    /// <summary>Stable cache key for the resolved drive (site + library, case-insensitive).</summary>
    public string CacheKey => $"{SiteUrl.TrimEnd('/', '\\')}|{Library}".ToLowerInvariant();
}

/// <summary>
/// SHARED SharePoint auth, bound from "HiveBDC:SharePointAuth". One tenant/app
/// registration is used for EVERY company. ClientSecret is plaintext or DPAPI-encrypted
/// ("enc:..." as written by the installer / loopback /config). The <see cref="SharePointUploader"/>
/// is constructed from this once and reused across all company targets.
/// </summary>
public sealed class SharePointAuthOptions
{
    /// <summary>Entra ID tenant (GUID or tenant.onmicrosoft.com).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>App registration (client) ID with application permission Sites.ReadWrite.All or Sites.Selected.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret — plaintext, or DPAPI-encrypted ("enc:...").</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Auth-only configuration problems, empty when usable. The upload TARGET is validated per company (see <see cref="SharePointTargetOptions.ResolveTarget"/>).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(TenantId)) problems.Add("SharePointAuth.TenantId is empty.");
        if (string.IsNullOrWhiteSpace(ClientId)) problems.Add("SharePointAuth.ClientId is empty.");
        if (string.IsNullOrWhiteSpace(ClientSecret)) problems.Add("SharePointAuth.ClientSecret is empty.");
        return problems;
    }
}

/// <summary>
/// PER-COMPANY SharePoint config, bound from "HiveBDC:Companies[i]:SharePoint".
/// Holds the upload TARGET (SiteUrl/Library/Folder), the per-company column
/// <see cref="Fields"/> mapping, and the <see cref="Enabled"/> toggle. Auth is NOT here —
/// it is shared (<see cref="SharePointAuthOptions"/>).
/// </summary>
public sealed class SharePointTargetOptions
{
    /// <summary>Whether uploads run for this company.</summary>
    public bool Enabled { get; set; }

    /// <summary>Full site URL, e.g. https://contoso.sharepoint.com/sites/Invoices.</summary>
    public string SiteUrl { get; set; } = "";

    /// <summary>Document library display name.</summary>
    public string Library { get; set; } = "";

    /// <summary>Optional folder path inside the library (e.g. "2026/Invoices"). Blank = library root. Created if missing.</summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// SharePoint internal column name -> value template, resolved per company. Templates may
    /// contain {property} placeholders filled case-insensitively from the top level of the AX
    /// document JSON, e.g. "{DocumentNumber}". A {property:date} modifier normalizes to yyyy-MM-dd.
    /// Fields whose resolved value is empty are skipped (never overwritten with "").
    /// </summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>
    /// Bring the typed values to one canonical form, so "…/sites/X/" and "…/sites/X", or
    /// "/Library/", "Library/" and "Library", configure the same target (and log, meter and
    /// cache as the same target). Idempotent.
    /// </summary>
    public void Normalize()
    {
        SiteUrl = CleanSiteUrl(SiteUrl);
        Library = CleanLibrary(Library);
        Folder = CleanFolder(Folder);
        Fields ??= new();
    }

    /// <summary>Trimmed, without trailing slashes: "https://t.sharepoint.com/sites/X/" -> "https://t.sharepoint.com/sites/X".</summary>
    public static string CleanSiteUrl(string? value) => (value ?? "").Trim().TrimEnd('/', '\\').Trim();

    /// <summary>
    /// A library as typed: the name from its URL, its display name, or a pasted library URL.
    /// Slashes around it are dropped ("/OrderConfirmations/" -> "OrderConfirmations"); a pasted
    /// URL or path keeps only the library segment ("…/OrderConfirmations/Forms/AllItems.aspx").
    /// A display name with spaces or '&amp;' is kept as is (trimmed).
    /// </summary>
    public static string CleanLibrary(string? value)
    {
        string s = (value ?? "").Trim().Replace('\\', '/').Trim('/').Trim();
        if (!s.Contains('/')) return s;
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        int forms = parts.FindIndex(p => p.Equals("Forms", StringComparison.OrdinalIgnoreCase));
        if (forms > 0) parts = parts.Take(forms).ToList();
        return parts.Count == 0 ? "" : Uri.UnescapeDataString(parts[^1]).Trim();
    }

    /// <summary>A folder inside the library: forward slashes, no leading/trailing or doubled slashes.</summary>
    public static string CleanFolder(string? value)
    {
        var parts = (value ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0);
        return string.Join("/", parts);
    }

    /// <summary>True when a usable Site+Library target is present.</summary>
    public bool HasTarget =>
        !string.IsNullOrWhiteSpace(SiteUrl) && !string.IsNullOrWhiteSpace(Library);

    /// <summary>
    /// Build the upload target from this company's config. Returns null plus a reason
    /// when SiteUrl/Library are missing or the site URL is not a valid https URL.
    /// </summary>
    public SharePointTarget? ResolveTarget(out string? problem)
    {
        problem = null;
        if (!HasTarget)
        {
            problem = "SharePoint SiteUrl/Library is not configured for this company.";
            return null;
        }
        if (!Uri.TryCreate(CleanSiteUrl(SiteUrl), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            problem = $"SharePoint site URL '{SiteUrl}' is not a valid https URL.";
            return null;
        }
        return new SharePointTarget(CleanSiteUrl(SiteUrl), CleanLibrary(Library), CleanFolder(Folder));
    }
}

