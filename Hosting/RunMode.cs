namespace Balsoft.Hive.BDC.Hosting;

/// <summary>How the program runs: on the customer's server (Windows service, SQLite, DPAPI) or as an Azure web app.</summary>
public static class RunModes
{
    public const string Local = "local";
    public const string Azure = "azure";

    public static string Clean(string? mode) => string.IsNullOrWhiteSpace(mode) ? Local : mode.Trim().ToLowerInvariant();
}

/// <summary>
/// The Azure run mode's own settings ("HiveBDC:Azure", in App Service the app settings HiveBDC__Azure__*). They are
/// set by the deployment, never by the dashboard, so they live outside the settings file.
/// </summary>
public sealed class AzureRunOptions
{
    /// <summary>https://{account}.table.core.windows.net, signed in with the app's managed identity.</summary>
    public string TableEndpoint { get; set; } = "";

    /// <summary>A storage connection string instead of the endpoint; a "kv:" reference when a Key Vault is set.</summary>
    public string StorageConnection { get; set; } = "";

    public string TablePrefix { get; set; } = "HiveBDC";

    /// <summary>https://{vault}.vault.azure.net; every secret in the settings is then a "kv:" reference.</summary>
    public string KeyVaultUrl { get; set; } = "";

    /// <summary>
    /// https://{account}.blob.core.windows.net/{container}/{name}.json: the settings of this instance, read in place of the
    /// package's appsettings.json (read-only in Azure) and written back by the service.
    /// </summary>
    public string SettingsBlobUrl { get; set; } = "";

    /// <summary>How often the settings blob is checked for a change.</summary>
    public int SettingsPollSeconds { get; set; } = 15;
}

/// <summary>
/// What in the settings only a local install has (failure case 18): reading a folder the ERP writes to, Word files from
/// a folder, a CSV written to a folder, delivery only to a folder, and secrets kept in the settings themselves. Each
/// problem names the document type and company or the inbound flow, so the log says what to change.
/// </summary>
public static class AzureSettingsCheck
{
    private const string VaultPrefix = "kv:";

    public static IReadOnlyList<string> Problems(BDCOptions opts)
    {
        var problems = new List<string>();
        foreach (var type in opts.DocumentTypes.Where(t => t.Enabled))
            foreach (var route in type.Companies.Where(r => r.IsOn))
            {
                string name = $"Outbound {type.Id} for company {route.DataAreaId}";
                if (InputFormats.IsWord(route.InputFormat))
                    problems.Add($"{name} reads Word files from a folder, which an Azure instance does not have.");
                else if (route.Source is not { Kind: "blob" or ConnectionKinds.ServiceBus })
                    problems.Add($"{name} reads the folder '{route.Input}'; in Azure documents come from Blob Storage or Service Bus (source kind blob or servicebus).");
                if (WindowsPath(route.Input) || WindowsPath(route.Output))
                    problems.Add($"{name} uses a Windows path ('{(WindowsPath(route.Input) ? route.Input : route.Output)}') as its work folder.");
                bool delivers = route.SharePoint.Enabled || route.Targets.Any(t => t.Enabled) || (route.MailsFor(type) && opts.Mail.Enabled);
                if (!delivers)
                    problems.Add($"{name} only writes to the folder '{route.Output}'; in Azure it needs SharePoint, DocuWare, Blob Storage or mail.");
            }

        foreach (var flow in opts.Flows.Where(f => f.Enabled))
            foreach (var target in flow.Targets.Where(t => t.Enabled && t.Kind == "folder"))
                problems.Add($"Inbound flow '{(flow.Name.Length > 0 ? flow.Name : flow.Id)}' writes to the folder '{target.Setting("Folder")}', which an Azure instance does not have.");

        void Secret(string where, string? value)
        {
            if (!string.IsNullOrEmpty(value) && !value.StartsWith(VaultPrefix, StringComparison.Ordinal))
                problems.Add($"The secret of {where} is kept in the settings; in Azure it must be a Key Vault reference (kv:name).");
        }
        Secret("the SharePoint app", opts.SharePointAuth.ClientSecret);
        Secret("the mail app", opts.Mail.ClientSecret);
        foreach (var c in opts.Connections) Secret($"connection '{c.Id}'", c.Secret);
        return problems;
    }

    private static bool WindowsPath(string? path)
        => !OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(path)
           && (path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'));
}

/// <summary>Whether the worker may take documents from their sources this cycle; a reason when it may not.</summary>
public interface IProcessingGate
{
    string? Hold(BDCOptions opts);
}

/// <summary>A local install has nothing to wait for beyond the licence, which the worker checks itself.</summary>
public sealed class LocalProcessingGate : IProcessingGate
{
    public string? Hold(BDCOptions opts) => null;
}

/// <summary>
/// The Azure run mode takes nothing from a source while its settings blob cannot be read, the settings use what only a
/// local install has (case 18), Table Storage cannot be reached (case 37), Key Vault cannot be reached (case 17) or the
/// tenant of the instance, which the licence is bound to, cannot be determined. The documents stay where they are and
/// are taken once the reason is gone.
/// </summary>
public sealed class AzureProcessingGate : IProcessingGate
{
    private readonly TableStateStore _state;
    private readonly SecretResolver _secrets;
    private readonly Func<string?> _settingsProblem;
    private readonly InstanceTenant? _tenant;

    public AzureProcessingGate(TableStateStore state, SecretResolver secrets, Func<string?>? settingsProblem = null,
        InstanceTenant? tenant = null)
    {
        _state = state;
        _secrets = secrets;
        _settingsProblem = settingsProblem ?? (() => null);
        _tenant = tenant;
    }

    public string? Hold(BDCOptions opts)
    {
        if (_settingsProblem() is { } unreadable)
            return "the settings in Blob Storage cannot be read: " + unreadable;
        var problems = AzureSettingsCheck.Problems(opts);
        if (problems.Count > 0)
            return "the settings use what only a local install has: " + string.Join(" ", problems);
        if (!_state.Reachable(out var stateProblem))
            return "Table Storage cannot be reached: " + stateProblem;
        if (_secrets.UsesVault && !_secrets.VaultReachable(out var vaultProblem))
            return "Key Vault cannot be reached: " + vaultProblem;
        if (_tenant is not null && !_tenant.Resolve(out _, out var tenantProblem))
            return "the tenant of this instance cannot be determined: " + tenantProblem;
        return null;
    }
}