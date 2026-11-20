using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>A received message: its sequence number (to fetch it again once deferred), its id, its subject and its body.</summary>
public sealed class BusMessage(long sequenceNumber, string messageId, string? subject, byte[] body, object? native = null)
{
    public long SequenceNumber { get; } = sequenceNumber;

    public string MessageId { get; } = messageId;

    public string? Subject { get; } = subject;

    public byte[] Body { get; } = body;

    /// <summary>The SDK's own message, which every settlement needs.</summary>
    public object? Native { get; } = native;
}

/// <summary>A queue, or a topic subscription, read with peek-lock. A message is deferred, abandoned, completed or dead-lettered.</summary>
public interface IBusInbox
{
    string Where { get; }

    IReadOnlyList<BusMessage> Receive(int max, TimeSpan wait);

    void Defer(BusMessage message);

    void Abandon(BusMessage message);

    void Complete(BusMessage message);

    void DeadLetter(BusMessage message, string reason, string description);

    /// <summary>A message deferred earlier, or null when it is no longer there (completed, expired).</summary>
    BusMessage? ReceiveDeferred(long sequenceNumber);
}

/// <summary>A queue or topic a message is sent to.</summary>
public interface IBusOutbox
{
    string Where { get; }

    void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties);

    /// <summary>The same with the body's content type; an outbox that only knows JSON sends it as JSON.</summary>
    void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties, string contentType)
        => Send(body, messageId, subject, properties);
}

/// <summary>
/// Azure Service Bus with one client per connection and one receiver or sender per entity, kept for the life of the
/// process (an AMQP connection is expensive to open). A changed connection gets new clients.
/// </summary>
public static class AzureServiceBus
{
    private static readonly ConcurrentDictionary<string, ServiceBusClient> Clients = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ServiceBusReceiver> Receivers = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ServiceBusSender> Senders = new(StringComparer.Ordinal);

    public static IBusInbox Inbox(ConnectionOptions connection, string queue, string topic, string subscription)
    {
        var (client, key) = Client(connection);
        string entity = queue.Length > 0 ? queue : $"{topic}/subscriptions/{subscription}";
        var receiver = Receivers.GetOrAdd(key + "|" + entity, _ => queue.Length > 0
            ? client.CreateReceiver(queue)
            : client.CreateReceiver(topic, subscription));
        return new AzureBusInbox(receiver, $"{client.FullyQualifiedNamespace}/{entity}");
    }

    /// <summary>How many messages wait (up to <paramref name="max"/>), read by peeking: nothing is locked, received or completed.</summary>
    public static async Task<int> PeekCount(ConnectionOptions connection, string queue, string topic, string subscription, int max)
    {
        var (client, _) = Client(connection);
        await using var receiver = queue.Length > 0 ? client.CreateReceiver(queue) : client.CreateReceiver(topic, subscription);
        return (await receiver.PeekMessagesAsync(max)).Count;
    }

    public static IBusOutbox Outbox(ConnectionOptions connection, string queueOrTopic)
    {
        var (client, key) = Client(connection);
        var sender = Senders.GetOrAdd(key + "|" + queueOrTopic, _ => client.CreateSender(queueOrTopic));
        return new AzureBusOutbox(sender, $"{client.FullyQualifiedNamespace}/{queueOrTopic}");
    }

    public static ServiceBusAdministrationClient Administration(ConnectionOptions connection)
    {
        string secret = SecretProtector.Unprotect(connection.Secret);
        return secret.Length > 0
            ? new ServiceBusAdministrationClient(secret)
            : new ServiceBusAdministrationClient(connection.Setting("Namespace"), new DefaultAzureCredential());
    }

