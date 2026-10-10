namespace Balsoft.Hive.BDC.Configuration;

/// <summary>
/// SHARED mail configuration, bound from "HiveBDC:Mail". One app registration sends
/// for every company, the same way <see cref="SharePointAuthOptions"/> uploads for every
/// company. The tenant is the SharePoint tenant unless <see cref="TenantId"/> overrides it,
/// but the app registration is separate: sending mail needs the application permission
/// <c>Mail.Send</c>, which is not what the SharePoint app is granted.
///
/// <see cref="ClientSecret"/> is plaintext or DPAPI-encrypted ("enc:...", as written by the
/// loopback /config endpoint), exactly like the SharePoint secret, and is never returned by
/// the status API.
/// </summary>
public sealed class MailOptions
{
    /// <summary>Whether the service mails each produced document after a successful export (per type, see DocumentTypeOptions.Mail).</summary>
    public bool Enabled { get; set; }

    /// <summary>Entra ID tenant. Blank => the SharePoint tenant (<see cref="SharePointAuthOptions.TenantId"/>).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>App registration (client) ID holding the application permission Mail.Send.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret — plaintext, or DPAPI-encrypted ("enc:...").</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Mailbox documents are sent as, e.g. rechnung@contoso.de. Must exist in the tenant.</summary>
    public string From { get; set; } = "";

    /// <summary>
    /// Where mail goes when the document did NOT come from a production AX
    /// (see <see cref="SourceEnvironment"/>). The partner address in the JSON is then
    /// ignored and only reported in the body, so a test document can never reach a customer
    /// or vendor. Required as soon as mail is enabled: without it a non-production document
    /// has nowhere safe to go.
    /// </summary>
    public List<string> RedirectTo { get; set; } = new();

    /// <summary>Keep a copy in the sender mailbox's Sent Items. Default true.</summary>
    public bool SaveToSentItems { get; set; } = true;

    /// <summary>The tenant actually used: <see cref="TenantId"/> when set, otherwise the SharePoint tenant.</summary>
    public string ResolveTenantId(SharePointAuthOptions? sharePointAuth)
        => string.IsNullOrWhiteSpace(TenantId) ? sharePointAuth?.TenantId ?? "" : TenantId;

    /// <summary>Configuration problems, empty when usable.</summary>
    public IReadOnlyList<string> Validate(SharePointAuthOptions? sharePointAuth)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ResolveTenantId(sharePointAuth)))
            problems.Add("Mail.TenantId is empty and no SharePoint tenant is configured to fall back on.");
        if (string.IsNullOrWhiteSpace(ClientId)) problems.Add("Mail.ClientId is empty.");
        if (string.IsNullOrWhiteSpace(ClientSecret)) problems.Add("Mail.ClientSecret is empty.");
        if (string.IsNullOrWhiteSpace(From)) problems.Add("Mail.From is empty.");
        else if (!MailAddresses.LooksLikeAddress(From)) problems.Add($"Mail.From '{From}' is not a valid e-mail address.");
        if (CleanRedirectTo().Count == 0)
            problems.Add("Mail.RedirectTo is empty — non-production documents would have nowhere to go.");
        foreach (var bad in CleanRedirectTo().Where(a => !MailAddresses.LooksLikeAddress(a)))
            problems.Add($"Mail.RedirectTo contains '{bad}', which is not a valid e-mail address.");
        return problems;
    }

    /// <summary>Redirect list without blanks or duplicates.</summary>
    public IReadOnlyList<string> CleanRedirectTo() => MailAddresses.Clean(RedirectTo);

    /// <summary>Fill derived defaults. Idempotent.</summary>
    public void Normalize()
    {
        RedirectTo ??= new List<string>();
        From = From?.Trim() ?? "";
        ClientId = ClientId?.Trim() ?? "";
        TenantId = TenantId?.Trim() ?? "";
    }
}

/// <summary>
/// The AX environment a document was exported from, carried in the JSON as
/// <c>environment</c> ("Live" / "Test" / "Dev") by <c>BDCEnvironmentHelper</c> on the AX side.
/// </summary>
public static class SourceEnvironment
{
    public const string Live = "Live";

    /// <summary>
    /// Whether this document came from production. Anything else, INCLUDING a missing or
    /// unrecognized value, is not production: an export that cannot prove where it came
    /// from must never reach a partner mailbox.
    /// </summary>
    public static bool IsLive(string? environment)
        => string.Equals(environment?.Trim(), Live, StringComparison.OrdinalIgnoreCase);

    /// <summary>Display form of the environment, for logs and the redirect banner.</summary>
    public static string Describe(string? environment)
        => string.IsNullOrWhiteSpace(environment) ? "unknown" : environment.Trim();
}

/// <summary>Parsing and tidying of the address lists that arrive from AX and from settings.</summary>
public static class MailAddresses
{
    private static readonly char[] Separators = { ';', ',' };

    /// <summary>Split an AX address field ("a@x.de; b@y.de") into clean, de-duplicated addresses.</summary>
    public static IReadOnlyList<string> Split(string? value)
        => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : Clean(new[] { value });

    /// <summary>
    /// Trim, drop blanks, drop duplicates (case-insensitive), keep order. Each entry is
    /// itself split on ; and , first, so one string holding several addresses is accepted
    /// wherever a list is: pasting "a@x.de; b@y.de" into the dashboard, or writing it as a
    /// single entry in appsettings.json, both give two recipients rather than one bad one.
    /// </summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string>? addresses)
    {
        if (addresses is null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var entry in addresses)
        {
            if (entry is null) continue;
            foreach (var a in entry.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = a.Trim();
                if (t.Length == 0) continue;
                if (seen.Add(t)) result.Add(t);
            }
        }
        return result;
    }

    /// <summary>Cheap shape check — enough to catch a typo in settings without pretending to validate RFC 5322.</summary>
    public static bool LooksLikeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && !value.Contains(' ') && value.IndexOf('@', at + 1) < 0;
    }
}
