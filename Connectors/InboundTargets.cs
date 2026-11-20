using System.Text;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// Where an inbound flow puts the CSV it made, and the record's file: the place the ERP reads from. LocalOnly kinds
/// (a folder) are not offered in Azure. The dashboard builds the "Goes to" settings from this.
/// </summary>
public sealed record InboundDescriptor(
    string Kind,
    string Name,
    string Description,
    string ConnectionKind,
    bool LocalOnly,
    IReadOnlyList<SourceSetting> Settings);

/// <summary>One record of an inbound flow on its way to the place the ERP reads.</summary>
public sealed record InboundDelivery(FlowOptions Flow, TargetOptions Target, string RecordId, ILogger Log);

/// <summary>
/// A place an inbound flow delivers to. The file is saved before the CSV, so the ERP never reads a CSV whose file is
/// missing; a repeat of either replaces the earlier one by name or message id.
/// </summary>
public interface IInboundTarget
{
    InboundDescriptor Descriptor { get; }

    /// <summary>What the settings alone get wrong, in words; checked when the settings are saved.</summary>
    IReadOnlyList<string> SettingsProblems(TargetOptions target);

    /// <summary>What is missing before a record can be delivered (settings, connections), in words.</summary>
    IReadOnlyList<string> Problems(TargetOptions target);

    /// <summary>Where the record's file will be, as {FilePath} in the CSV.</summary>
    string FileLocation(TargetOptions target, string fileName);

    /// <summary>Saves the record's file and returns how the history names it.</summary>
    string SaveFile(InboundDelivery delivery, string fileName, byte[] content, string contentType);

    /// <summary>Delivers the CSV; the summary says where to, e.g. "to C:\ERP\in".</summary>
    DeliveryResult DeliverCsv(InboundDelivery delivery, string csvName, string csv);
}

/// <summary>Setting names every inbound kind shares.</summary>
public static class InboundSettings
{
    public const string SaveFile = "SaveFile";

    public static bool SavesFile(TargetOptions target)
        => string.Equals(target.Setting(SaveFile), "true", StringComparison.OrdinalIgnoreCase);

    public static string Prefix(string value)
    {
        string prefix = (value ?? "").Trim().Replace('\\', '/').TrimStart('/');
        return prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    /// <summary>The CSV for Blob Storage and Service Bus: UTF-8 without a BOM, which a receive flow would read as part of the first column name.</summary>
    public static byte[] Utf8(string csv) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv);

    /// <summary>Azure answers that pass on their own (busy, throttled, unreachable) wait for the next scan; the rest fail in words.</summary>
    public static ConnectorException AzureFailure(string where, Exception ex) => ex switch
    {
        RequestFailedException r => new ConnectorException($"{where}: {FirstLine(r.Message)}",
            retryLater: r.Status is 0 or 408 or 429 or >= 500),
        AuthenticationFailedException or UnauthorizedAccessException => new ConnectorException($"{where} refused the sign-in: {FirstLine(ex.Message)}", retryLater: false),
        ServiceBusException s => new ConnectorException($"{where}: {FirstLine(s.Message)}", retryLater: s.IsTransient),
        _ => new ConnectorException($"{where}: {FirstLine(ex.Message)}", retryLater: ex is HttpRequestException or TimeoutException),
    };

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}

/// <summary>A folder the ERP reads: the CSV with a BOM (as AX expects it) and the file next to it. Windows only.</summary>
public sealed class FolderInboundTarget : IInboundTarget
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public InboundDescriptor Descriptor { get; } = new(
        "folder", "Folder the ERP reads", "A CSV file in a folder the ERP reads, for example AX or another ERP on your network.",
        "", LocalOnly: true,
        new[] { new SourceSetting("Folder", "Folder the ERP reads", SourceSteps.Which) });

    public IReadOnlyList<string> SettingsProblems(TargetOptions target) => Array.Empty<string>();

    public IReadOnlyList<string> Problems(TargetOptions target)
        => target.Setting("Folder").Length == 0 ? new[] { "Set the folder the ERP reads." } : Array.Empty<string>();

    public string FileLocation(TargetOptions target, string fileName) => Path.Combine(target.Setting("Folder"), fileName);

    public string SaveFile(InboundDelivery delivery, string fileName, byte[] content, string contentType)
    {
        CsvFile.WriteAtomic(FileLocation(delivery.Target, fileName), content);
        return fileName;
    }

    public DeliveryResult DeliverCsv(InboundDelivery delivery, string csvName, string csv)
    {
        string folder = delivery.Target.Setting("Folder");
        string path = Path.Combine(folder, csvName);
        CsvFile.WriteAtomic(path, Utf8WithBom.GetPreamble().Concat(Utf8WithBom.GetBytes(csv)).ToArray());
        return new DeliveryResult(path, null, $"to {folder}");
    }
}

