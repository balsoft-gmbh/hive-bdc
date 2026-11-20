using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Licensing;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>A licence bound to one Azure instance (design 2026-11-20 licence binding, failure cases 1 to 11).</summary>
public sealed class LicenseBindingTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Instance = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string OtherInstance = "bbbbbbbb-0000-4000-8000-000000000002";
    private static readonly DateOnly Today = new(2026, 11, 20);
    private readonly RSA _key = RSA.Create(2048);

    public void Dispose() => _key.Dispose();

    private string Licence(string product = LicenseValidator.BoundProductName, string? instance = Instance, string tenant = Tenant)
    {
        var payload = new Dictionary<string, object?>
        {
            ["product"] = product, ["customer"] = "Contoso GmbH", ["customerCode"] = "CONTOSO", ["tenantId"] = tenant,
            ["issued"] = "2026-11-20", ["expires"] = "2027-11-20", ["features"] = new[] { "einvoice", "documents", "sharepoint" },
        };
        if (instance is not null) payload["instanceId"] = instance;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return B64(bytes) + "." + B64(_key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private LicenseResult Check(string licence, LicenseScope scope) => LicenseValidator.ValidateContent(licence, scope, Today, _key);

    private static LicenseScope Azure(string instance) => new(Tenant, instance);

    [Fact]
    public void ALicenceBoundToThisInstanceRuns()
    {
        var result = Check(Licence(), Azure(Instance));

        Assert.Equal(LicenseState.Valid, result.State);
        Assert.Equal(Instance, result.Info!.InstanceId);
    }

    [Fact]
    public void ALicenceBoundToAnotherInstanceIsRefusedNamingBoth()
    {
        var result = Check(Licence(), Azure(OtherInstance));

        Assert.Equal(LicenseState.Invalid, result.State);
        Assert.Equal($"This licence belongs to another Hive BDC instance ({Instance}). This instance is {OtherInstance}. Ask Balsoft for a licence issued for it.", result.Message);
        Assert.True(result.Blocked);
    }

    [Fact]
    public void ABoundLicenceDoesNotRunOnALocalInstall()
    {
        foreach (var tenant in new[] { Tenant, "", null })
        {
            var result = Check(Licence(), LicenseScope.Local(tenant));
            Assert.Equal(LicenseState.Invalid, result.State);
            Assert.Equal($"This licence belongs to the Hive BDC instance {Instance} in Azure and does not run on a local install. Ask Balsoft for a licence for this install.", result.Message);
        }
    }

    [Fact]
    public void AnUnboundLicenceBehavesAsBefore()
    {
        string unbound = Licence(LicenseValidator.ProductName, instance: null);

        Assert.Equal(LicenseState.Valid, Check(unbound, LicenseScope.Local(Tenant)).State);
        Assert.Equal(LicenseState.Valid, Check(unbound, LicenseScope.Local("")).State);
        Assert.Equal(LicenseState.Valid, Check(unbound, Azure(Instance)).State);
        Assert.Equal(LicenseState.Valid, Check(unbound, Azure(OtherInstance)).State);
        Assert.Equal(LicenseState.Invalid, Check(unbound, new LicenseScope("99999999-9999-9999-9999-999999999999", Instance)).State);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("remove")]
    [InlineData("product")]
    public void AnEditedBindingFailsTheSignature(string edit)
    {
        var parts = Licence().Split('.');
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0].Replace('-', '+').Replace('_', '/').PadRight((parts[0].Length + 3) / 4 * 4, '=')));
        json = edit switch
        {
            "instance" => json.Replace(Instance, OtherInstance),
            "remove" => json.Replace($",\"instanceId\":\"{Instance}\"", ""),
            _ => json.Replace(LicenseValidator.BoundProductName, LicenseValidator.ProductName),
        };
        string tampered = B64(Encoding.UTF8.GetBytes(json)) + "." + parts[1];

        var result = Check(tampered, Azure(OtherInstance));

        Assert.Equal(LicenseState.Invalid, result.State);
        Assert.StartsWith("License signature is invalid", result.Message);
    }

    [Fact]
    public void TheBoundProductMustNameItsInstance()
    {
        var result = Check(Licence(instance: null), Azure(Instance));

        Assert.Equal(LicenseState.Invalid, result.State);
        Assert.Equal("This licence is for one Hive BDC instance in Azure but names no instance. Ask Balsoft for a new licence file.", result.Message);
    }

    [Fact]
    public void ABindingOnTheOldProductNameIsStillEnforced()
    {
        string handMade = Licence(LicenseValidator.ProductName, Instance);

        Assert.Equal(LicenseState.Valid, Check(handMade, Azure(Instance)).State);
        Assert.Equal(LicenseState.Invalid, Check(handMade, Azure(OtherInstance)).State);
        Assert.Equal(LicenseState.Invalid, Check(handMade, LicenseScope.Local(Tenant)).State);
    }

    [Fact]
    public void VersionsBeforeTheBindingRefuseABoundLicenceByItsProduct()
    {
        // 1.10.1 and older accept only the product "HiveBDC" and ignore unknown fields, so the product name keeps them out.
        Assert.NotEqual(LicenseValidator.ProductName, LicenseValidator.BoundProductName, StringComparer.OrdinalIgnoreCase);
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(PadB64(Licence().Split('.')[0]))));
        Assert.Equal(LicenseValidator.BoundProductName, payload.RootElement.GetProperty("product").GetString());
    }

    [Fact]
    public void AnInstanceWhoseIdentityCannotBeReadFailsClosed()
    {
        var result = Check(Licence(), new LicenseScope(Tenant, LicenseScope.UnknownInstance, "IMDS did not answer"));

        Assert.Equal(LicenseState.Invalid, result.State);
        Assert.Equal($"This licence belongs to instance {Instance}; the identity of this instance cannot be read yet (IMDS did not answer).", result.Message);
    }

    [Fact]
    public void TheBindingIgnoresCaseAndSpaces()
        => Assert.Equal(LicenseState.Valid, Check(Licence(instance: " " + Instance.ToUpperInvariant() + " "), Azure(Instance)).State);

    [Fact]
    public void TheSameInstanceKeepsItsIdThroughARestartAndACopyGetsAnother()
    {
        string Jwt(string oid) => $"e30.{B64(Encoding.UTF8.GetBytes($$"""{"tid":"{{Tenant}}","oid":"{{oid}}"}"""))}.sig";
        var before = new InstanceTenant(() => Jwt(Instance)).Scope(new BDCOptions());
        var afterRestart = new InstanceTenant(() => Jwt(Instance)).Scope(new BDCOptions());
        var copy = new InstanceTenant(() => Jwt(OtherInstance)).Scope(new BDCOptions());

        Assert.Equal(before, afterRestart);
        Assert.Equal(LicenseState.Valid, Check(Licence(), afterRestart).State);
        Assert.Equal(LicenseState.Invalid, Check(Licence(), copy).State);
        var failing = new InstanceTenant(() => throw new InvalidOperationException("no identity endpoint")).Scope(new BDCOptions());
        Assert.Equal(LicenseScope.UnknownInstance, failing.InstanceId);
        Assert.Equal(LicenseState.Invalid, Check(Licence(), failing).State);
        Assert.True(((ILicenseTenant)new SettingsLicenseTenant()).Scope(new BDCOptions()).IsLocal);
    }

    [Fact]
    public void AnUploadedLicenceOfAnotherInstanceIsNotStored()
    {
        var blob = new MemoryETagBlob();
        var store = new BlobLicenseStore(blob, (content, scope, today) => LicenseValidator.ValidateContent(content, scope, today, _key));
        Assert.True(store.Install(new BDCOptions(), Licence(), Azure(Instance), Today).Stored);

        string other = Licence(instance: OtherInstance);
        var (result, stored) = store.Install(new BDCOptions(), other, Azure(Instance), Today);

        Assert.False(stored);
        Assert.Contains("belongs to another Hive BDC instance", result.Message);
        Assert.Equal(LicenseState.Valid, store.Check(new BDCOptions(), Azure(Instance), Today).State);
    }

    private static string PadB64(string s) => s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '=');
}