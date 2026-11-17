using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Company entries whose documents arrive on Azure Service Bus (the route's Source, Kind "servicebus": Settings Queue,
/// or Topic and Subscription; PdfConnection and PdfContainer for the PDF the JSON names in pdfLink). The message body is
/// the document JSON as an F&amp;O send class writes it (MessageId = its outbound id).
/// Before each scan, new messages are received with peek-lock and staged into the input folder as {id}_sb.json (and
/// {id}_sb.pdf), a note records the message's sequence number, and the message is deferred: a deferred message keeps no
/// lock and no delivery count, so a document that waits for hours is not dead-lettered by the broker. Once the pipeline
/// has moved the pair to processed or error, the deferred message is fetched by its sequence number and completed, or
/// dead-lettered with the reason. A message whose MessageId was staged before (sent twice, or delivered again after a
/// crash before deferral) is completed without a second copy (failure case 13).
/// </summary>
public sealed partial class ServiceBusStager
{
    private const string NotesFolder = ".hive-sources";
    private const string StagedSuffix = "_sb";
    private const int ReceiveAtMost = 20;

    private readonly ILogger _log;
    private readonly IStateStore? _state;
    private readonly Func<ConnectionOptions, string, string, string, IBusInbox> _inboxes;
    private readonly Func<ConnectionOptions, string, IBlobStore> _stores;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public ServiceBusStager(ILogger log, IStateStore? state,
        Func<ConnectionOptions, string, string, string, IBusInbox>? inboxes = null, Func<ConnectionOptions, string, IBlobStore>? stores = null)
    {
        _log = log;
        _state = state;
        _inboxes = inboxes ?? AzureServiceBus.Inbox;
        _stores = stores ?? ((c, container) => AzureBlobStore.For(c, container));
    }

    private sealed record Note(string Connection, string Queue, string Topic, string Subscription, long SequenceNumber, string MessageId);

    [GeneratedRegex(@"[^A-Za-z0-9\-]")]
    private static partial Regex UnsafeIdChars();

    public void Run(BDCOptions opts)
    {
        foreach (var t in opts.DocumentTypes.Where(t => t.Enabled))
            foreach (var route in t.Companies.Where(r => r.IsOn && r.Source is { Kind: ConnectionKinds.ServiceBus }))
            {
                try { Settle(opts, route); Stage(opts, route); }
                catch (Exception ex) { WarnOnce(route.Input, $"Service Bus source for {route.Input}: {ex.Message.Split('\n')[0]}"); }
            }
    }

    private void Stage(BDCOptions opts, DocumentRoute route)
    {
        var source = route.Source!;
        var connection = opts.Connection(source.Connection);
        string queue = source.Setting("Queue"), topic = source.Setting("Topic"), subscription = source.Setting("Subscription");
        if (connection is null || (queue.Length == 0 && (topic.Length == 0 || subscription.Length == 0)))
        { WarnOnce(route.Input, $"Service Bus source for {route.Input}: set the connection and a queue, or a topic and its subscription."); return; }
        var inbox = _inboxes(connection, queue, topic, subscription);
        string notes = Path.Combine(route.Input, NotesFolder);

        foreach (var message in inbox.Receive(ReceiveAtMost, TimeSpan.FromSeconds(1)))
        {
            string id = IdFor(message);
            string staged = id + StagedSuffix + ".json";
            if (File.Exists(Path.Combine(notes, id + StagedSuffix + ".json"))
                || new[] { route.Input, Path.Combine(route.Input, "processed"), Path.Combine(route.Input, "error") }.Any(d => File.Exists(Path.Combine(d, staged))))
            {
                inbox.Complete(message);
                _log.LogInformation("[servicebus] {Entity}: message {MessageId} was staged before; this copy is completed without a second document.",
                    inbox.Where, message.MessageId);
                continue;
            }

            byte[]? pdf = null;
            if (PdfLink.Present(message.Body))
            {
                var pdfConnection = opts.Connection(source.Setting("PdfConnection"));
                string container = source.Setting("PdfContainer");
                if (pdfConnection is null || container.Length == 0)
                {
                    Refuse(inbox, message, id, "The document names a pdfLink, but the Service Bus source has no PdfConnection and PdfContainer to fetch it from.");
                    continue;
                }
                var store = _stores(pdfConnection, container);
                string? name = PdfLink.BlobName(store, message.Body, out var refused);
                if (refused is not null) { Refuse(inbox, message, id, refused); continue; }
                if (!store.Exists(name!))
                {
                    // Case 36: the PDF is not there yet. The message comes back after its lock; the broker counts it.
                    inbox.Abandon(message);
                    WarnOnce(route.Input + id, $"message {message.MessageId}: the PDF {name} is not in {container} yet; it is asked again later.");
                    continue;
                }
                pdf = store.Download(name!);
            }

            Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, id + StagedSuffix + ".json"),
                JsonSerializer.Serialize(new Note(source.Connection, queue, topic, subscription, message.SequenceNumber, message.MessageId)));
            if (pdf is not null) CsvFile.WriteAtomic(Path.Combine(route.Input, id + StagedSuffix + ".pdf"), pdf);
            CsvFile.WriteAtomic(Path.Combine(route.Input, staged), message.Body);
            inbox.Defer(message);
            _log.LogInformation("[servicebus] {Entity}: message {MessageId} staged as {Staged}{Pdf} and deferred (sequence {Sequence}).",
                inbox.Where, message.MessageId, staged, pdf is null ? "" : " with its PDF", message.SequenceNumber);
        }
    }

    /// <summary>Completes or dead-letters the deferred message of every pair the pipeline has finished with.</summary>
    private void Settle(BDCOptions opts, DocumentRoute route)
    {
        string notes = Path.Combine(route.Input, NotesFolder);
        if (!Directory.Exists(notes)) return;
        foreach (var file in Directory.EnumerateFiles(notes, "*" + StagedSuffix + ".json"))
        {
            string staged = Path.GetFileName(file);
            bool done = File.Exists(Path.Combine(route.Input, "processed", staged));
            bool failed = File.Exists(Path.Combine(route.Input, "error", staged));
            if (!done && !failed) continue;

            var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(file))!;
            var connection = opts.Connection(note.Connection);
            if (connection is null) { WarnOnce(file, $"message {note.MessageId}: connection '{note.Connection}' is gone; the message stays deferred."); continue; }
            var inbox = _inboxes(connection, note.Queue, note.Topic, note.Subscription);
            var message = inbox.ReceiveDeferred(note.SequenceNumber);
            if (message is null)
                _log.LogWarning("[servicebus] {Entity}: message {MessageId} (sequence {Sequence}) is no longer there (completed or expired); nothing to settle.",
                    inbox.Where, note.MessageId, note.SequenceNumber);
            else if (done)
                inbox.Complete(message);
            else
                inbox.DeadLetter(message, "HiveBDC.DocumentError", Reason(staged));
            File.Delete(file);
            if (message is not null)
                _log.LogInformation("[servicebus] {Entity}: message {MessageId} {Outcome}.", inbox.Where, note.MessageId, done ? "completed" : "dead-lettered");
        }
    }

    private void Refuse(IBusInbox inbox, BusMessage message, string id, string reason)
    {
        inbox.DeadLetter(message, "HiveBDC.Refused", reason);
        _state?.RecordEvent(new DocumentEvent(id, message.MessageId, EventSteps.Failed, EventStates.Failed, reason));
        _log.LogWarning("[servicebus] {Entity}: message {MessageId} dead-lettered: {Reason}", inbox.Where, message.MessageId, reason);
    }

    /// <summary>The last failed step of the staged file in the document history, or a pointer to the history.</summary>
    private string Reason(string staged)
        => _state?.Events(staged, 200).LastOrDefault(e => e.State == EventStates.Failed)?.Message is { Length: > 0 } message
            ? message
            : "See the document history in Hive BDC.";

    private static string IdFor(BusMessage message)
    {
        string id = UnsafeIdChars().Replace(message.MessageId, "-").Trim('-');
        return id.Length == 0 ? "seq-" + message.SequenceNumber : id.Length > 100 ? id[..100] : id;
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key + "|" + message)) _log.LogWarning("[servicebus] {Message}", message);
    }
}