    private static (ServiceBusClient Client, string Key) Client(ConnectionOptions connection)
    {
        string secret = SecretProtector.Unprotect(connection.Secret);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.Id + "|" + connection.Setting("Namespace") + "|" + secret)));
        var client = Clients.GetOrAdd(key, _ => secret.Length > 0
            ? new ServiceBusClient(secret)
            : new ServiceBusClient(connection.Setting("Namespace"), new DefaultAzureCredential()));
        return (client, key);
    }

    private sealed class AzureBusInbox(ServiceBusReceiver receiver, string where) : IBusInbox
    {
        public string Where => where;

        public IReadOnlyList<BusMessage> Receive(int max, TimeSpan wait)
            => receiver.ReceiveMessagesAsync(max, wait).GetAwaiter().GetResult().Select(ToMessage).ToList();

        public void Defer(BusMessage message) => receiver.DeferMessageAsync(Native(message)).GetAwaiter().GetResult();

        public void Abandon(BusMessage message) => receiver.AbandonMessageAsync(Native(message)).GetAwaiter().GetResult();

        public void Complete(BusMessage message) => receiver.CompleteMessageAsync(Native(message)).GetAwaiter().GetResult();

        public void DeadLetter(BusMessage message, string reason, string description)
            => receiver.DeadLetterMessageAsync(Native(message), reason, description.Length > 1024 ? description[..1024] : description).GetAwaiter().GetResult();

        public BusMessage? ReceiveDeferred(long sequenceNumber)
        {
            try
            {
                return ToMessage(receiver.ReceiveDeferredMessageAsync(sequenceNumber).GetAwaiter().GetResult());
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageNotFound)
            {
                return null;
            }
        }

        private static BusMessage ToMessage(ServiceBusReceivedMessage m)
            => new(m.SequenceNumber, m.MessageId ?? "", m.Subject, m.Body.ToArray(), m);

        private static ServiceBusReceivedMessage Native(BusMessage message)
            => message.Native as ServiceBusReceivedMessage ?? throw new InvalidOperationException("The message was not received from Service Bus.");
    }

    private sealed class AzureBusOutbox(ServiceBusSender sender, string where) : IBusOutbox
    {
        public string Where => where;

        public void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties)
            => Send(body, messageId, subject, properties, "application/json");

        public void Send(byte[] body, string messageId, string subject, IReadOnlyDictionary<string, object> properties, string contentType)
        {
            var message = new ServiceBusMessage(body) { MessageId = messageId, Subject = subject, ContentType = contentType };
            foreach (var (name, value) in properties) message.ApplicationProperties[name] = value;
            sender.SendMessageAsync(message).GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Sends the produced document as JSON (<see cref="DocumentEnvelope"/>) to a Service Bus queue or topic, for example the
/// one an F&amp;O receive flow (BDCDataReceive_ServiceBusQueue or _Topic) reads: the message body is the UTF-8 JSON,
/// MessageId is "{type}_{company}_{document id}" (the receive flow's external id, the same for a repeat, so the queue's
/// duplicate detection can drop it), Subject the document type, and documentNumber, documentType, dataAreaId and
/// fileName are message properties. A transient Service Bus failure leaves the document waiting.
/// </summary>
public sealed class ServiceBusDocumentTarget(BDCOptions options, Func<ConnectionOptions, string, IBusOutbox>? outboxes = null) : IDocumentTarget
{
    private readonly Func<ConnectionOptions, string, IBusOutbox> _outboxes = outboxes ?? AzureServiceBus.Outbox;

    public string Kind => ConnectionKinds.ServiceBus;

    public bool ReplacesOnRepeat => true;

    public string? Unavailable(TargetOptions target)
    {
        var connection = options.Connection(target.Connection);
        if (connection is null)
            return $"The Service Bus target uses the connection '{target.Connection}', which is not set up. Add it in {SettingsPaths.Pick("Settings > Connections", "Connections")}.";
        var problems = connection.Problems();
        if (problems.Count > 0) return string.Join(" ", problems);
        return Entity(target).Length == 0 ? "The Service Bus target has no queue or topic." : null;
    }

    public DeliveryResult Deliver(DeliveryContext ctx)
    {
        var outbox = _outboxes(options.Connection(ctx.Target.Connection)!, Entity(ctx.Target));
        string messageId = MessageIdFor(ctx.Type.Id, ctx.Route.DataAreaId, ctx.DocumentId);
        byte[] body = Encoding.UTF8.GetBytes(DocumentEnvelope.Build(ctx).ToJsonString());
        var properties = new Dictionary<string, object>
        {
            ["documentNumber"] = ctx.DocumentNumber,
            ["documentType"] = ctx.Type.Id,
            ["dataAreaId"] = ctx.Route.DataAreaId,
            ["fileName"] = ctx.Output.Name,
        };
        try
        {
            outbox.Send(body, messageId, ctx.Type.Id, properties);
        }
        catch (ServiceBusException ex)
        {
            throw new ConnectorException($"Service Bus {outbox.Where}: {ex.Message.Split('\n')[0]}", retryLater: ex.IsTransient);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or AuthenticationFailedException)
        {
            throw new ConnectorException($"Service Bus {outbox.Where} refused the sign-in: {ex.Message.Split('\n')[0]}", retryLater: false);
        }
        ctx.Log.LogInformation("[servicebus] {Id} -> {Entity} as {MessageId}", ctx.DocumentId, outbox.Where, messageId);
        return new DeliveryResult(outbox.Where, messageId, $"Sent {ctx.Output.Name} to Service Bus {outbox.Where} as message {messageId}.");
    }

    private static string Entity(TargetOptions target)
        => target.Setting("Queue") is { Length: > 0 } queue ? queue : target.Setting("Topic");

    /// <summary>At most 128 characters, as Service Bus allows; letters, digits, '-', '_' and '.' only.</summary>
    public static string MessageIdFor(string type, string area, string documentId)
    {
        var text = new StringBuilder();
        foreach (char c in $"{type}_{area}_{documentId}")
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return text.Length <= 128 ? text.ToString() : text.ToString(0, 128);
    }
}

/// <summary>The Settings side of the Service Bus target: a queue, or a topic.</summary>
public sealed class ServiceBusDestinationKind(SourceRuntime runtime) : IDestinationKind
{
    public TargetDescriptor Descriptor { get; } = new(
        ConnectionKinds.ServiceBus, "Azure Service Bus",
        "Sent as a JSON message to a queue or topic, for example one a Dynamics 365 Finance and Operations receive flow reads.",
        ConnectionKinds.ServiceBus,
        new[]
        {
            new SourceSetting("Queue", "Queue", SourceSteps.Which, Required: false, Hint: "Give a queue or a topic."),
            new SourceSetting("Topic", "Topic", SourceSteps.Which, Required: false),
        },
        null);

    public bool IsConnected => runtime.Options.Connections.Any(c => c.Kind == ConnectionKinds.ServiceBus && c.Problems().Count == 0);

    public Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, TargetOptions target)
        => Task.FromResult<IReadOnlyList<SourceChoice>>(Array.Empty<SourceChoice>());
}

