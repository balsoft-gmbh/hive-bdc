using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>What the service holds while it scans: the settings and the shared clients a target may need.</summary>
public sealed record TargetRuntime(BDCOptions Options, SharePointUploader? Uploader, MailSender? Mailer, ILogger Log);

/// <summary>One produced document on its way to one target.</summary>
public sealed record DeliveryContext(
    DocumentRoute Route,
    DocumentTypeOptions Type,
    TargetOptions Target,
    AxDocument Data,
    JsonElement Document,
    string DocumentId,
    string DocumentNumber,
    DocumentFile Output,
    string? PreviousRemoteId,
    string Tag,
    Action<string> Warn,
    ILogger Log);

/// <summary>
/// Where the document went: a location for the ledger, the id the target gave it if it gives one, and one sentence for
/// the document's history in words a user understands.
/// </summary>
public sealed record DeliveryResult(string? Location = null, string? RemoteId = null, string? Summary = null);

/// <summary>
/// A place a produced document is delivered to. One class per kind, found through
/// <see cref="ConnectorRegistry"/>; the processor treats every kind the same.
/// </summary>
public interface IDocumentTarget
{
    string Kind { get; }

    /// <summary>
    /// True when delivering the same document again replaces the earlier delivery (a file
    /// overwritten by name). False when a second delivery would create a second document: such a
    /// target gets the id of its earlier delivery in <see cref="DeliveryContext.PreviousRemoteId"/>
    /// and updates that one.
    /// </summary>
    bool ReplacesOnRepeat { get; }

    /// <summary>The reason this target cannot deliver right now (no connection), or null. Checked before anything is produced.</summary>
    string? Unavailable(TargetOptions target);

    DeliveryResult Deliver(DeliveryContext ctx);
}