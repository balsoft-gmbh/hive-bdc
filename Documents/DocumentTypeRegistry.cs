using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Resolves the documentType of a dropped JSON to its <see cref="DocumentTypeOptions"/>,
/// and registers types the ERP sends that nobody configured yet.
///
/// Resolution order: the configured list (appsettings, which ships with the six built-in
/// types), then the built-in catalog (in case someone deleted an entry by hand), then
/// discovery: the id is appended to appsettings.json as a PDF pass-through routed like the
/// company entry it was found under, the host's reload watcher picks the file up, and the
/// dashboard shows the new type. Until that reload lands, the entry also lives in memory
/// here so the rest of the scan cycle (and every later cycle) resolves it without touching
/// the file again.
/// </summary>
public sealed class DocumentTypeRegistry
{
    private readonly string _settingsPath;
    private readonly ILogger _log;
    private readonly Dictionary<string, DocumentTypeOptions> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public DocumentTypeRegistry(string settingsPath, ILogger log)
    {
        _settingsPath = settingsPath;
        _log = log;
    }

    /// <summary>The configured type for <paramref name="id"/>, or null when nothing is configured or built in.</summary>
    public static DocumentTypeOptions? Find(BDCOptions opts, string? id)
    {
        string clean = DocumentTypeCatalog.CleanId(id);
        if (clean.Length == 0) clean = DocumentTypeCatalog.DefaultId;
        return opts.DocumentTypes.FirstOrDefault(t => t.Matches(clean))
               ?? DocumentTypeCatalog.BuiltIn.FirstOrDefault(t => t.Matches(clean));
    }

    /// <summary>
    /// Resolve, discovering when unknown. <paramref name="partyHint"/> is what the JSON said
    /// (blank = Customer); <paramref name="seed"/> is the company entry of the folder the pair
    /// was found in, copied onto the new type so it keeps flowing. Never returns null.
    /// </summary>
    public DocumentTypeOptions Resolve(BDCOptions opts, string? id, string? partyHint, DocumentRoute? seed, out bool discoveredNow)
    {
        discoveredNow = false;
        var found = Find(opts, id);
        if (found is not null) return found;

        string clean = DocumentTypeCatalog.CleanId(id);
        lock (_gate)
        {
            if (_discovered.TryGetValue(clean, out var cached)) return cached;

            var party = Enum.TryParse<DocumentParty>(partyHint?.Trim(), ignoreCase: true, out var p) ? p : DocumentParty.Customer;
            var entry = DocumentTypeCatalog.Discovered(clean, party, seed);
            _discovered[clean] = entry;
            discoveredNow = true;

            try
            {
                bool written = SettingsFile.Update(_settingsPath, section =>
                {
                    if (section["DocumentTypes"] is not JsonArray arr)
                    {
                        arr = new JsonArray();
                        section["DocumentTypes"] = arr;
                    }
                    foreach (var node in arr)
                        if (node is JsonObject o && DocumentTypeCatalog.SameId(o["Id"]?.GetValue<string>(), clean))
                            return false;   // already there (another cycle got here first)
                    arr.Add(SettingsFile.DocumentTypeNode(entry));
                    return true;
                });
                if (written)
                    _log.LogWarning(
                        "New document type '{Type}' received from the ERP — registered in settings as a PDF pass-through " +
                        "({Party}, mail on{Route}). Review it under Settings > Document types.",
                        clean, party,
                        seed is null ? "" : $", company {seed.DataAreaId}, folder {seed.Input}, SharePoint {(seed.SharePoint.Enabled ? "on" : "off")}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "New document type '{Type}' could not be written to appsettings.json; it is handled with defaults " +
                    "for this run only.", clean);
            }
            return entry;
        }
    }

    /// <summary>
    /// An auto-registered type reached from another company's folder: give it that company's
    /// entry (a copy of <paramref name="seed"/>, the folder's own route) instead of leaving the
    /// pair pending, the same way the type got its first entry. Types someone configured are
    /// never extended here; the caller holds the pair and the log names the missing company.
    /// </summary>
    public DocumentRoute AddCompany(DocumentTypeOptions type, DocumentRoute seed)
    {
        lock (_gate)
        {
            var existing = type.Companies.FirstOrDefault(r =>
                string.Equals(r.DataAreaId, seed.DataAreaId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            var route = seed.Clone();
            type.Companies.Add(route);
            try
            {
                bool written = SettingsFile.Update(_settingsPath, section =>
                {
                    if (section["DocumentTypes"] is not JsonArray arr) return false;
                    for (int i = 0; i < arr.Count; i++)
                        if (arr[i] is JsonObject o && DocumentTypeCatalog.SameId(o["Id"]?.GetValue<string>(), type.Id))
                        {
                            arr[i] = SettingsFile.DocumentTypeNode(type);
                            return true;
                        }
                    return false;
                });
                if (written)
                    _log.LogWarning(
                        "Auto-registered document type '{Type}' arrived for company {Area}: added that company in settings " +
                        "(folder {Input}, SharePoint {Sp}). Review it under Settings > Document types.",
                        type.Id, route.DataAreaId, route.Input, route.SharePoint.Enabled ? "on" : "off");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Company {Area} for document type '{Type}' could not be written to appsettings.json; it is used for this run only.",
                    route.DataAreaId, type.Id);
            }
            return route;
        }
    }
}
