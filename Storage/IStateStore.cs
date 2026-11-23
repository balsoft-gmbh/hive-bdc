using System.Text.RegularExpressions;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>
/// Everything the service remembers between documents: the export ledger, what each target received, and the history
/// of every document. Local installs keep it in SQLite (<see cref="ExportLedger"/>); other hosts bring their own store.
/// Every member is best-effort: a store failure is logged and never stops a document.
/// </summary>
public interface IStateStore
{
    void Record(string documentId, string documentType, string format, string outputFile, string? targetSite,
        long sizeBytes, string? dataAreaId = null, string source = "json");

    void RecordDelivery(string documentKey, string targetKey, string kind, string? location, string? remoteId);

    string? DeliveryRemoteId(string documentKey, string targetKey);

    void RecordEvent(DocumentEvent entry);

    /// <summary>
    /// Every step of the documents whose number, id or input file name contains <paramref name="search"/> (any case,
    /// oldest step first, at most 20 documents), or the newest events of all documents when it is blank.
    /// </summary>
    IReadOnlyList<DocumentEvent> Events(string? search, int limit);

    /// <summary>The last line of every document whose last step failed or is waiting, newest first.</summary>
    IReadOnlyList<DocumentEvent> Attention(int limit);

    ExportLedger.MetricsSnapshot Metrics(int recent);

    int Prune(int retentionDays = ExportLedger.DefaultRetentionDays);

    void PruneIfDue(int retentionDays = ExportLedger.DefaultRetentionDays);
}

/// <summary>The steps of a document's history.</summary>
public static class EventSteps
{
    public const string Received = "received";
    public const string Read = "read";
    public const string Produced = "produced";
    public const string Compressed = "compressed";
    public const string Delivered = "delivered";
    public const string Skipped = "skipped";
    public const string Waiting = "waiting";
    public const string Failed = "failed";
    public const string Done = "done";
}

public static class EventStates
{
    public const string Ok = "ok";
    public const string Waiting = "waiting";
    public const string Failed = "failed";
}

/// <summary>
/// One line of a document's history. <see cref="Input"/> is the file or item the document came in as, so the steps of
/// one arrival stay together; <see cref="Target"/> names the target kind on delivery lines.
/// </summary>
public sealed record DocumentEvent(
    string DocumentId,
    string Input,
    string Step,
    string State,
    string Message,
    string? DocumentNumber = null,
    string? DocumentType = null,
    string? DataAreaId = null,
    string? Target = null,
    long Id = 0,
    string? At = null);

/// <summary>Event text as it is stored and shown: mail addresses masked, line breaks flattened, length capped.</summary>
public static partial class EventText
{
    public const int MaxLength = 2000;

    [GeneratedRegex(@"(?<first>[A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]*@(?<domain>[A-Za-z0-9.\-]+\.[A-Za-z]{2,})")]
    private static partial Regex MailAddress();

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string s = MailAddress().Replace(text, m => m.Groups["first"].Value + "***@" + m.Groups["domain"].Value);
        s = LineBreaks().Replace(s, " ").Trim();
        return s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
    }
}