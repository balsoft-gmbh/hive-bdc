using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>What a destination kind is and what a company's destination of that kind needs; Settings builds its page from this.</summary>
public sealed record TargetDescriptor(
    string Kind,
    string Name,
    string Description,
    string ConnectionKind,
    IReadOnlyList<SourceSetting> Settings,
    string? FieldsLookup);

/// <summary>
/// The Settings side of a destination kind: its description and the lists its dropdowns and field rows come from. The
/// delivery itself is the <see cref="IDocumentTarget"/> of the same kind.
/// </summary>
public interface IDestinationKind
{
    TargetDescriptor Descriptor { get; }

    bool IsConnected { get; }

    Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, TargetOptions target);
}

/// <summary>A DocuWare file cabinet: index fields filled from templates, a repeat updates the document found by the match field.</summary>
public sealed class DocuWareDestinationKind(SourceRuntime runtime, Func<ConnectionOptions, DocuWareClient> clients) : IDestinationKind
{
    public TargetDescriptor Descriptor { get; } = new(
        ConnectionKinds.DocuWare, "DocuWare file cabinet",
        "Stored with index values from the document. A repeat updates the same document.",
        ConnectionKinds.DocuWare,
        new[]
        {
            new SourceSetting("FileCabinet", "File cabinet", SourceSteps.Which, Lookup: "cabinets"),
            new SourceSetting("MatchField", "Never store twice: find an earlier version by", SourceSteps.Which, Required: false,
                Lookup: "matchfields", DependsOn: "FileCabinet",
                Hint: "A field that is the same for every version of a document, such as its number. Empty: every delivery stores a new document."),
        },
        "fields");

    private ConnectionOptions? Connection(TargetOptions target)
        => runtime.Connection(string.IsNullOrWhiteSpace(target.Connection) ? ConnectionKinds.DocuWare : target.Connection);

    public bool IsConnected => Connection(new TargetOptions()) is { } c && c.Problems().Count == 0;

    public async Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, TargetOptions target)
    {
        var connection = Connection(target);
        if (connection is null || connection.Problems().Count > 0)
            throw new ConnectorException($"Connect DocuWare under {SettingsPaths.Pick("Settings > Connections", "Connections")} first.", retryLater: false);
        var client = clients(connection);
        string cabinet = target.Setting("FileCabinet");
        switch (lookup)
        {
            case "cabinets":
                return (await client.CabinetsAsync()).Select(c => new SourceChoice(c.Id, c.Name)).ToList();
            case "fields":
                if (cabinet.Length == 0) return Array.Empty<SourceChoice>();
                var rows = new List<SourceChoice>();
                foreach (var f in (await client.CabinetAsync(cabinet)).Fields)
                {
                    if (f.Columns.Count > 0 || f.Type == "Table")
                    {
                        rows.AddRange(f.Columns.Select(c => new SourceChoice($"{f.Name}.{c.Name}", $"{f.DisplayName}: {c.DisplayName}", KindOf(c.Type))));
                        continue;
                    }
                    rows.Add(new SourceChoice(f.Name, f.DisplayName + (f.Required ? " (required)" : ""), KindOf(f.Type)));
                }
                return rows;
            case "matchfields":
                if (cabinet.Length == 0) return Array.Empty<SourceChoice>();
                return (await client.CabinetAsync(cabinet)).Fields.Where(f => f.Columns.Count == 0 && f.Type is "Text" or "Numeric")
                    .Select(f => new SourceChoice(f.Name, f.DisplayName)).ToList();
            default:
                return Array.Empty<SourceChoice>();
        }
    }

    private static string KindOf(string type) => type switch
    {
        "Date" => "date",
        "DateTime" => "date and time",
        "Decimal" => "amount",
        "Numeric" => "number",
        "Keyword" => "several values, separate with ;",
        _ => "text",
    };
}

/// <summary>A SharePoint document library (the company's SharePoint block): site, library, folder and the columns it fills.</summary>
public sealed class SharePointDestinationKind(SourceRuntime runtime) : IDestinationKind
{
    public TargetDescriptor Descriptor { get; } = new(
        TargetKinds.SharePoint, "SharePoint library",
        "Uploaded to a document library, with columns filled from the document.",
        TargetKinds.SharePoint,
        new[]
        {
            new SourceSetting("SiteUrl", "Site address", SourceSteps.Which, Hint: "https://yourcompany.sharepoint.com/sites/Name"),
            new SourceSetting("Library", "Library", SourceSteps.Which, Lookup: "libraries", DependsOn: "SiteUrl"),
            new SourceSetting("Folder", "Folder (optional)", SourceSteps.Which, Required: false),
        },
        "columns");

