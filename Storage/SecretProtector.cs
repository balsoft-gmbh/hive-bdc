using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>
/// At-rest protection for secrets stored in the settings. Local installs use Windows DPAPI with machine scope
/// ("enc:" + base64 blob, decrypts only on the machine that produced it). The Azure run mode keeps every secret in Key
/// Vault and the settings hold "kv:" + the secret name (<see cref="UseVault"/>). Plaintext values are still accepted at
/// runtime (hand-edited configs) so post-setup configuration stays a plain-text-editor job.
/// </summary>
public static class SecretProtector
{
    public static SecretResolver Current { get; private set; } = new(null);

    /// <summary>Called once at start by the Azure run mode; from then on secrets are written to and read from the vault.</summary>
    public static void UseVault(ISecretVault vault) => Current = new SecretResolver(vault);

    public static bool IsProtected(string value) => SecretResolver.IsProtected(value);

    public static string Protect(string plaintext, string? vaultName = null) => Current.Protect(plaintext, vaultName);

    /// <summary>The plain secret of an "enc:" or "kv:" value; a plaintext value is returned unchanged.</summary>
    public static string Unprotect(string value) => Current.Unprotect(value);
}

/// <summary>
/// What <see cref="SecretProtector"/> does, as an instance, so a vault and a clock can be given. Vault values are kept
/// for <c>cacheFor</c> (default five minutes) so a rotated secret is picked up without a restart; when the vault cannot be
/// reached, the last value read is used, and without one the call fails with the secret's name (never its value), so
/// the document waits and is tried again.
/// </summary>
public sealed partial class SecretResolver
{
    private const string LocalPrefix = "enc:";
    private const string VaultPrefix = "kv:";

    private readonly ISecretVault? _vault;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _cacheFor;
    private readonly Dictionary<string, (string Value, DateTimeOffset ReadAt)> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SecretResolver(ISecretVault? vault, TimeProvider? clock = null, TimeSpan? cacheFor = null)
    {
        _vault = vault;
        _clock = clock ?? TimeProvider.System;
        _cacheFor = cacheFor ?? TimeSpan.FromMinutes(5);
    }

    public bool UsesVault => _vault is not null;

    public static bool IsProtected(string value)
        => value.StartsWith(LocalPrefix, StringComparison.Ordinal) || value.StartsWith(VaultPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Local: the DPAPI value (plain text off Windows). With a vault: the secret is written under
    /// <paramref name="vaultName"/> and "kv:" + that name comes back; without a name it is refused, because in Azure no
    /// secret may land in the settings.
    /// </summary>
    public string Protect(string plaintext, string? vaultName = null)
    {
        if (plaintext.Length == 0) return plaintext;
        if (_vault is null)
        {
            if (!OperatingSystem.IsWindows()) return plaintext;
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.LocalMachine);
            return LocalPrefix + Convert.ToBase64String(blob);
        }
        if (string.IsNullOrWhiteSpace(vaultName))
            throw new InvalidOperationException("This secret has no Key Vault name; in Azure every secret is kept in Key Vault, never in the settings.");
        string name = VaultName(vaultName);
        _vault.Set(name, plaintext);
        lock (_gate) _cache[name] = (plaintext, _clock.GetUtcNow());
        return VaultPrefix + name;
    }

    public string Unprotect(string value)
    {
        if (value.StartsWith(VaultPrefix, StringComparison.Ordinal)) return FromVault(value[VaultPrefix.Length..]);
        if (!value.StartsWith(LocalPrefix, StringComparison.Ordinal)) return value;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI-protected secret found on a non-Windows host.");
        var blob = Convert.FromBase64String(value[LocalPrefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine));
    }

    /// <summary>A valid Key Vault secret name (letters, digits and dashes, at most 127) from any text, lower case.</summary>
    public static string VaultName(string text)
    {
        string name = InvalidNameCharacters().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        if (name.Length == 0) throw new ArgumentException("A Key Vault secret name needs at least one letter or digit.", nameof(text));
        return name.Length <= 127 ? name : name[..127].TrimEnd('-');
    }

    private string FromVault(string name)
    {
        if (_vault is null)
            throw new InvalidOperationException($"The setting refers to Key Vault secret '{name}', but no Key Vault is configured for this instance.");
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(name, out var cached) && now - cached.ReadAt < _cacheFor) return cached.Value;
        }
        string? value;
        try
        {
            value = _vault.Get(name);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(name, out var stale)) return stale.Value;
            }
            throw new InvalidOperationException($"Key Vault secret '{name}' could not be read: {ex.Message}", ex);
        }
        if (value is null) throw new InvalidOperationException($"Key Vault secret '{name}' does not exist.");
        lock (_gate) _cache[name] = (value, now);
        return value;
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex InvalidNameCharacters();
}