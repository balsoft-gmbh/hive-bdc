using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Connectors;

/// <summary>
/// Stores the document in a DocuWare file cabinet with index values from the field templates. Settings: FileCabinet (id)
/// and, optionally, MatchField: the cabinet field that holds the document number, used to find a copy stored earlier
/// when the ledger does not know it. Table fields are filled from an array of the document:
/// <c>ARTICLES.QUANTITY = {lines.quantity}</c> gives one row per element of <c>lines</c>.
/// </summary>
public sealed partial class DocuWareDocumentTarget(BDCOptions options, Func<ConnectionOptions, DocuWareClient> clients) : IDocumentTarget
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\.")]
    private static partial Regex ArrayPrefix();

    public string Kind => ConnectionKinds.DocuWare;

    public bool ReplacesOnRepeat => false;

    public string? RequiredFeature => LicenseFeatures.SharePoint;

    public string? Unavailable(TargetOptions target)
    {
        var connection = options.Connection(target.Connection);
        if (connection is null)
            return $"The DocuWare target uses the connection '{target.Connection}', which is not set up. Add it in Settings > Connections.";
        var problems = connection.Problems();
        if (problems.Count > 0) return string.Join(" ", problems);
        if (target.Setting("FileCabinet").Length == 0)
            return "The DocuWare target has no file cabinet. Choose one in Settings > Outbound > the document type > the company > Goes to.";
        return null;
    }

    public DeliveryResult Deliver(DeliveryContext ctx)
    {
        var connection = options.Connection(ctx.Target.Connection)!;
        var client = clients(connection);
        string cabinetId = ctx.Target.Setting("FileCabinet");
        var cabinet = client.CabinetAsync(cabinetId).GetAwaiter().GetResult();

        var single = ctx.Target.Fields.Where(f => !f.Key.Contains('.')).ToDictionary(f => f.Key, f => f.Value);
        var values = SharePointFieldTemplate.Resolve(ctx.Document, single, ctx.Warn);
        var tables = TableRows(ctx.Document, ctx.Target.Fields, ctx.Warn);
        var problems = new List<string>();
        var fields = DocuWareFieldValues.Build(cabinet, values, tables, problems);
        if (problems.Count > 0)
            throw new InvalidOperationException($"DocuWare file cabinet \"{cabinet.Name}\": " + string.Join(" ", problems) +
                " Correct the fields of the DocuWare destination in Settings > Outbound.");

        string? existing = ctx.PreviousRemoteId;
        string matchField = ctx.Target.Setting("MatchField");
        if (existing is null && matchField.Length > 0 && values.TryGetValue(matchField, out var matchValue))
            existing = client.FindAsync(cabinetId, matchField, matchValue).GetAwaiter().GetResult();

        if (existing is not null)
        {
            client.UpdateFieldsAsync(cabinetId, existing, fields).GetAwaiter().GetResult();
            ctx.Log.LogInformation("[docuware] {Id} -> {Cabinet}: document {DocId} already there, {Fields} field(s) updated",
                ctx.DocumentId, cabinet.Name, existing, fields.Count);
            return new DeliveryResult(cabinet.Name, existing,
                $"Already in file cabinet \"{cabinet.Name}\" as document {existing}; {fields.Count} field(s) updated, no second copy stored.");
        }

        string stored = client.StoreAsync(cabinetId, fields, ctx.Output).GetAwaiter().GetResult();
        ctx.Log.LogInformation("[docuware] {Id} -> {Cabinet}: stored {File} as document {DocId}, {Fields} field(s)",
            ctx.DocumentId, cabinet.Name, ctx.Output.Name, stored, fields.Count);
        return new DeliveryResult(cabinet.Name, stored,
            $"Stored {ctx.Output.Name} in file cabinet \"{cabinet.Name}\" as document {stored}, {fields.Count} field(s) set.");
    }

    /// <summary>Rows for every table field: templates "TABLE.COLUMN" = "{array.property}", one row per element of the array.</summary>
    internal static Dictionary<string, List<Dictionary<string, string>>> TableRows(JsonElement document,
        IReadOnlyDictionary<string, string> templates, Action<string>? warn)
    {
        var result = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in templates.Where(t => t.Key.Contains('.')).GroupBy(t => t.Key[..t.Key.IndexOf('.')], StringComparer.OrdinalIgnoreCase))
        {
            string? arrayName = null;
            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, template) in table)
            {
                var prefix = ArrayPrefix().Match(template);
                if (prefix.Success) arrayName ??= prefix.Groups[1].Value;
                columns[key[(key.IndexOf('.') + 1)..]] = prefix.Success ? template.Replace("{" + prefix.Groups[1].Value + ".", "{") : template;
            }
            var rows = new List<Dictionary<string, string>>();
            if (arrayName is not null && TryGetArray(document, arrayName, out var array))
                foreach (var element in array.EnumerateArray())
                {
                    var row = SharePointFieldTemplate.Resolve(element, columns, warn);
                    if (row.Count > 0) rows.Add(row);
                }
            else
                warn?.Invoke($"DocuWare table {table.Key}: the document has no list '{arrayName ?? "(none named)"}' to fill rows from.");
            result[table.Key] = rows;
        }
        return result;
    }

    private static bool TryGetArray(JsonElement document, string name, out JsonElement array)
    {
        array = default;
        if (document.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in document.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
            { array = p.Value; return true; }
        return false;
    }
}