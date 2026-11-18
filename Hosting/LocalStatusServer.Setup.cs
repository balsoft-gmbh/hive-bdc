using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
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