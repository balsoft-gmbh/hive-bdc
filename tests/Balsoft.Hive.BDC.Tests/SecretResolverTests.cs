using Balsoft.Hive.BDC.Storage;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class SecretResolverTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Vault : ISecretVault
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public int Reads { get; private set; }

        public bool Down { get; set; }

        public string? Get(string name)
        {
            Reads++;
            if (Down) throw new InvalidOperationException("vault not reachable");
            return Secrets.GetValueOrDefault(name);
        }

        public void Set(string name, string value)
        {
            if (Down) throw new InvalidOperationException("vault not reachable");
            Secrets[name] = value;
        }
    }

    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly Vault _vault = new();

    [Fact]
    public void WithAVaultASecretGoesToTheVaultAndTheSettingsHoldOnlyItsName()
    {
        var resolver = new SecretResolver(_vault, _clock);

        string stored = resolver.Protect("p@ss", "Connection DocuWare/Main");

        Assert.Equal("kv:connection-docuware-main", stored);
        Assert.Equal("p@ss", _vault.Secrets["connection-docuware-main"]);
        Assert.True(SecretResolver.IsProtected(stored));
        Assert.Equal("p@ss", resolver.Unprotect(stored));
    }

    [Fact]
    public void WithAVaultASecretWithoutANameIsRefused()
    {
        var resolver = new SecretResolver(_vault, _clock);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Protect("p@ss"));
        Assert.DoesNotContain("p@ss", ex.Message);
        Assert.Empty(_vault.Secrets);
        Assert.Equal("", resolver.Protect(""));
    }

    [Fact]
    public void AVaultValueIsReadOnceAndAgainAfterTheCacheTime()
    {
        _vault.Secrets["sp-secret"] = "one";
        var resolver = new SecretResolver(_vault, _clock, TimeSpan.FromMinutes(5));

        Assert.Equal("one", resolver.Unprotect("kv:sp-secret"));
        _vault.Secrets["sp-secret"] = "two";
        _clock.Now = _clock.Now.AddMinutes(4);
        Assert.Equal("one", resolver.Unprotect("kv:sp-secret"));
        Assert.Equal(1, _vault.Reads);

        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.Equal("two", resolver.Unprotect("kv:sp-secret"));
        Assert.Equal(2, _vault.Reads);
    }

    [Fact]
    public void AnUnreachableVaultUsesTheLastValueOrFailsWithTheNameOnly()
    {
        _vault.Secrets["mail-secret"] = "s3cret-value";
        var resolver = new SecretResolver(_vault, _clock);
        Assert.Equal("s3cret-value", resolver.Unprotect("kv:mail-secret"));

        _vault.Down = true;
        _clock.Now = _clock.Now.AddHours(1);
        Assert.Equal("s3cret-value", resolver.Unprotect("kv:mail-secret"));

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Unprotect("kv:never-read"));
        Assert.Contains("never-read", ex.Message);
        Assert.DoesNotContain("s3cret-value", ex.Message);
    }

    [Fact]
    public void AMissingVaultSecretOrAReferenceWithoutVaultSaysWhichSecret()
    {
        Assert.Contains("'gone'", Assert.Throws<InvalidOperationException>(() => new SecretResolver(_vault, _clock).Unprotect("kv:gone")).Message);
        Assert.Contains("no Key Vault is configured", Assert.Throws<InvalidOperationException>(() => new SecretResolver(null).Unprotect("kv:gone")).Message);
    }

    [Fact]
    public void PlainValuesStayAsTheyAreWithAndWithoutAVault()
    {
        Assert.Equal("plain", new SecretResolver(_vault, _clock).Unprotect("plain"));
        Assert.Equal("plain", new SecretResolver(null).Unprotect("plain"));
        Assert.False(SecretResolver.IsProtected("plain"));
        Assert.Equal(0, _vault.Reads);
    }

    [Theory]
    [InlineData("Connection DocuWare/Main", "connection-docuware-main")]
    [InlineData("  --SharePoint__ClientSecret--  ", "sharepoint-clientsecret")]
    [InlineData("Ünïcode Name", "n-code-name")]
    public void VaultNamesAreLettersDigitsAndDashes(string text, string name) => Assert.Equal(name, SecretResolver.VaultName(text));

    [Fact]
    public void AVaultNameIsCutTo127Characters()
        => Assert.Equal(127, SecretResolver.VaultName(new string('a', 200)).Length);
}