/// <summary>
/// A blob container the ERP reads, for example the pending prefix of a Dynamics 365 F&amp;O Blob Storage endpoint:
/// the CSV under Prefix, the file under FilePrefix. The receive flow takes every blob under its prefix as a message,
/// so the files must lie outside it.
/// </summary>
public sealed class BlobInboundTarget(BDCOptions options, Func<ConnectionOptions, string, IBlobStore> stores) : IInboundTarget
{
    public InboundDescriptor Descriptor { get; } = new(
        "blob", "Blob Storage", "A CSV file in a blob container, for example the one a Dynamics 365 F&O receive flow reads.",
        ConnectionKinds.AzureStorage, LocalOnly: false,
        new[]
        {
            new SourceSetting("Container", "Container", SourceSteps.Which),
            new SourceSetting("Prefix", "Folder in the container", SourceSteps.Which, Required: false,
                Hint: "The folder the ERP takes CSV files from. In F&O the pending prefix of the Blob endpoint."),
            new SourceSetting("FilePrefix", "Folder for the files", SourceSteps.After, Required: false, DependsOn: InboundSettings.SaveFile,
                Hint: "Outside the folder above, so the ERP never takes a file for a CSV. Its address is {FilePath} in the CSV."),
        });

    public const string DefaultFilePrefix = "files/";

    public static string FilePrefix(TargetOptions target)
        => target.Setting("FilePrefix") is { Length: > 0 } p ? InboundSettings.Prefix(p) : DefaultFilePrefix;

    public IReadOnlyList<string> SettingsProblems(TargetOptions target)
    {
        if (!InboundSettings.SavesFile(target)) return Array.Empty<string>();
        string prefix = InboundSettings.Prefix(target.Setting("Prefix"));
        string files = FilePrefix(target);
        return files.StartsWith(prefix, StringComparison.Ordinal)
            ? new[] { prefix.Length == 0
                ? $"The ERP reads the whole container, so it would take each file in '{files}' for a CSV. Set the folder in the container the ERP reads."
                : $"The folder for the files '{files}' lies inside the folder the ERP reads '{prefix}', so the ERP would take each file for a CSV. Choose a folder outside it." }
            : Array.Empty<string>();
    }

    public IReadOnlyList<string> Problems(TargetOptions target)
    {
        var problems = new List<string>();
        var connection = options.Connection(target.Connection);
        if (connection is null)
            problems.Add($"Choose the storage connection the ERP reads from (it is set up under {SettingsPaths.Pick("Settings > Connections", "Connections")}).");
        else
            problems.AddRange(connection.Problems());
        if (target.Setting("Container").Length == 0) problems.Add("Set the container the ERP reads.");
        problems.AddRange(SettingsProblems(target));
        return problems;
    }

    private IBlobStore Store(TargetOptions target) => stores(options.Connection(target.Connection)!, target.Setting("Container"));

    public string FileLocation(TargetOptions target, string fileName)
        => $"{Store(target).ContainerUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{FilePrefix(target)}{fileName}";

    public string SaveFile(InboundDelivery delivery, string fileName, byte[] content, string contentType)
    {
        var store = Store(delivery.Target);
        string name = FilePrefix(delivery.Target) + fileName;
        try { store.Upload(name, content, contentType); }
        catch (Exception ex) when (ex is not ConnectorException) { throw InboundSettings.AzureFailure("Blob Storage", ex); }
        return $"{delivery.Target.Setting("Container")}/{name}";
    }

    public DeliveryResult DeliverCsv(InboundDelivery delivery, string csvName, string csv)
    {
        var store = Store(delivery.Target);
        string prefix = InboundSettings.Prefix(delivery.Target.Setting("Prefix"));
        string name = prefix + csvName;
        try { store.Upload(name, InboundSettings.Utf8(csv), "text/csv; charset=utf-8"); }
        catch (Exception ex) when (ex is not ConnectorException) { throw InboundSettings.AzureFailure("Blob Storage", ex); }
        string where = $"{store.ContainerUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{prefix}";
        delivery.Log.LogInformation("[import] {Id} -> {Container}/{Name}", delivery.RecordId, store.ContainerUri, name);
        return new DeliveryResult(where, name, $"to {where}");
    }
}

