namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// The connector kinds this version knows, by name: targets an outbound flow delivers to and sources an inbound flow
/// reads from. A new DMS is one class and one line here; the processors, the settings and the ledger do not change.
/// </summary>
public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, Func<TargetRuntime, IDocumentTarget>> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<SourceRuntime, IRecordSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sourceOrder = new();

    public static ConnectorRegistry BuiltIn()
    {
        var registry = new ConnectorRegistry();
        registry.RegisterTarget(TargetKinds.SharePoint, runtime => new SharePointDocumentTarget(runtime.Uploader));
        registry.RegisterTarget(TargetKinds.Mail, runtime => new MailDocumentTarget(runtime.Options.Mail, runtime.Mailer));
        registry.RegisterTarget(ConnectionKinds.DocuWare, runtime => new DocuWareDocumentTarget(runtime.Options, DocuWareClients.For));
        registry.RegisterTarget("blob", runtime => new BlobDocumentTarget(runtime.Options, (c, container) => AzureBlobStore.For(c, container)));
        registry.RegisterSource(ConnectionKinds.DocuWare, runtime => new DocuWareRecordSource(runtime, DocuWareClients.For));
        registry.RegisterSource(TargetKinds.SharePoint, runtime => new SharePointRecordSource(runtime.SharePointAuth));
        return registry;
    }

    public void RegisterSource(string kind, Func<SourceRuntime, IRecordSource> create)
    {
        string key = TargetKinds.Clean(kind);
        if (!_sources.ContainsKey(key)) _sourceOrder.Add(key);
        _sources[key] = create;
    }

    /// <summary>The source kinds in the order they were registered (the order of the tiles in the dashboard).</summary>
    public IReadOnlyList<string> SourceKindNames => _sourceOrder;

    /// <summary>The source for <paramref name="kind"/>, or null when this version does not know the kind.</summary>
    public IRecordSource? Source(string kind, SourceRuntime runtime)
        => _sources.TryGetValue(TargetKinds.Clean(kind), out var create) ? create(runtime) : null;

    public void RegisterTarget(string kind, Func<TargetRuntime, IDocumentTarget> create)
        => _targets[TargetKinds.Clean(kind)] = create;

    public IReadOnlyCollection<string> TargetKindNames => _targets.Keys;

    /// <summary>The target for <paramref name="kind"/>, or null when this version does not know the kind.</summary>
    public IDocumentTarget? Target(string kind, TargetRuntime runtime)
        => _targets.TryGetValue(TargetKinds.Clean(kind), out var create) ? create(runtime) : null;
}

/// <summary>One DocuWare client per connection and its sign-in data, so a token and the cabinet fields are reused across documents.</summary>
public static class DocuWareClients
{
    private static readonly Dictionary<string, DocuWareClient> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static DocuWareClient For(ConnectionOptions connection)
    {
        string password = SecretProtector.Unprotect(connection.Secret);
        string key = string.Join("|", connection.Id, connection.Setting("Url"), connection.Setting("UserName"),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password))));
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out var client))
                Cache[key] = client = new DocuWareClient(connection.Setting("Url"), connection.Setting("UserName"), password);
            return client;
        }
    }
}