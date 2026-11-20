namespace Balsoft.Hive.BDC.Connectors;

public sealed record SourceInfo(string Kind, SourceDescriptor Descriptor, bool Connected);

public sealed record ChoicesResult(IReadOnlyList<SourceChoice> Choices, string? Error);

/// <summary>What the dashboard asks about sources: which ones this version has, and the values for one dropdown.</summary>
public static class SourceCatalog
{
    public static IReadOnlyList<SourceInfo> Describe(ConnectorRegistry registry, SourceRuntime runtime)
        => registry.SourceKindNames
            .Select(kind => registry.Source(kind, runtime))
            .Where(s => s is not null)
            .Select(s => new SourceInfo(s!.Descriptor.Kind, s.Descriptor, s.IsConnected))
            .ToList();

    public static async Task<ChoicesResult> ChoicesAsync(ConnectorRegistry registry, SourceRuntime runtime, string kind, string lookup,
        SourceOptions source)
    {
        var connector = registry.Source(kind, runtime);
        if (connector is null)
            return new ChoicesResult(Array.Empty<SourceChoice>(), $"The document system '{kind}' is not known to this version of Hive BDC.");
        try { return new ChoicesResult(await connector.ChoicesAsync(lookup, source), null); }
        catch (ConnectorException ex) { return new ChoicesResult(Array.Empty<SourceChoice>(), ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        { return new ChoicesResult(Array.Empty<SourceChoice>(), $"{connector.Descriptor.Name}: {ex.Message}"); }
    }
}

/// <summary>Checks over the inbound flows before they are saved.</summary>
public static class InboundChecks
{
    /// <summary>Two switched-on flows that pick up the same records would hand each record to the ERP twice.</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<FlowOptions> flows, ConnectorRegistry registry, SourceRuntime runtime)
    {
        var problems = new List<string>();
        foreach (var group in flows.Where(f => f.Enabled).GroupBy(f => f.Source.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var source = registry.Source(group.Key, runtime);
            if (source is null) continue;
            foreach (var same in group.GroupBy(f => source.Scope(f.Source)).Where(g => g.Key.Trim('|').Length > 0 && g.Count() > 1))
                problems.Add("The inbound flows " +
                             string.Join(" and ", same.Select(f => $"\"{f.DisplayName}\"" + (f.DataAreaId.Length > 0 ? $" ({f.DataAreaId})" : ""))) +
                             $" read the same documents from {source.Descriptor.Name}. Switch one off or change which documents it picks up.");
        }
        foreach (var flow in flows)
            foreach (var target in flow.Targets.Where(t => t.Enabled))
                if (registry.Inbound(target.Kind, runtime.Options) is { } destination)
                    problems.AddRange(destination.SettingsProblems(target).Select(p =>
                        $"Inbound flow \"{flow.DisplayName}\"" + (flow.DataAreaId.Length > 0 ? $" ({flow.DataAreaId})" : "") + ": " + p));
        return problems;
    }
}