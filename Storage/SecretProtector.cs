using System.Security.Cryptography;
using System.Text;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>
/// At-rest protection for secrets stored in appsettings.json, using Windows
/// DPAPI with machine scope ("enc:" + base64 blob). The blob only decrypts on
/// the machine that produced it — the installer encrypts on the install host,
/// which is by definition the host the service runs on. Plaintext values are
/// still accepted at runtime (hand-edited configs) so post-setup configuration
/// stays a plain-text-editor job.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:";

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows() || plaintext.Length == 0) return plaintext;
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Decrypt an "enc:" value; a plaintext value is returned unchanged.</summary>
    public static string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI-protected secret found on a non-Windows host.");
        var blob = Convert.FromBase64String(value[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine));
    }
}
