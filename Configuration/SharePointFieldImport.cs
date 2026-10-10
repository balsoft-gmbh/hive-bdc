using System.Text.Json;

namespace Balsoft.Hive.BDC.Configuration;

// This file is compiled into the service AND linked into the dashboard
// (app/HiveBDC.App/HiveBDC.App.csproj), so it only uses System.* types and
// carries its own usings instead of relying on the service's GlobalUsings.

/// <summary>
/// A SharePoint field template import file ("format": "HiveBDC.SharePointFields"):
/// per document type and AX company, the SharePoint target and the column templates for
/// that company entry. Typically prepared from the customer's real library columns and
/// imported in Settings, so nobody types internal column names by hand.
/// <code>
/// { "format": "HiveBDC.SharePointFields", "version": 1, "description": "...",
///   "documentTypes": [
///     { "id": "SalesInvoice",
///       "companies": [
///         { "dataAreaId": "001",
///           "sharePoint": { "siteUrl": "https://...", "library": "SalesInvoices", "folder": "",
///                           "fields": { "Title": "{invoiceNumber}", "DocumentDate": "{issueDate:date}" } } } ] } ] }
/// </code>
/// </summary>
public sealed class SharePointFieldImportFile
{
    public const string FormatName = "HiveBDC.SharePointFields";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = "";
    public int Version { get; set; }

    /// <summary>Free text for the person importing (customer, source sites, date). Not applied.</summary>
    public string? Description { get; set; }

    public List<SharePointFieldImportType> DocumentTypes { get; set; } = new();
}

/// <summary>One document type in an import file; <see cref="Id"/> matches the configured type id (case-insensitive).</summary>
public sealed class SharePointFieldImportType
{
    public string Id { get; set; } = "";
    public List<SharePointFieldImportCompany> Companies { get; set; } = new();
}

/// <summary>One company entry of a document type in an import file.</summary>
public sealed class SharePointFieldImportCompany
{
    public string DataAreaId { get; set; } = "";
    public SharePointFieldImportTarget SharePoint { get; set; } = new();
}

/// <summary>
/// What an import sets on a company entry. Every property is optional: an absent (null)
/// value keeps what is configured. <see cref="Fields"/>, when present, REPLACES the
/// entry's column templates, so the result matches the file exactly.
/// </summary>
public sealed class SharePointFieldImportTarget
{
    public bool? Enabled { get; set; }
    public string? SiteUrl { get; set; }
    public string? Library { get; set; }
    public string? Folder { get; set; }
    public Dictionary<string, string>? Fields { get; set; }
}

/// <summary>A configured company entry an import can be applied to (service options or a dashboard row).</summary>
public interface ISharePointFieldImportRoute
{
    string DataAreaId { get; }
    void ApplySharePoint(SharePointFieldImportTarget target);
}

/// <summary>A configured document type and its company entries, as seen by the import.</summary>
public sealed record SharePointFieldImportScope(string TypeId, IReadOnlyList<ISharePointFieldImportRoute> Routes);

/// <summary>What an import changed and what it could not place.</summary>
public sealed record SharePointFieldImportResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

