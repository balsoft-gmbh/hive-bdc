namespace Balsoft.Hive.BDC.Documents;

/// <summary>What one ready record would become: the CSV lines and its file name, built by the same code as a run.</summary>
public sealed record InboundPreview(IReadOnlyList<string> Lines, string FileName, int Columns, int Rows, IReadOnlyList<string> Problems)
{
    public static InboundPreview Build(SourceDescriptor d, SourceRecord record, ProduceOptions produce, string dataAreaId,
        TargetOptions? target, BDCOptions options)
    {
        string filePath = "";
        // Only a destination that could deliver names the file's address; an incomplete one leaves {FilePath} empty.
        if (target is not null && InboundSettings.SavesFile(target)
            && ConnectorRegistry.BuiltIn().Inbound(target.Kind, options) is { } destination && destination.Problems(target).Count == 0)
        {
            try { filePath = destination.FileLocation(target, ImportProcessor.FileNameFor(d, record.Id, "")); }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or FormatException) { }
        }
        var csv = ImportProcessor.MakeCsv(d, record, produce, dataAreaId, filePath);
        var lines = csv.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        return new InboundPreview(lines, csv.FileName, produce.Columns.Count, Math.Max(0, lines.Length - 1), csv.Problems);
    }
}