    public bool IsConnected => runtime.SharePointAuth is { } auth && auth.Validate().Count == 0;

    public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, TargetOptions target)
        => new SharePointRecordSource(runtime.SharePointAuth).ChoicesAsync(lookup,
            new SourceOptions { Kind = TargetKinds.SharePoint, Settings = new(target.Settings, StringComparer.OrdinalIgnoreCase) });
}

/// <summary>An Azure Blob container: the document under a prefix, and a status file if wanted.</summary>
public sealed class BlobDestinationKind(SourceRuntime runtime) : IDestinationKind
{
    public TargetDescriptor Descriptor { get; } = new(
        "blob", "Azure Blob container",
        "Uploaded to a container of an Azure storage account, for example for Dynamics 365 Finance and Operations.",
        ConnectionKinds.AzureStorage,
        new[]
        {
            new SourceSetting("Container", "Container", SourceSteps.Which),
            new SourceSetting("Prefix", "Folder in the container (optional)", SourceSteps.Which, Required: false, Hint: "For example outbound/"),
            new SourceSetting("Status", "Write a status file", SourceSteps.Which, Required: false, Lookup: "yesno",
                Hint: "{document id}.status.json next to the file says whether the delivery worked."),
        },
        null);

    public bool IsConnected => runtime.Options.Connections.Any(c => c.Kind == ConnectionKinds.AzureStorage && c.Problems().Count == 0);

    public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, TargetOptions target)
        => Task.FromResult<IReadOnlyList<SourceChoice>>(lookup == "yesno"
            ? new[] { new SourceChoice("true", "Yes"), new SourceChoice("false", "No") }
            : Array.Empty<SourceChoice>());
}

public sealed record DestinationInfo(string Kind, TargetDescriptor Descriptor, bool Connected);

/// <summary>What the dashboard asks about outbound destinations.</summary>
public static class DestinationCatalog
{
    public static IReadOnlyList<DestinationInfo> Describe(ConnectorRegistry registry, SourceRuntime runtime)
        => registry.DestinationKindNames
            .Select(kind => registry.Destination(kind, runtime))
            .Where(d => d is not null)
            .Select(d => new DestinationInfo(d!.Descriptor.Kind, d.Descriptor, d.IsConnected))
            .ToList();

