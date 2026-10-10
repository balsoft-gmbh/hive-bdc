using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Balsoft.Hive.BDC.Licensing;

/// <summary>Licensing config ("HiveBDC:License" section).</summary>
public sealed class LicenseOptions
{
    /// <summary>Path to the license file. Blank = look next to the exe (license.lic), then %ProgramData%\HiveBDC\license.lic.</summary>
    public string File { get; set; } = "";
}

public enum LicenseState { Valid, Warning, Grace, Expired, Missing, Invalid }

/// <summary>Signed license payload (the exact JSON bytes are what the signature covers).</summary>
public sealed class LicenseInfo
{
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("customer")] public string Customer { get; set; } = "";
    [JsonPropertyName("customerCode")] public string CustomerCode { get; set; } = "";
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = "";
    [JsonPropertyName("issued")] public string Issued { get; set; } = "";
    [JsonPropertyName("expires")] public string Expires { get; set; } = "";
    [JsonPropertyName("features")] public List<string> Features { get; set; } = new();
}

/// <summary>
/// The feature flags a license may carry. A handler names the one it needs; a license
/// without it HOLDS that document type in the input folder rather than failing it.
/// </summary>
public static class LicenseFeatures
{
    /// <summary>XRechnung / ZUGFeRD generation (the Hive eInvoice module).</summary>
    public const string EInvoice = "einvoice";

    /// <summary>PDF pass-through dispatch of every other document type.</summary>
    public const string Documents = "documents";

    /// <summary>SharePoint upload (informational today; upload is governed by the settings).</summary>
    public const string SharePoint = "sharepoint";

    public static readonly IReadOnlyList<string> All = new[] { EInvoice, Documents, SharePoint };
}

public sealed class LicenseResult
{
    public required LicenseState State { get; init; }
    public LicenseInfo? Info { get; init; }
    public string Message { get; init; } = "";
    public DateOnly? ExpiresOn { get; init; }

