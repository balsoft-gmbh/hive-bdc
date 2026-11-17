using System.Text;

namespace Balsoft.Hive.BDC.Licensing;

/// <summary>
/// Where the licence file lives and how a new one is put there: a file on a local install, a blob next to the settings
/// in Azure. Both check a licence before storing it, so a tampered, foreign or expired one never replaces a working one.
/// </summary>
public interface ILicenseStore
{
    string Where(BDCOptions opts);

    LicenseResult Check(BDCOptions opts, string? tenant, DateOnly today);

    /// <summary>Checks <paramref name="content"/> and stores it unless it is Invalid or Expired; Stored says which.</summary>
    (LicenseResult Result, bool Stored) Install(BDCOptions opts, string content, string? tenant, DateOnly today);
}

public delegate LicenseResult LicenseCheck(string? content, string? tenant, DateOnly today);

/// <summary>The local rule: License:File, else license.lic next to the exe, else %ProgramData%\HiveBDC\license.lic.</summary>
public sealed class FileLicenseStore(LicenseCheck? check = null) : ILicenseStore
{
    private readonly LicenseCheck _check = check ?? LicenseValidator.ValidateContent;

    public string Where(BDCOptions opts) => LicenseValidator.DefaultInstallPath(opts.License.File);

    public LicenseResult Check(BDCOptions opts, string? tenant, DateOnly today) => LicenseValidator.Validate(opts.License.File, tenant, today);

    public (LicenseResult Result, bool Stored) Install(BDCOptions opts, string content, string? tenant, DateOnly today)
    {
        var result = _check(content, tenant, today);
        if (result.State is LicenseState.Invalid or LicenseState.Expired) return (result, false);
        string target = Where(opts);
        string dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $".license.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, content.Trim());
        try { File.Move(tmp, target, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { /* best effort */ } throw; }
        return (result, true);
    }
}

/// <summary>
/// The Azure rule: license.lic next to the settings blob. A check reads the blob only when its ETag, the tenant or the day
/// changed; when the blob cannot be read, the last result stands.
/// </summary>
public sealed class BlobLicenseStore(IETagBlob blob, LicenseCheck? check = null) : ILicenseStore
{
    private const int Attempts = 3;
    private readonly LicenseCheck _check = check ?? LicenseValidator.ValidateContent;
    private readonly object _gate = new();
    private (string ETag, string? Tenant, DateOnly Day, LicenseResult Result)? _last;

    public string Where(BDCOptions opts) => blob.Where;

    public LicenseResult Check(BDCOptions opts, string? tenant, DateOnly today)
    {
        lock (_gate)
        {
            try
            {
                string? etag = blob.CurrentETag();
                if (etag is null)
                    return new LicenseResult { State = LicenseState.Missing, Message = "No valid license installed — processing is stopped. Upload the license file Balsoft sent you." };
                if (_last is { } last && last.ETag == etag && last.Tenant == tenant && last.Day == today) return last.Result;
                var stored = blob.Read();
                if (stored is null)
                    return new LicenseResult { State = LicenseState.Missing, Message = "No valid license installed — processing is stopped. Upload the license file Balsoft sent you." };
                var result = _check(Encoding.UTF8.GetString(stored.Value.Content), tenant, today);
                _last = (stored.Value.ETag, tenant, today, result);
                return result;
            }
            catch (Exception ex)
            {
                return _last is { } last && last.Tenant == tenant && last.Day == today
                    ? last.Result
                    : new LicenseResult { State = LicenseState.Invalid, Message = $"The license in {blob.Where} could not be read: {ex.Message}" };
            }
        }
    }

    public (LicenseResult Result, bool Stored) Install(BDCOptions opts, string content, string? tenant, DateOnly today)
    {
        var result = _check(content, tenant, today);
        if (result.State is LicenseState.Invalid or LicenseState.Expired) return (result, false);
        byte[] bytes = Encoding.UTF8.GetBytes(content.Trim());
        for (int attempt = 0; attempt < Attempts; attempt++)
            if (blob.TryWrite(bytes, blob.CurrentETag()))
                return (result, true);
        throw new InvalidOperationException($"{blob.Where} was changed by someone else {Attempts} times in a row; the license was not stored.");
    }
}