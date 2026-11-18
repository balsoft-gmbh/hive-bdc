namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// DocuWare as the source of an inbound flow: documents of a file cabinet whose index field has the ready value; once
/// handed over, that field gets the done value. Settings FileCabinet, ReadyField, ReadyValue and DoneValue.
/// </summary>
public sealed class DocuWareRecordSource(SourceRuntime runtime, Func<ConnectionOptions, DocuWareClient> clients) : IRecordSource
{
    public SourceDescriptor Descriptor { get; } = new(
        ConnectionKinds.DocuWare, "DocuWare",
        "Documents in a file cabinet, picked up when an index field has a value, then marked as imported.",
        ConnectionKinds.DocuWare, LicenseFeatures.SharePoint, "document", "DW", "{DWDOCID}.csv",
        new[]
        {
            new SourceSetting("FileCabinet", "File cabinet", SourceSteps.Which, Lookup: "cabinets"),
            new SourceSetting("ReadyField", "Ready when the field", SourceSteps.Which, Lookup: "readyfields", DependsOn: "FileCabinet"),
            new SourceSetting("ReadyValue", "has the value", SourceSteps.Which,
                Hint: "For example Ready for ERP. A person or a DocuWare workflow sets it."),
            new SourceSetting("DoneValue", "Afterwards set the field to", SourceSteps.After,
                Hint: "For example Imported, so the document is not picked up again."),
        });

    public bool IsConnected => Connection(new SourceOptions()) is { } c && c.Problems().Count == 0;

    private ConnectionOptions? Connection(SourceOptions source)
        => runtime.Connection(string.IsNullOrWhiteSpace(source.Connection) ? ConnectionKinds.DocuWare : source.Connection);

    private DocuWareClient Client(SourceOptions source)
    {
        var connection = Connection(source);
        if (connection is null || connection.Problems().Count > 0)
            throw new ConnectorException($"Connect DocuWare under {SettingsPaths.Pick("Settings > Connections", "Connections")} first.", retryLater: false);
        return clients(connection);
    }

    public IReadOnlyList<string> Problems(SourceOptions source)
    {
        var connection = Connection(source);
        var problems = connection?.Problems().ToList()
                       ?? new List<string> { $"The connection '{source.Connection}' is not set up ({SettingsPaths.Pick("Settings > Connections", "Connections")})." };
        if (Cabinet(source).Length == 0 || source.Setting("ReadyField").Length == 0 || source.Setting("ReadyValue").Length == 0
            || source.Setting("DoneValue").Length == 0)
            problems.Add("Set the file cabinet, the field and value that mark a document ready, and the value written once it is imported.");
        return problems;
    }

    private static string Cabinet(SourceOptions source) => source.Setting("FileCabinet");

    public Task<IReadOnlyList<string>> ReadyAsync(SourceOptions source, int max)
        => Client(source).QueryIdsAsync(Cabinet(source), source.Setting("ReadyField"), source.Setting("ReadyValue"), max);

    public Task<SourceRecord> ReadAsync(SourceOptions source, string id) => Client(source).RecordAsync(Cabinet(source), id);

    public async Task<DocumentFile?> FileAsync(SourceOptions source, string id)
    {
        var (content, name, _) = await Client(source).DownloadAsync(Cabinet(source), id);
        return DocumentFile.FromBytes(name.Length > 0 ? name : id + ".pdf", content);
    }

    public async Task<string> MarkDoneAsync(SourceOptions source, string id)
    {
        string field = source.Setting("ReadyField"), done = source.Setting("DoneValue");
        await Client(source).SetFieldAsync(Cabinet(source), id, field, done);
        return $"Marked {field} = {done} in DocuWare, so it is not imported again.";
    }

    public async Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, SourceOptions source)
    {
        var client = Client(source);
        switch (lookup)
        {
            case "cabinets":
                return (await client.CabinetsAsync()).Select(c => new SourceChoice(c.Id, c.Name)).ToList();
            case "readyfields":
                if (Cabinet(source).Length == 0) return Array.Empty<SourceChoice>();
                return (await client.CabinetAsync(Cabinet(source))).Fields
                    .Where(f => f.Columns.Count == 0 && f.Type is "Text" or "Keyword")
                    .Select(f => new SourceChoice(f.Name, f.DisplayName, f.Type)).ToList();
            case "fields":
                if (Cabinet(source).Length == 0) return Array.Empty<SourceChoice>();
                var list = new List<SourceChoice>();
                var cabinetFields = (await client.CabinetAsync(Cabinet(source))).Fields;
                if (cabinetFields.All(f => !f.Name.Equals("DWDOCID", StringComparison.OrdinalIgnoreCase)))
                    list.Add(new SourceChoice("DWDOCID", "Document id", "number"));
                foreach (var f in cabinetFields)
                {
                    list.Add(new SourceChoice(f.Name, f.DisplayName, f.Columns.Count > 0 ? "table" : f.Type));
                    list.AddRange(f.Columns.Select(c => new SourceChoice($"{f.Name}.{c.Name}", $"{f.DisplayName}: {c.DisplayName}", "column of " + f.Name)));
                }
                return list;
            default:
                return Array.Empty<SourceChoice>();
        }
    }

    public string RecordKey(SourceOptions source, string id) => $"{Cabinet(source)}|{id}";

    public string Scope(SourceOptions source)
        => string.Join("|", source.Connection, Cabinet(source), source.Setting("ReadyField"), source.Setting("ReadyValue")).ToLowerInvariant();
}