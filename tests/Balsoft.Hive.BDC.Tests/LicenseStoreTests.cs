using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>The licence an Azure user uploads lives next to the settings blob; a local one stays a file.</summary>
public sealed class LicenseStoreTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 17);
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hivebdc-license-" + Guid.NewGuid());
    private readonly MemoryETagBlob _blob = new();
    private readonly List<(string? Content, string? Tenant)> _checked = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for the signature check: "good" licenses this tenant, "foreign" another one, anything else is tampered.</summary>
    private LicenseResult Check(string? content, string? tenant, DateOnly today)
    {
        _checked.Add((content, tenant));
        return content?.Trim() switch
        {
            "good" => new LicenseResult { State = LicenseState.Valid, Message = "License OK", Info = new LicenseInfo { TenantId = Tenant } },
            "foreign" => new LicenseResult { State = LicenseState.Invalid, Message = "License is bound to tenant 99999999-9999-9999-9999-999999999999" },
            "old" => new LicenseResult { State = LicenseState.Expired, Message = "License expired" },
            _ => new LicenseResult { State = LicenseState.Invalid, Message = "License format is not valid" },
        };
    }

    [Fact]
    public void AnUploadedLicenceIsStoredInTheBlobAndReadFromThere()
    {
        var store = new BlobLicenseStore(_blob, Check);

        var (result, stored) = store.Install(new BDCOptions(), "good\n", Tenant, Today);

        Assert.True(stored);
        Assert.Equal(LicenseState.Valid, result.State);
        Assert.Equal("good", _blob.Text);
        Assert.Equal(LicenseState.Valid, store.Check(new BDCOptions(), Tenant, Today).State);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("old")]
    [InlineData("tampered")]
    public void AForeignExpiredOrTamperedLicenceNeverReplacesAWorkingOne(string upload)
    {
        _blob.Put("good");
        var store = new BlobLicenseStore(_blob, Check);

        var (result, stored) = store.Install(new BDCOptions(), upload, Tenant, Today);

        Assert.False(stored);
        Assert.NotEqual(LicenseState.Valid, result.State);
        Assert.Equal("good", _blob.Text);
        Assert.Equal(0, _blob.Writes);
    }

    [Fact]
    public void TheUploadIsCheckedAgainstTheTenantGiven()
    {
        new BlobLicenseStore(_blob, Check).Install(new BDCOptions(), "good", Tenant, Today);

        Assert.Equal((string?)Tenant, Assert.Single(_checked).Tenant);
    }

    [Fact]
    public void NoBlobMeansNoLicence()
        => Assert.Equal(LicenseState.Missing, new BlobLicenseStore(_blob, Check).Check(new BDCOptions(), Tenant, Today).State);

    [Fact]
    public void TheBlobIsReadAgainOnlyWhenItTheTenantOrTheDayChanged()
    {
        _blob.Put("good");
        var store = new BlobLicenseStore(_blob, Check);

        store.Check(new BDCOptions(), Tenant, Today);
        store.Check(new BDCOptions(), Tenant, Today);
        Assert.Equal(1, _blob.Reads);

        store.Check(new BDCOptions(), Tenant, Today.AddDays(1));
        store.Check(new BDCOptions(), "other", Today.AddDays(1));
        _blob.Put("old");
        Assert.Equal(LicenseState.Expired, store.Check(new BDCOptions(), "other", Today.AddDays(1)).State);
        Assert.Equal(4, _blob.Reads);
    }

    [Fact]
    public void AnUnreachableBlobKeepsTheLastResultAndSaysWhyWithoutOne()
    {
        _blob.Put("good");
        var store = new BlobLicenseStore(_blob, Check);
        store.Check(new BDCOptions(), Tenant, Today);

        _blob.Down = true;
        Assert.Equal(LicenseState.Valid, store.Check(new BDCOptions(), Tenant, Today).State);

        var fresh = new BlobLicenseStore(_blob, Check).Check(new BDCOptions(), Tenant, Today);
        Assert.Equal(LicenseState.Invalid, fresh.State);
        Assert.Contains("could not be read: blob 503", fresh.Message);
    }

    [Fact]
    public void AnotherWriterInBetweenIsOverwrittenByTheCheckedLicence()
    {
        _blob.Put("old");
        _blob.BeforeWrite = b => b.Put("tampered");
        var store = new BlobLicenseStore(_blob, Check);

        Assert.True(store.Install(new BDCOptions(), "good", Tenant, Today).Stored);
        Assert.Equal("good", _blob.Text);
    }

    [Fact]
    public void LocallyTheLicenceIsAFileAtLicenseFileAndAForeignOneIsNotWritten()
    {
        string file = Path.Combine(_root, "license.lic");
        var opts = new BDCOptions { License = { File = file } };
        var store = new FileLicenseStore(Check);

        Assert.False(store.Install(opts, "foreign", Tenant, Today).Stored);
        Assert.False(File.Exists(file));

        Assert.True(store.Install(opts, "good\r\n", Tenant, Today).Stored);
        Assert.Equal("good", File.ReadAllText(file));
        Assert.Equal(file, store.Where(opts));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void TheLicenceBlobSitsNextToTheSettingsBlob()
    {
        Assert.Equal("https://acct.blob.core.windows.net/hivebdc/license.lic",
            AzureETagBlob.SiblingUrl("https://acct.blob.core.windows.net/hivebdc/settings.json", "license.lic"));
        Assert.Equal("https://acct.blob.core.windows.net/hivebdc/customer-a/license.lic",
            AzureETagBlob.SiblingUrl("https://acct.blob.core.windows.net/hivebdc/customer-a/settings.json", "license.lic"));
    }
}