namespace Balsoft.Hive.BDC.Configuration;

/// <summary>The target kinds this version ships. A kind is a plain string in the settings, so a
/// file written by a newer version loads here and only the entries using an unknown kind are held.</summary>
public static class TargetKinds
{
    public const string SharePoint = "sharepoint";
    public const string Mail = "mail";

    public static string Clean(string? kind) => (kind ?? "").Trim().ToLowerInvariant();
}

/// <summary>
/// One more place a company entry delivers its documents to ("DocumentTypes[i]:Companies[j]:Targets[k]").
/// The entry's own SharePoint block stays the first target and mail stays a switch on the document type;
/// this list adds further targets of any kind, each with its own settings and field templates.
/// </summary>
public sealed class TargetOptions
{
    /// <summary>Connector kind, e.g. "sharepoint".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Id of the connection the target signs in with. Blank = the kind's built-in connection.</summary>
    public string Connection { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>What the kind needs to find its destination (SharePoint: SiteUrl, Library, Folder).</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Destination field name -> value template, the same templates as the SharePoint columns.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    public string Setting(string name) => Settings.TryGetValue(name, out var v) ? v?.Trim() ?? "" : "";

    /// <summary>Settings shown to people only; they do not change where a document goes.</summary>
    private static readonly HashSet<string> DisplayOnly = new(StringComparer.OrdinalIgnoreCase) { "FileCabinetName" };

    /// <summary>Identifies this destination among the targets of all entries: kind, connection and settings.</summary>
    public string Key => string.Join("|", new[] { Kind, Connection }
        .Concat(Settings.Where(s => !DisplayOnly.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Key.ToLowerInvariant() + "=" + (s.Value ?? "").Trim().TrimEnd('/', '\\').ToLowerInvariant())));

    public void Normalize()
    {
        Kind = TargetKinds.Clean(Kind);
        Connection = Connection?.Trim() ?? "";
        Settings = new Dictionary<string, string>(Settings ?? new(), StringComparer.OrdinalIgnoreCase);
        Fields ??= new();
    }

    public TargetOptions Clone() => new()
    {
        Kind = Kind,
        Connection = Connection,
        Enabled = Enabled,
        Settings = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase),
        Fields = new Dictionary<string, string>(Fields),
    };

    /// <summary>The entry's SharePoint block as a target, so one code path delivers both.</summary>
    public static TargetOptions FromSharePoint(SharePointTargetOptions sp) => new()
    {
        Kind = TargetKinds.SharePoint,
        Enabled = sp.Enabled,
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SiteUrl"] = sp.SiteUrl,
            ["Library"] = sp.Library,
            ["Folder"] = sp.Folder,
        },
        Fields = sp.Fields,
    };

    public SharePointTargetOptions ToSharePoint() => new()
    {
        Enabled = Enabled,
        SiteUrl = Setting("SiteUrl"),
        Library = Setting("Library"),
        Folder = Setting("Folder"),
        Fields = Fields,
    };
}