public static class SharePointFieldImport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parse and validate an import file. Returns false with every problem found, never throws on bad input.</summary>
    public static bool TryParse(string json, out SharePointFieldImportFile? file, out List<string> errors)
    {
        errors = new List<string>();
        file = null;
        try
        {
            file = JsonSerializer.Deserialize<SharePointFieldImportFile>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            errors.Add("The file is not valid JSON: " + ex.Message);
            return false;
        }
        if (file is null)
        {
            errors.Add("The file is empty.");
            return false;
        }

        if (!string.Equals(file.Format?.Trim(), SharePointFieldImportFile.FormatName, StringComparison.OrdinalIgnoreCase))
            errors.Add($"\"format\" must be \"{SharePointFieldImportFile.FormatName}\"; this is not a SharePoint field template file.");
        if (file.Version != SharePointFieldImportFile.CurrentVersion)
            errors.Add($"\"version\" {file.Version} is not supported (expected {SharePointFieldImportFile.CurrentVersion}).");
        file.DocumentTypes ??= new();
        if (file.DocumentTypes.Count == 0)
            errors.Add("\"documentTypes\" is empty; there is nothing to import.");

        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.DocumentTypes.Count; i++)
        {
            var t = file.DocumentTypes[i];
            if (t is null) { errors.Add($"documentTypes[{i}] is null."); continue; }
            string id = t.Id?.Trim() ?? "";
            string label = id.Length > 0 ? $"Document type '{id}'" : $"documentTypes[{i}]";
            if (id.Length == 0) errors.Add($"{label} has no \"id\".");
            else if (!seenTypes.Add(id)) errors.Add($"{label} is listed twice.");

            t.Companies ??= new();
            if (t.Companies.Count == 0) errors.Add($"{label} has no companies.");
            var seenAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in t.Companies)
            {
                if (c is null) { errors.Add($"{label} has a null company entry."); continue; }
                string area = c.DataAreaId?.Trim() ?? "";
                if (area.Length == 0) { errors.Add($"{label}: a company entry has no \"dataAreaId\"."); continue; }
                if (!seenAreas.Add(area)) errors.Add($"{label}: company '{area}' is listed twice.");

                var sp = c.SharePoint;
                if (sp is null) { errors.Add($"{label}, company '{area}': \"sharePoint\" is missing."); continue; }
                if (!string.IsNullOrWhiteSpace(sp.SiteUrl)
                    && (!Uri.TryCreate(sp.SiteUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                    errors.Add($"{label}, company '{area}': site URL '{sp.SiteUrl}' is not a valid https URL.");
                if (sp.Fields is not null && sp.Fields.Keys.Any(k => string.IsNullOrWhiteSpace(k)))
                    errors.Add($"{label}, company '{area}': a field has an empty column name.");
            }
        }

        if (errors.Count > 0) { file = null; return false; }
        return true;
    }

    /// <summary>
    /// Apply a parsed file to the configured types. A type or company the file names but the
    /// configuration does not have is reported as skipped, never created: a company entry
    /// needs an input folder, which only the person configuring the machine knows.
    /// </summary>
    public static SharePointFieldImportResult Apply(SharePointFieldImportFile file, IEnumerable<SharePointFieldImportScope> configured)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var scopes = configured.ToList();

        foreach (var t in file.DocumentTypes)
        {
            string id = t.Id.Trim();
            var scope = scopes.FirstOrDefault(s => string.Equals(s.TypeId?.Trim(), id, StringComparison.OrdinalIgnoreCase));
            if (scope is null)
            {
                skipped.Add($"Document type '{id}' is not configured here; its {t.Companies.Count} company entr{(t.Companies.Count == 1 ? "y was" : "ies were")} not imported.");
                continue;
            }

            foreach (var c in t.Companies)
            {
                string area = c.DataAreaId.Trim();
                var route = scope.Routes.FirstOrDefault(r => string.Equals(r.DataAreaId?.Trim(), area, StringComparison.OrdinalIgnoreCase));
                if (route is null)
                {
                    skipped.Add($"Document type '{id}' has no company entry '{area}'. Add the company with its input folder first, then import again.");
                    continue;
                }

                route.ApplySharePoint(c.SharePoint);
                int n = c.SharePoint.Fields?.Count ?? 0;
                string target = string.IsNullOrWhiteSpace(c.SharePoint.Library) ? "" : $" -> {c.SharePoint.Library!.Trim()}";
                applied.Add($"{id} / {area}{target}" + (c.SharePoint.Fields is null ? "" : $" ({n} column template{(n == 1 ? "" : "s")})"));
            }
        }
        return new SharePointFieldImportResult(applied, skipped);
    }

    /// <summary>The field map an import sets: trimmed column names, null templates as "", last duplicate wins.</summary>
    public static Dictionary<string, string> CleanFields(IReadOnlyDictionary<string, string> fields)
    {
        var map = new Dictionary<string, string>();
        foreach (var (col, tpl) in fields)
            map[col.Trim()] = tpl ?? "";
        return map;
    }
}
