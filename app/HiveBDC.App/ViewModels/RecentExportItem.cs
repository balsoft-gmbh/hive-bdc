namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>One row in the recent-exports list — metadata only, never document content.</summary>
public sealed class RecentExportItem
{
    public required string DocumentId { get; init; }
    public string? DataAreaId { get; init; }
    /// <summary>Configured document type id, e.g. SalesInvoice, PurchaseOrder.</summary>
    public required string DocumentType { get; init; }
    /// <summary>What was produced: xrechnung / zugferd / pdf.</summary>
    public required string Format { get; init; }
    public required string ExportedAt { get; init; }
    public required string Size { get; init; }
    public string? TargetSite { get; init; }
    /// <summary>Local path of the produced output (.pdf / .xml) — opened on double-click.</summary>
    public string? OutputFile { get; init; }

    public static RecentExportItem From(Services.RecentExportDto e) => new()
    {
        DocumentId = e.DocumentId ?? "—",
        DataAreaId = e.DataAreaId,
        DocumentType = e.DocumentType ?? "—",
        Format = e.Format ?? "—",
        ExportedAt = e.ExportedAt ?? "—",
        Size = MainViewModel.HumanSize(e.SizeBytes),
        TargetSite = e.TargetSite,
        OutputFile = e.OutputFile,
    };
}
