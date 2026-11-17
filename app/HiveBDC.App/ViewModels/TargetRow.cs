using CommunityToolkit.Mvvm.ComponentModel;
using Balsoft.Hive.BDC.App.Services;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// One further target of a company entry, as the list shows it. Targets of a kind this dashboard cannot edit (written
/// by a newer version) keep every setting and are sent back unchanged.
/// </summary>
public sealed partial class TargetRow : ObservableObject
{
    public const string DocuWareKind = "docuware";

    /// <summary>Remove was clicked once; the card asks before the item goes.</summary>
    [ObservableProperty] private bool _confirmRemove;

    [ObservableProperty] private bool _enabled = true;

    public string Kind { get; init; } = "";
    public string Connection { get; init; } = "";
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Fields { get; init; } = new();

    public bool IsDocuWare => string.Equals(Kind, DocuWareKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>The destination kind's name as the service describes it (Settings sets it when the list is loaded).</summary>
    public string KindName { get; set; } = "";

    public string KindLabel => KindName.Length > 0 ? KindName : IsDocuWare ? "DocuWare" : Kind;

    /// <summary>The line next to the name in the destination list.</summary>
    public string Detail => IsDocuWare
        ? $"File cabinet \"{Setting("FileCabinetName", Setting("FileCabinet"))}\", {Fields.Count} field(s) filled" +
          (Setting("MatchField").Length > 0 ? $"; never stored twice (by {Setting("MatchField")})." : ".")
        : Settings.Count == 0 ? "Not set up yet"
          : string.Join(", ", Settings.Where(p => p.Value.Length > 0 && !p.Key.EndsWith("Name", StringComparison.OrdinalIgnoreCase)).Select(p => $"{p.Key} {p.Value}"));

    public string Summary => IsDocuWare
        ? $"DocuWare: file cabinet \"{Setting("FileCabinetName", Setting("FileCabinet"))}\", {Fields.Count} field(s)" +
          (Setting("MatchField").Length > 0 ? $", never stored twice (by {Setting("MatchField")})" : "")
        : $"{Kind} (set up with a newer version of Hive BDC)";

    public string Setting(string name, string fallback = "")
        => Settings.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    public static TargetRow FromDto(TargetConfigDto dto) => new()
    {
        Kind = dto.Kind ?? "",
        Connection = dto.Connection ?? "",
        Enabled = dto.Enabled,
        Settings = new Dictionary<string, string>(dto.Settings ?? new(), StringComparer.OrdinalIgnoreCase),
        Fields = new Dictionary<string, string>(dto.Fields ?? new()),
    };

    public TargetConfigDto ToDto() => new()
    {
        Kind = Kind,
        Connection = Connection,
        Enabled = Enabled,
        Settings = new Dictionary<string, string>(Settings),
        Fields = new Dictionary<string, string>(Fields),
    };
}