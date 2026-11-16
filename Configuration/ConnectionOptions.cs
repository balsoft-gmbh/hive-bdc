namespace Balsoft.Hive.BDC.Configuration;

/// <summary>The connection kinds this version ships.</summary>
public static class ConnectionKinds
{
    public const string DocuWare = "docuware";
    public const string AzureStorage = "azure-storage";
}

/// <summary>
/// A named sign-in to an outside system ("HiveBDC:Connections[i]"), defined once and used by any number of targets.
/// <see cref="Secret"/> holds the password or key, protected on the machine ("enc:"); it is never sent back to the
/// dashboard.
/// </summary>
public sealed class ConnectionOptions
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    /// <summary>What the kind needs besides the secret (DocuWare: Url, UserName).</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Secret { get; set; } = "";

    public string Setting(string name) => Settings.TryGetValue(name, out var v) ? v?.Trim() ?? "" : "";

    public void Normalize()
    {
        Id = Id?.Trim() ?? "";
        Kind = TargetKinds.Clean(Kind);
        Settings = new Dictionary<string, string>(Settings ?? new(), StringComparer.OrdinalIgnoreCase);
        Secret ??= "";
    }

    /// <summary>What is missing before this connection can sign in, in words for the Settings screen; empty when complete.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Id.Length == 0) problems.Add("The connection has no name.");
        if (Kind == ConnectionKinds.DocuWare)
        {
            if (!Uri.TryCreate(Setting("Url"), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                problems.Add($"Connection '{Id}': the DocuWare address must start with https://, for example https://yourcompany.docuware.cloud.");
            if (Setting("UserName").Length == 0) problems.Add($"Connection '{Id}': the DocuWare user name is empty.");
            if (Secret.Length == 0) problems.Add($"Connection '{Id}': the DocuWare password is empty.");
        }
        if (Kind == ConnectionKinds.AzureStorage && Secret.Length == 0
            && !(Uri.TryCreate(Setting("AccountUrl"), UriKind.Absolute, out var account) && account.Scheme == Uri.UriSchemeHttps))
            problems.Add($"Connection '{Id}': give the storage connection string, or in Azure the account address (https://<account>.blob.core.windows.net) for the managed identity.");
        return problems;
    }
}