/// <summary>A Service Bus namespace: the connection string as the secret, or the namespace for a managed identity.</summary>
public sealed class ServiceBusConnectionKind : IConnectionKind
{
    public ConnectionDescriptor Descriptor { get; } = new(
        ConnectionKinds.ServiceBus, "Azure Service Bus", "Documents from and to Service Bus queues and topics, for example of Dynamics 365 Finance and Operations.",
        new[]
        {
            new SourceSetting("Namespace", "Namespace (only for a managed identity in Azure)", SourceSteps.Which, Required: false,
                Hint: "<name>.servicebus.windows.net. Leave it empty when you give the connection string."),
        },
        "Connection string", "From the namespace, Shared access policies. Stored encrypted on this machine.");

    public async Task<string> TestAsync(ConnectionOptions connection)
    {
        try
        {
            var properties = await AzureServiceBus.Administration(connection).GetNamespacePropertiesAsync();
            return $"Signed in to {properties.Value.Name} ({properties.Value.MessagingSku}).";
        }
        catch (UnauthorizedAccessException)
        {
            return LimitedAccess(connection);
        }
        catch (Exception ex) when (ex is ServiceBusException or AuthenticationFailedException or FormatException or ArgumentException)
        {
            throw new ConnectorException("Service Bus refused the sign-in: " + ex.Message.Split('\n')[0], retryLater: false);
        }
    }

    /// <summary>The check's answer when the namespace is reached without manage rights: a connection string signs in
    /// with its key, a namespace alone with the managed identity, as <see cref="AzureServiceBus.Administration"/> picks.</summary>
    public static string LimitedAccess(ConnectionOptions connection)
        => connection.Secret.Length > 0
            ? "Reached the namespace. This key may only send or receive, which is all Hive BDC needs."
            : "Reached the namespace. This identity may only send or receive, which is all Hive BDC needs.";
}