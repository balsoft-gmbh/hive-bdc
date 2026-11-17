using System.Text;
using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Balsoft.Hive.BDC.Licensing;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Design check 11: in Azure the licence is bound to the tenant of the instance's own identity.</summary>
public sealed class LicenseTenantTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";

    private static string Jwt(string payloadJson)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Part("""{"alg":"RS256","typ":"JWT"}""") + "." + Part(payloadJson) + ".c2lnbmF0dXJl";
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void TheTenantComesFromTheTidClaimOfTheToken()
        => Assert.Equal(Tenant, InstanceTenant.TenantOfToken(Jwt($$"""{"aud":"https://storage.azure.com","tid":"{{Tenant}}","oid":"x"}""")));

    [Theory]
    [InlineData("""{"aud":"https://storage.azure.com"}""")]
    [InlineData("""{"tid":"not-a-guid"}""")]
    [InlineData("""{"tid":42}""")]
    [InlineData("""["tid"]""")]
    public void ATokenWithoutAUsableTidNamesNoTenant(string payload)
        => Assert.Null(InstanceTenant.TenantOfToken(Jwt(payload)));

    [Theory]
    [InlineData("")]
    [InlineData("no-dots")]
    [InlineData("a.%%%.c")]
    [InlineData("a.b")]
    public void SomethingThatIsNotAJwtNamesNoTenant(string token)
        => Assert.Null(InstanceTenant.TenantOfToken(token));

    [Fact]
    public void AFailedAttemptIsRepeatedAtMostOnceAMinuteAndAKnownTenantIsNotAskedAgain()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        int calls = 0;
        bool identityUp = false;
        var tenant = new InstanceTenant(() =>
        {
            calls++;
            return identityUp ? Jwt($$"""{"tid":"{{Tenant}}"}""") : throw new InvalidOperationException("ManagedIdentityCredential: no identity");
        }, clock);

        Assert.False(tenant.Resolve(out _, out var problem));
        Assert.Equal("ManagedIdentityCredential: no identity", problem);
        identityUp = true;
        clock.Now = clock.Now.AddSeconds(30);
        Assert.False(tenant.Resolve(out _, out _));
        Assert.Equal(1, calls);

        clock.Now = clock.Now.AddSeconds(31);
        Assert.True(tenant.Resolve(out var found, out _));
        Assert.Equal(Tenant, found);
        clock.Now = clock.Now.AddDays(3);
        Assert.Equal(Tenant, tenant.For(new BDCOptions()));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WhileTheTenantIsUnknownATenantBoundLicenceFailsClosed()
    {
        var tenant = new InstanceTenant(() => throw new InvalidOperationException("no identity"));

        Assert.Equal(InstanceTenant.Unknown, tenant.For(new BDCOptions()));
    }

    [Fact]
    public void TheSettingsCannotMoveTheLicenceToAnotherTenantInAzure()
    {
        var opts = new BDCOptions { SharePointAuth = { TenantId = "99999999-9999-9999-9999-999999999999" } };

        Assert.Equal(Tenant, new InstanceTenant(() => Jwt($$"""{"tid":"{{Tenant}}"}""")).For(opts));
        Assert.Equal("99999999-9999-9999-9999-999999999999", new SettingsLicenseTenant().For(opts));
    }

    [Fact]
    public void TheGateWaitsWhileTheTenantCannotBeDetermined()
    {
        var gate = new AzureProcessingGate(new TableStateStore(new MemoryTableRows(), NullLogger.Instance), new SecretResolver(null),
            tenant: new InstanceTenant(() => throw new InvalidOperationException("no identity")));

        Assert.Equal("the tenant of this instance cannot be determined: no identity", gate.Hold(new BDCOptions()));
    }
}