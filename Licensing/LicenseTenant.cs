using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Balsoft.Hive.BDC.Licensing;

/// <summary>
/// What a licence is checked against: the tenant (blank = not bound, as before) and the instance. A local install has no
/// instance (null); an Azure instance has the object id of its own system identity, or <see cref="UnknownInstance"/>
/// while that cannot be read, so a licence bound to an instance fails closed.
/// </summary>
public sealed record LicenseScope(string? TenantId, string? InstanceId, string? Problem = null)
{
    public const string UnknownInstance = "unknown";

    /// <summary>A local install: the tenant only.</summary>
    public static LicenseScope Local(string? tenantId) => new(tenantId, null);

    public bool IsLocal => InstanceId is null;
}

/// <summary>The tenant, and on Azure the instance, a licence must be issued for on this install.</summary>
public interface ILicenseTenant
{
    string? For(BDCOptions opts);

    LicenseScope Scope(BDCOptions opts) => LicenseScope.Local(For(opts));
}

/// <summary>A local install: the tenant of the SharePoint app in the settings, as before (blank = not bound).</summary>
public sealed class SettingsLicenseTenant : ILicenseTenant
{
    public string? For(BDCOptions opts) => opts.SharePointAuth.TenantId;
}

/// <summary>
/// An Azure instance: the tenant of its own identity (managed identity in Azure, the Azure CLI sign-in on a developer
/// machine), read from the "tid" claim of an access token. The settings cannot change it, so a licence works only in
/// the customer's tenant it was issued for (design check 11). While the tenant is not known, <see cref="For"/> names a
/// tenant no licence has, so a tenant-bound licence fails closed; a failed attempt is repeated at most once a minute.
/// </summary>
public sealed class InstanceTenant : ILicenseTenant
{
    public const string Unknown = "unknown (the tenant of this instance could not be determined)";

    private readonly Func<string> _accessToken;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private string? _tenant;
    private (DateTimeOffset At, string Problem)? _lastFailure;

    public InstanceTenant(Func<string> accessToken, TimeProvider? clock = null)
    {
        _accessToken = accessToken;
        _clock = clock ?? TimeProvider.System;
    }

    public static InstanceTenant FromHostIdentity()
    {
        var credential = new DefaultAzureCredential();
        var request = new TokenRequestContext(new[] { "https://storage.azure.com/.default" });
        return new InstanceTenant(() => credential.GetToken(request, CancellationToken.None).Token);
    }

    public string? For(BDCOptions opts) => Resolve(out var tenant, out _) ? tenant : Unknown;

    /// <summary>
    /// The tenant and the object id of this instance's system identity (design 2026-11-20 licence binding): the id stays
    /// the same through self-updates and restarts and is another one for a copy in any other resource.
    /// </summary>
    public LicenseScope Scope(BDCOptions opts)
        => Resolve(out var tenant, out var problem) && ObjectId is { Length: > 0 } oid
            ? new LicenseScope(tenant, oid)
            : new LicenseScope(tenant ?? Unknown, LicenseScope.UnknownInstance, problem ?? "the access token of this instance names no object id");

    public bool Resolve(out string? tenant, out string? problem)
    {
        lock (_gate)
        {
            if (_tenant is not null)
            {
                (tenant, problem) = (_tenant, null);
                return true;
            }
            var now = _clock.GetUtcNow();
            if (_lastFailure is { } failure && now - failure.At < TimeSpan.FromMinutes(1))
            {
                (tenant, problem) = (null, failure.Problem);
                return false;
            }
            try
            {
                string token = _accessToken();
                _tenant = TenantOfToken(token)
                          ?? throw new InvalidOperationException("the access token of this instance names no tenant");
                ObjectId = ClaimOfToken(token, "oid");
                _lastFailure = null;
                (tenant, problem) = (_tenant, null);
                return true;
            }
            catch (Exception ex)
            {
                _lastFailure = (now, ex.Message);
                (tenant, problem) = (null, ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// The object id of the instance's own identity ("oid" of the same token), known once the tenant is: what a customer's
    /// IT grants a role on a storage account they own (setup assistant, failure case 55).
    /// </summary>
    public string? ObjectId { get; private set; }

    /// <summary>The "tid" claim of a JWT access token (payload only, no signature check: the token is our own).</summary>
    public static string? TenantOfToken(string jwt) => ClaimOfToken(jwt, "tid");

    /// <summary>A GUID claim of a JWT access token, or null.</summary>
    public static string? ClaimOfToken(string jwt, string claim)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(claim, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && Guid.TryParse(value.GetString(), out _)
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}