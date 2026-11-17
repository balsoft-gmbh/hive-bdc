namespace Balsoft.Hive.BDC.Configuration;

/// <summary>
/// An inbound flow ("HiveBDC:Flows[i]"): one company of one document type, read from a document system (the source),
/// made into what the ERP reads (a CSV) and put where the ERP reads it. Outbound flows stay under DocumentTypes.
/// </summary>
public sealed class FlowOptions
{
    public string Id { get; set; } = "";

    /// <summary>Shown in the dashboard and the history, e.g. "Purchase requisitions from DocuWare".</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>The document type this flow brings in, e.g. PurchaseOrder. Empty in files written before 1.5.0.</summary>
    public string DocumentType { get; set; } = "";

    /// <summary>The ERP company the records are for; also {DataAreaId} in the CSV. Empty in files written before 1.5.0.</summary>
    public string DataAreaId { get; set; } = "";

    public SourceOptions Source { get; set; } = new();

    public ProduceOptions Produce { get; set; } = new();

    public List<TargetOptions> Targets { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

    /// <summary>The document type the dashboard groups this flow under; an older entry without one stands under its own name.</summary>
    public string TypeLabel => DocumentType.Length > 0 ? DocumentType : DisplayName;

    public void Normalize()
    {
        Id = Id?.Trim() ?? "";
        Name = Name?.Trim() ?? "";
        DocumentType = DocumentType?.Trim() ?? "";
        DataAreaId = DataAreaId?.Trim() ?? "";
        Source ??= new();
        Source.Normalize();
        Produce ??= new();
        Produce.Normalize();
        Targets ??= new();
        Targets.RemoveAll(t => t is null);
        foreach (var t in Targets) t.Normalize();
    }
}

/// <summary>
/// Where records come from: a source kind registered in the ConnectorRegistry and its settings, e.g. DocuWare (FileCabinet,
/// ReadyField, ReadyValue, DoneValue) or SharePoint (SiteUrl, Library, ReadyColumn, ReadyValue, DoneValue).
/// </summary>
public sealed class SourceOptions
{
    public string Kind { get; set; } = "";
    public string Connection { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Setting(string name) => Settings.TryGetValue(name, out var v) ? v?.Trim() ?? "" : "";

    public void Normalize()
    {
        Kind = TargetKinds.Clean(Kind);
        Connection = Connection?.Trim() ?? "";
        Settings = new Dictionary<string, string>(Settings ?? new(), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// What is made from a record. CSV: Settings Delimiter (default ;), Culture for numbers and dates (default de-DE),
/// RowsFrom (a table field: one row per table row, the other columns repeated), FileName (a template, default
/// {DWDOCID}.csv); Columns in their order, each a name and a template.
/// </summary>
public sealed class ProduceOptions
{
    public string Kind { get; set; } = "csv";
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ColumnOptions> Columns { get; set; } = new();

    public string Setting(string name, string fallback = "")
        => Settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public void Normalize()
    {
        Kind = string.IsNullOrWhiteSpace(Kind) ? "csv" : TargetKinds.Clean(Kind);
        Settings = new Dictionary<string, string>(Settings ?? new(), StringComparer.OrdinalIgnoreCase);
        Columns ??= new();
        Columns.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Name));
        foreach (var c in Columns) { c.Name = c.Name.Trim(); c.Value ??= ""; }
    }
}

public sealed class ColumnOptions
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}