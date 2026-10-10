using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>One release's entry in the changelog.</summary>
public sealed record ChangelogEntry(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("changes")] string[]? Changes);

/// <summary>The changelog document (changelog.json) — embedded in the app and mirrored on the feed.</summary>
public sealed record ChangelogDoc(
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("releases")] ChangelogEntry[]? Releases);

/// <summary>Reads the changelog bundled with this build (offline; matches the installed version).</summary>
public static class Changelog
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static List<ChangelogEntry> LoadEmbedded()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("Assets/changelog.json", UriKind.Relative));
            if (info is null) return new();
            using var s = info.Stream;
            var doc = JsonSerializer.Deserialize<ChangelogDoc>(s, Opts);
            return doc?.Releases?.ToList() ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Built-in changelog could not be read: " + ex.Message);
            return new();
        }
    }
}
