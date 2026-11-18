using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// The setup assistant's calls (boards W1 to W4): the instance's own identity, and a check that it can read the container
/// F&amp;O writes to (failure case 55). A check reads only; it never stores the address, the connection string or anything else.
/// </summary>
public sealed partial class LocalStatusServer
{
    private const int CompaniesRead = 50;

    /// <summary>How a check reaches a container: account address, container, and an optional connection string.</summary>
    internal Func<string, string, string?, IBlobStore> SourceStores { get; set; } = (accountUrl, container, connectionString) =>
        new AzureBlobStore((string.IsNullOrWhiteSpace(connectionString)
                ? new BlobServiceClient(new Uri(accountUrl), new DefaultAzureCredential())
                : new BlobServiceClient(connectionString))
            .GetBlobContainerClient(container));

    /// <summary>The worker's test runs; null where no worker runs beside the API.</summary>
    internal TestRuns? TestRuns { get; set; }

    /// <summary>How long a test run may take before the answer says to follow the history instead.</summary>
    internal TimeSpan TestRunWait { get; set; } = TimeSpan.FromSeconds(120);

    private sealed class TestRunDto
    {
        public string? DocumentType { get; set; }
        public string? Company { get; set; }
    }

    private async Task PostTestRun(ApiCall call)
    {
        TestRunDto? dto;
        try { dto = JsonSerializer.Deserialize<TestRunDto>(call.Body, JsonOpts); }
        catch (JsonException ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }
        if (string.IsNullOrWhiteSpace(dto?.DocumentType) || string.IsNullOrWhiteSpace(dto.Company))
        { Respond(call, 400, new { ok = false, message = "Name the document type and the company." }); return; }
        if (TestRuns is null)
        { Respond(call, 503, new { ok = false, message = "No worker runs beside this API, so nothing can be tested here." }); return; }
        if (!TestRuns.TryRequest(dto.DocumentType.Trim(), dto.Company.Trim(), out var running))
        { Respond(call, 409, new { ok = false, message = "A test run is already going." }); return; }

        var finished = await Task.WhenAny(running, Task.Delay(TestRunWait));
        if (finished != running)
        {
            Respond(call, 202, new { ok = false, message = "The test run is still going; follow it in the document history." });
            return;
        }
        var result = await running;
        _log.LogInformation("Test run by {User}: {Ok} {Id}.", ForLog(call.User ?? "dashboard"), result.Ok, ForLog(result.DocumentId ?? "-"));
        // The history search matches parts of names (RE-1 also finds RE-10), so only this document's own steps.
        var events = result.DocumentId is null ? [] : _ledger.Events(result.DocumentId, 200)
            .Where(e => string.Equals(e.DocumentId, result.DocumentId, StringComparison.Ordinal))
            .OrderBy(e => e.Id)
            .Select(e => (object)new { at = e.At, step = e.Step, target = e.Target, state = e.State, message = e.Message })
            .ToArray();
        Respond(call, 200, new { ok = result.Ok, documentId = result.DocumentId, message = result.Message, events });
    }

    /// <summary>Start or pause taking documents (failure case 57): the "Paused" setting, nothing else.</summary>
    private void PostPaused(ApiCall call, bool paused)
    {
        try
        {
            _settings.Update(section =>
            {
                bool now = section["Paused"] is JsonValue v && v.TryGetValue<bool>(out var p) && p;
                if (now == paused) return false;
                section["Paused"] = paused;
                return true;
            });
        }
        catch (Exception ex)
        {
            Respond(call, 500, new { ok = false, message = $"The settings could not be written ({_settings.Where}): " + ex.Message });
            return;
        }
        _log.LogInformation("{User} {Action} Hive BDC.", ForLog(call.User ?? "dashboard"), paused ? "paused" : "started");
        Respond(call, 200, new { ok = true, paused });
    }

    private sealed class CheckSourceDto
    {
        public string? AccountUrl { get; set; }
        public string? Container { get; set; }
        public string? Prefix { get; set; }
        public string? ConnectionString { get; set; }
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}$")]
    private static partial Regex ContainerName();

    private (string? ObjectId, string? TenantId) InstanceIdentity()
        => _licenseTenant is InstanceTenant instance && instance.Resolve(out var tenant, out _) ? (instance.ObjectId, tenant) : (null, null);

    private void GetSetupIdentity(ApiCall call)
    {
        var (objectId, tenantId) = InstanceIdentity();
        Respond(call, 200, new
        {
            objectId,
            tenantId,
            message = objectId is null ? "Only an Azure instance has an identity of its own." : "",
        });
    }