/// <summary>
/// A Service Bus queue or topic the ERP reads, for example by a Dynamics 365 F&amp;O receive flow: the CSV is the
/// message body (text/csv, no BOM), MessageId "{flow}_{record}" (the same for a repeat, so duplicate detection drops
/// it), Subject the document type. A message cannot carry the file, so it goes to a blob container (FileConnection,
/// FileContainer, FilePrefix).
/// </summary>
public sealed class ServiceBusInboundTarget(BDCOptions options, Func<ConnectionOptions, string, IBusOutbox> outboxes,
    Func<ConnectionOptions, string, IBlobStore> stores) : IInboundTarget
{
    public InboundDescriptor Descriptor { get; } = new(
        ConnectionKinds.ServiceBus, "Service Bus", "The CSV as a message to a queue or topic, for example one a Dynamics 365 F&O receive flow reads.",
        ConnectionKinds.ServiceBus, LocalOnly: false,
        new[]
        {
            new SourceSetting("Queue", "Queue", SourceSteps.Which, Required: false, Hint: "Give a queue or a topic."),
            new SourceSetting("Topic", "Topic", SourceSteps.Which, Required: false),
            new SourceSetting("FileConnection", "Storage for the files", SourceSteps.After, DependsOn: InboundSettings.SaveFile,
                Lookup: "connections:" + ConnectionKinds.AzureStorage, Hint: "A message cannot carry the file, so it is saved in Blob Storage."),
            new SourceSetting("FileContainer", "Container for the files", SourceSteps.After, DependsOn: InboundSettings.SaveFile),
            new SourceSetting("FilePrefix", "Folder for the files", SourceSteps.After, Required: false, DependsOn: InboundSettings.SaveFile,
                Hint: "Its address is {FilePath} in the CSV."),
        });

    public IReadOnlyList<string> SettingsProblems(TargetOptions target) => Array.Empty<string>();

    public IReadOnlyList<string> Problems(TargetOptions target)
    {
        var problems = new List<string>();
        string connections = SettingsPaths.Pick("Settings > Connections", "Connections");
        var connection = options.Connection(target.Connection);
        if (connection is null) problems.Add($"Choose the Service Bus connection the ERP reads from (it is set up under {connections}).");
        else problems.AddRange(connection.Problems());
        if (Entity(target).Length == 0) problems.Add("Set the queue or topic the ERP reads.");
        if (InboundSettings.SavesFile(target))
        {
            var files = options.Connection(target.Setting("FileConnection"));
            if (files is null) problems.Add($"Choose the storage the files go to (it is set up under {connections}).");
            else problems.AddRange(files.Problems());
            if (target.Setting("FileContainer").Length == 0) problems.Add("Set the container the files go to.");
        }
        return problems;
    }

    private static string Entity(TargetOptions target)
        => target.Setting("Queue") is { Length: > 0 } queue ? queue : target.Setting("Topic");

    private IBlobStore FileStore(TargetOptions target)
        => stores(options.Connection(target.Setting("FileConnection"))!, target.Setting("FileContainer"));

    public string FileLocation(TargetOptions target, string fileName)
        => $"{FileStore(target).ContainerUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{InboundSettings.Prefix(target.Setting("FilePrefix"))}{fileName}";

    public string SaveFile(InboundDelivery delivery, string fileName, byte[] content, string contentType)
    {
        string name = InboundSettings.Prefix(delivery.Target.Setting("FilePrefix")) + fileName;
        try { FileStore(delivery.Target).Upload(name, content, contentType); }
        catch (Exception ex) when (ex is not ConnectorException) { throw InboundSettings.AzureFailure("Blob Storage", ex); }
        return $"{delivery.Target.Setting("FileContainer")}/{name}";
    }

    public DeliveryResult DeliverCsv(InboundDelivery delivery, string csvName, string csv)
    {
        var outbox = outboxes(options.Connection(delivery.Target.Connection)!, Entity(delivery.Target));
        var flow = delivery.Flow;
        string messageId = MessageIdFor(flow.Id, delivery.RecordId);
        byte[] body = InboundSettings.Utf8(csv);
        var properties = new Dictionary<string, object>
        {
            ["documentType"] = flow.TypeLabel,
            ["dataAreaId"] = flow.DataAreaId,
            ["recordId"] = delivery.RecordId,
            ["fileName"] = csvName,
        };
        try
        {
            outbox.Send(body, messageId, flow.TypeLabel, properties, "text/csv");
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageSizeExceeded)
        {
            throw new ConnectorException(
                $"The CSV has {(body.Length + 1023) / 1024} KB, more than Service Bus {outbox.Where} takes in one message " +
                "(256 KB on the Standard tier). Use fewer columns or rows, or Blob Storage.", retryLater: false);
        }
        catch (Exception ex) when (ex is not ConnectorException) { throw InboundSettings.AzureFailure($"Service Bus {outbox.Where}", ex); }
        delivery.Log.LogInformation("[import] {Id} -> {Entity} as {MessageId}", delivery.RecordId, outbox.Where, messageId);
        return new DeliveryResult(outbox.Where, messageId, $"to Service Bus {outbox.Where} as message {messageId}");
    }

    /// <summary>At most 128 characters, as Service Bus allows; letters, digits, '-', '_' and '.' only.</summary>
    public static string MessageIdFor(string flowId, string recordId)
    {
        var text = new StringBuilder();
        foreach (char c in $"{flowId}_{recordId}")
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return text.Length <= 128 ? text.ToString() : text.ToString(0, 128);
    }
}