    public static async Task<ChoicesResult> ChoicesAsync(ConnectorRegistry registry, SourceRuntime runtime, string kind, string lookup, TargetOptions target)
    {
        var destination = registry.Destination(kind, runtime);
        if (destination is null)
            return new ChoicesResult(Array.Empty<SourceChoice>(), $"The destination '{kind}' is not known to this version of Hive BDC.");
        try { return new ChoicesResult(await destination.ChoicesAsync(lookup, target), null); }
        catch (ConnectorException ex) { return new ChoicesResult(Array.Empty<SourceChoice>(), ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        { return new ChoicesResult(Array.Empty<SourceChoice>(), $"{destination.Descriptor.Name}: {ex.Message}"); }
    }
}

/// <summary>What a kind of connection needs: its settings and what its secret is called.</summary>
public sealed record ConnectionDescriptor(string Kind, string Name, string Description, IReadOnlyList<SourceSetting> Settings, string SecretLabel, string SecretHint);

/// <summary>A kind of sign-in kept under Connections: what it needs, and a check that signs in and says what it found.</summary>
public interface IConnectionKind
{
    ConnectionDescriptor Descriptor { get; }

    /// <summary>Signs in and returns one sentence about what was found; a failure is a <see cref="ConnectorException"/>.</summary>
    Task<string> TestAsync(ConnectionOptions connection);
}

public sealed class DocuWareConnectionKind : IConnectionKind
{
    public ConnectionDescriptor Descriptor { get; } = new(
        ConnectionKinds.DocuWare, "DocuWare", "Store documents in DocuWare file cabinets, or import from them.",
        new[]
        {
            new SourceSetting("Url", "Address of your DocuWare", SourceSteps.Which, Hint: "Copy it from the browser when DocuWare is open, e.g. https://yourcompany.docuware.cloud."),
            new SourceSetting("UserName", "User name", SourceSteps.Which, Hint: "A user allowed to sign in with a DocuWare password."),
        },
        "Password", "Stored encrypted on this machine.");

    public async Task<string> TestAsync(ConnectionOptions connection)
    {
        var client = new DocuWareClient(connection.Setting("Url"), connection.Setting("UserName"), SecretProtector.Unprotect(connection.Secret));
        var cabinets = await client.CabinetsAsync();
        return cabinets.Count == 0
            ? "Signed in, but this user sees no file cabinet. Ask the DocuWare administrator for access."
            : $"Signed in. {cabinets.Count} file cabinet(s): {string.Join(", ", cabinets.Take(5).Select(c => c.Name))}{(cabinets.Count > 5 ? ", …" : "")}.";
    }
}

public sealed class AzureStorageConnectionKind : IConnectionKind
{
    public ConnectionDescriptor Descriptor { get; } = new(
        ConnectionKinds.AzureStorage, "Azure Storage", "Documents from and to Azure Blob containers, for example of Dynamics 365 Finance and Operations.",
        new[]
        {
            new SourceSetting("AccountUrl", "Account address (only for a managed identity in Azure)", SourceSteps.Which, Required: false,
                Hint: "https://<account>.blob.core.windows.net. Leave it empty when you give the connection string."),
        },
        "Connection string", "From the storage account, Access keys. Stored encrypted on this machine.");

    public async Task<string> TestAsync(ConnectionOptions connection)
    {
        try
        {
            string secret = SecretProtector.Unprotect(connection.Secret);
            var service = secret.Length > 0
                ? new BlobServiceClient(secret)
                : new BlobServiceClient(new Uri(connection.Setting("AccountUrl")), new DefaultAzureCredential());
            var names = new List<string>();
            await foreach (var page in service.GetBlobContainersAsync().AsPages(pageSizeHint: 20))
            {
                names.AddRange(page.Values.Select(c => c.Name));
                break;
            }
            return names.Count == 0 ? "Signed in. The account has no container yet." : $"Signed in. Containers: {string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", …" : "")}.";
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or FormatException or ArgumentException)
        { throw new ConnectorException("Azure Storage refused the sign-in: " + ex.Message.Split('\n')[0], retryLater: false); }
    }
}

public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>What the dashboard asks about connections: the kinds, and a check with the values on the form.</summary>
public static class ConnectionCatalog
{
    public static IReadOnlyList<ConnectionDescriptor> Describe(ConnectorRegistry registry)
        => registry.ConnectionKindNames.Select(k => registry.ConnectionKind(k)?.Descriptor).OfType<ConnectionDescriptor>().ToList();

    /// <summary>The connection as the form shows it; a blank secret means the stored one of the same id.</summary>
    public static ConnectionOptions Draft(BDCOptions stored, string id, string kind, Dictionary<string, string> settings, string secret)
    {
        var draft = new ConnectionOptions
        {
            Id = string.IsNullOrWhiteSpace(id) ? kind : id.Trim(),
            Kind = kind,
            Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase),
            Secret = secret,
        };
        draft.Normalize();
        if (draft.Secret.Length == 0 && stored.Connection(draft.Id) is { } old && old.Kind == draft.Kind) draft.Secret = old.Secret;
        return draft;
    }

    public static async Task<ConnectionTestResult> TestAsync(ConnectorRegistry registry, ConnectionOptions draft)
    {
        var kind = registry.ConnectionKind(draft.Kind);
        if (kind is null) return new ConnectionTestResult(false, $"The connection kind '{draft.Kind}' is not known to this version of Hive BDC.");
        var problems = draft.Problems();
        if (problems.Count > 0) return new ConnectionTestResult(false, string.Join(" ", problems));
        try { return new ConnectionTestResult(true, await kind.TestAsync(draft)); }
        catch (ConnectorException ex) { return new ConnectionTestResult(false, ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        { return new ConnectionTestResult(false, $"{kind.Descriptor.Name}: {ex.Message}"); }
    }
}