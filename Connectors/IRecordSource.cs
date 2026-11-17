using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>A failure a connector reports in words. RetryLater: the system is busy or unreachable, so the next scan tries again.</summary>
public class ConnectorException(string message, bool retryLater) : Exception(message)
{
    public bool RetryLater { get; } = retryLater;
}

/// <summary>The steps a source's settings are shown in.</summary>
public static class SourceSteps
{
    public const string Which = "which";
    public const string After = "after";
}

/// <summary>
/// One setting of a source as the dashboard shows it. Lookup names the list the source fills the dropdown from
/// (<see cref="IRecordSource.ChoicesAsync"/>); DependsOn is the setting that must be filled first.
/// </summary>
public sealed record SourceSetting(string Key, string Label, string Step, bool Required = true, string? Lookup = null,
    string? DependsOn = null, string Hint = "");

/// <summary>What a document system is and what an inbound flow from it needs; the dashboard builds its screens from this.</summary>
public sealed record SourceDescriptor(
    string Kind,
    string Name,
    string Description,
    string ConnectionKind,
    string RequiredFeature,
    string RecordNoun,
    string FilePrefix,
    string DefaultFileName,
    IReadOnlyList<SourceSetting> Settings,
    string FieldsLookup = "fields");

public sealed record SourceChoice(string Value, string Label, string Detail = "");

/// <summary>
/// The settings a source works with. The dashboard's unsaved values (a new password, a changed SharePoint app) come
/// in as overrides, so a dropdown can be filled before Save.
/// </summary>
public sealed record SourceRuntime(BDCOptions Options, ILogger Log)
{
    public Func<string, ConnectionOptions?>? ConnectionOverride { get; init; }
    public SharePointAuthOptions? SharePointAuthOverride { get; init; }

    public ConnectionOptions? Connection(string? id) => ConnectionOverride?.Invoke(id ?? "") ?? Options.Connection(id);
    public SharePointAuthOptions? SharePointAuth => SharePointAuthOverride ?? Options.SharePointAuth;
}

/// <summary>
/// A document system an inbound flow reads from. Records marked ready are read, handed to the ERP as a CSV (and their
/// file), then marked done so they are not read again. A new system is one class registered by kind in
/// <see cref="ConnectorRegistry"/>; the processor, the settings file and the dashboard do not change.
/// </summary>
public interface IRecordSource
{
    SourceDescriptor Descriptor { get; }

    /// <summary>Whether the connection this source needs is set up (the "Connected" mark in the dashboard).</summary>
    bool IsConnected => true;

    /// <summary>What is missing before a flow can run, in words. Empty when it is ready.</summary>
    IReadOnlyList<string> Problems(SourceOptions source);

    Task<IReadOnlyList<string>> ReadyAsync(SourceOptions source, int max);

    Task<SourceRecord> ReadAsync(SourceOptions source, string id);

    Task<DocumentFile?> FileAsync(SourceOptions source, string id);

    /// <summary>Marks the record handed over and returns the history line that says so.</summary>
    Task<string> MarkDoneAsync(SourceOptions source, string id);

    Task<IReadOnlyList<SourceChoice>> ChoicesAsync(string lookup, SourceOptions source);

    /// <summary>The record's key inside the source; with the flow id it keeps a written record from being written twice.</summary>
    string RecordKey(SourceOptions source, string id);

    /// <summary>Which records the flow picks up; two switched-on flows with the same scope would read the same records.</summary>
    string Scope(SourceOptions source);
}