    /// <summary>True when the license is usable (Valid/Warning/Grace) AND lists <paramref name="feature"/>.</summary>
    public bool HasFeature(string feature)
        => !Blocked && Info is not null
           && Info.Features.Any(f => string.Equals(f?.Trim(), feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for states that mean "not a currently valid license" (invalid/tampered, expired past grace).</summary>
    public bool IsFatal => State is LicenseState.Invalid or LicenseState.Expired;

    /// <summary>Whether processing must be blocked. A valid license is ALWAYS required, so a
    /// MISSING license blocks too; only Valid/Warning/Grace keep converting.</summary>
    public bool Blocked => IsFatal || State == LicenseState.Missing;
}

/// <summary>
/// Verifies a signed, time-limited license against the embedded Balsoft public
/// certificate (same cert that signs the binaries). Offline, no phone-home.
/// License file format: {base64url(payloadJson)}.{base64url(RSA-PKCS1-SHA256 signature)}.
/// Bound to the customer's M365 tenant id; grace-and-warn expiry policy.
/// </summary>
public static class LicenseValidator
{
    public const string ProductName = "HiveBDC";
    public const int WarnWithinDays = 30;   // warn this many days BEFORE expiry
    public const int GraceDays = 7;         // keep running this many days AFTER expiry

    // The Worker checks the licence every poll and the dashboard's /status every few seconds.
    // An RSA verification plus a file read each time is wasted work: the result only changes
    // when the file changes (renewal), the expected tenant changes, or the day rolls over.
    private static readonly object CacheGate = new();
    private static (string Path, DateTime Written, long Length, string? Tenant, DateOnly Day, LicenseResult Result)? _cache;

    public static LicenseResult Validate(string? configuredPath, string? expectedTenantId, DateOnly today)
    {
        string? path = ResolvePath(configuredPath);
        if (path is null || !System.IO.File.Exists(path))
            return new LicenseResult { State = LicenseState.Missing, Message = "No valid license installed — processing is stopped. Upload the license file Balsoft sent you." };

        var info = new FileInfo(path);
        lock (CacheGate)
        {
            if (_cache is { } c && c.Path == path && c.Written == info.LastWriteTimeUtc && c.Length == info.Length
                && c.Tenant == expectedTenantId && c.Day == today)
                return c.Result;
        }

        string raw;
        try { raw = System.IO.File.ReadAllText(path).Trim(); }
        catch (Exception ex) { return new LicenseResult { State = LicenseState.Invalid, Message = "License file could not be read: " + ex.Message }; }
        var result = ValidateContent(raw, expectedTenantId, today);
        lock (CacheGate) _cache = (path, info.LastWriteTimeUtc, info.Length, expectedTenantId, today, result);
        return result;
    }

    /// <summary>
    /// Validate license TEXT (the file's contents) rather than a path. Used by the dashboard's
    /// upload/renew flow to pre-check a .lic before installing it. Same checks as <see cref="Validate"/>
    /// minus the Missing case (there is always content here); empty text is Invalid.
    /// </summary>
    public static LicenseResult ValidateContent(string? content, string? expectedTenantId, DateOnly today)
    {
        string raw = content?.Trim() ?? "";
        if (raw.Length == 0)
            return new LicenseResult { State = LicenseState.Invalid, Message = "License file is empty." };

        var parts = raw.Split('.');
        if (parts.Length != 2)
            return new LicenseResult { State = LicenseState.Invalid, Message = "License file is malformed (expected payload.signature)." };

        byte[] payloadBytes, signature;
        try { payloadBytes = Base64UrlDecode(parts[0]); signature = Base64UrlDecode(parts[1]); }
        catch { return new LicenseResult { State = LicenseState.Invalid, Message = "License file is not valid base64url." }; }

        bool signatureOk;
        try
        {
            using var rsa = LoadTrustedPublicKey();
            signatureOk = rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            return new LicenseResult { State = LicenseState.Invalid, Message = "Could not verify the license signature: " + ex.Message };
        }
        if (!signatureOk)
            return new LicenseResult { State = LicenseState.Invalid, Message = "License signature is invalid — the file was tampered with or not issued by Balsoft." };

        LicenseInfo? info;
        try { info = JsonSerializer.Deserialize<LicenseInfo>(payloadBytes); }
        catch { return new LicenseResult { State = LicenseState.Invalid, Message = "License payload could not be read." }; }
        if (info is null || !string.Equals(info.Product, ProductName, StringComparison.OrdinalIgnoreCase))
            return new LicenseResult
            {
                State = LicenseState.Invalid, Info = info,
                Message = info?.Product is { } p && (p.Equals("HiveDocFlow", StringComparison.OrdinalIgnoreCase)
                                                     || p.Equals("HiveEInvoice", StringComparison.OrdinalIgnoreCase))
                    ? "This license is for an earlier Balsoft product. Hive BDC needs a new license file from Balsoft."
                    : "License is not for this product.",
            };

        if (!string.IsNullOrWhiteSpace(info.TenantId) && !string.IsNullOrWhiteSpace(expectedTenantId)
            && !string.Equals(info.TenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseResult
            {
                State = LicenseState.Invalid,
                Info = info,
                Message = $"License is bound to tenant {info.TenantId}, but this install is configured for {expectedTenantId}.",
            };
        }

        if (!DateOnly.TryParse(info.Expires, out var expires))
            return new LicenseResult { State = LicenseState.Invalid, Info = info, Message = "License has no valid expiry date." };

        int daysToExpiry = expires.DayNumber - today.DayNumber;
        if (daysToExpiry >= 0)
        {
            return daysToExpiry <= WarnWithinDays
                ? new LicenseResult { State = LicenseState.Warning, Info = info, ExpiresOn = expires,
                    Message = $"License for {info.Customer} expires in {daysToExpiry} day(s) on {expires:yyyy-MM-dd} — renew soon." }
                : new LicenseResult { State = LicenseState.Valid, Info = info, ExpiresOn = expires,
                    Message = $"Licensed to {info.Customer}, valid until {expires:yyyy-MM-dd} (features: {DescribeFeatures(info)})." };
        }

        int daysPast = -daysToExpiry;
        return daysPast <= GraceDays
            ? new LicenseResult { State = LicenseState.Grace, Info = info, ExpiresOn = expires,
                Message = $"License for {info.Customer} EXPIRED on {expires:yyyy-MM-dd}; grace period ends in {GraceDays - daysPast} day(s). Renew now." }
            : new LicenseResult { State = LicenseState.Expired, Info = info, ExpiresOn = expires,
                Message = $"License for {info.Customer} expired on {expires:yyyy-MM-dd} and the {GraceDays}-day grace period is over — processing is stopped. Renew the license." };
    }

    /// <summary>Comma list of the license's features, or "none".</summary>
    public static string DescribeFeatures(LicenseInfo? info)
    {
        var f = info?.Features?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).ToList();
        return f is { Count: > 0 } ? string.Join(", ", f) : "none";
    }

    /// <summary>Where an uploaded license should be written so <see cref="Validate"/> reads it next:
    /// the configured path if set, else %ProgramData%\HiveBDC\license.lic.</summary>
    public static string DefaultInstallPath(string? configuredPath)
        => !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HiveBDC", "license.lic");

    /// <summary>Public key from the embedded Balsoft code-signing certificate.</summary>
    private static RSA LoadTrustedPublicKey()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("Balsoft.Hive.BDC.BalsoftGmbH.cer")
            ?? throw new InvalidOperationException("Embedded Balsoft certificate not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        using var cert = X509CertificateLoader.LoadCertificate(ms.ToArray());
        return cert.GetRSAPublicKey() ?? throw new InvalidOperationException("Embedded certificate has no RSA public key.");
    }

    private static string? ResolvePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string local = Path.Combine(AppContext.BaseDirectory, "license.lic");
        if (System.IO.File.Exists(local)) return local;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HiveBDC", "license.lic");
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>Logs a license result at the appropriate level for the grace-and-warn policy.</summary>
public static class LicenseLogging
{
    public static void Report(ILogger log, LicenseResult r)
    {
        switch (r.State)
        {
            case LicenseState.Valid:
                log.LogInformation("License OK — {Message}", r.Message);
                break;
            case LicenseState.Warning:
            case LicenseState.Grace:
                log.LogWarning("License — {Message}", r.Message);
                break;
            default: // Missing, Expired, Invalid — a valid license is always required
                log.LogCritical("License — {Message}", r.Message);
                break;
        }
    }
}