    private void PostCheckSource(ApiCall call)
    {
        CheckSourceDto? dto;
        try { dto = JsonSerializer.Deserialize<CheckSourceDto>(call.Body, JsonOpts); }
        catch (JsonException ex) { Respond(call, 400, new { ok = false, message = "Invalid JSON: " + ex.Message }); return; }

        string container = dto?.Container?.Trim() ?? "";
        if (!Uri.TryCreate(dto?.AccountUrl?.Trim(), UriKind.Absolute, out var account) || account.Scheme != Uri.UriSchemeHttps)
        { Respond(call, 400, new { ok = false, message = "Enter the storage account address, for example https://youraccount.blob.core.windows.net." }); return; }
        if (!ContainerName().IsMatch(container))
        { Respond(call, 400, new { ok = false, message = "A container name has 3 to 63 lower-case letters, digits and single hyphens." }); return; }

        string accountUrl = account.GetLeftPart(UriPartial.Authority);
        string accountName = account.Host.Split('.')[0];
        var (objectId, _) = InstanceIdentity();
        string? command = objectId is null ? null
            : $"az role assignment create --assignee-object-id {objectId} --assignee-principal-type ServicePrincipal "
              + $"--role \"Storage Blob Data Contributor\" --scope \"$(az storage account show -n {accountName} --query id -o tsv)\"";

        string prefix = dto?.Prefix?.Trim() ?? "";
        try
        {
            var store = SourceStores(accountUrl, container, dto?.ConnectionString);
            string processed = "processed/" + prefix, error = "error/" + prefix;
            var exports = store.List(prefix)
                .Where(n => !n.StartsWith(processed, StringComparison.Ordinal) && !n.StartsWith(error, StringComparison.Ordinal))
                .Where(n => !n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var byCompany = exports.Take(CompaniesRead)
                .Select(n => CompanyOf(store.Download(n)))
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { company = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count).ThenBy(g => g.company, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string waiting = exports.Count switch
            {
                0 => "No export is waiting yet.",
                1 => "1 export is waiting",
                _ => $"{exports.Count} exports are waiting",
            };
            string detail = byCompany.Length == 0 ? "" : ": " + string.Join(", ", byCompany.Select((c, i) =>
                i == 0 ? $"{c.count} for company {c.company}" : $"{c.count} for {c.company}"));
            string read = exports.Count > CompaniesRead ? $" (companies of the first {CompaniesRead})" : "";
            Respond(call, 200, new
            {
                ok = true,
                message = exports.Count == 0 ? "Access works. " + waiting : $"Access works. {waiting}{detail}{read}.",
                waiting = exports.Count,
                byCompany,
                command,
            });
        }
        catch (RequestFailedException ex)
        {
            string message = ex.Status switch
            {
                403 when string.IsNullOrWhiteSpace(dto?.ConnectionString)
                    => $"This instance has no access to {account.Host} yet. Your IT runs the command below once; then wait a minute and check again.",
                403 => "The connection string was refused by the storage account. Copy it again from the account's Access keys.",
                404 when ex.ErrorCode == "ContainerNotFound" => $"The container '{container}' does not exist in {account.Host}.",
                _ => $"The storage account answered {ex.Status} ({ex.ErrorCode}).",
            };
            _log.LogInformation("Setup check of {Account}/{Container}: {Status} {Code}.", ForLog(account.Host), ForLog(container), ex.Status, ex.ErrorCode);
            Respond(call, 200, new { ok = false, message, waiting = 0, byCompany = Array.Empty<object>(), command });
        }
        catch (Exception ex) when (ex is AggregateException or HttpRequestException or AuthenticationFailedException or FormatException or ArgumentException)
        {
            string message = ex is FormatException or ArgumentException
                ? "The connection string cannot be read. Copy it again from the account's Access keys."
                : $"{account.Host} cannot be reached from this instance. Check the address.";
            _log.LogInformation("Setup check of {Account}/{Container} failed: {Kind}.", ForLog(account.Host), ForLog(container), ex.GetType().Name);
            Respond(call, 200, new { ok = false, message, waiting = 0, byCompany = Array.Empty<object>(), command });
        }
    }

    /// <summary>The company an export names ("dataAreaId" in any case), or "unknown" when it cannot be read.</summary>
    private static string CompanyOf(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var property in doc.RootElement.EnumerateObject())
                    if (string.Equals(property.Name, "dataAreaId", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } company)
                        return company;
        }
        catch (JsonException) { /* not an export */ }
        return "unknown";
    }
}