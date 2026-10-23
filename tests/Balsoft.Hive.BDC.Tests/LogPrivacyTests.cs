using Balsoft.Hive.BDC.Documents;
using Balsoft.Hive.BDC.Hosting;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class LogPrivacyTests
{
    [Theory]
    [InlineData("max.mustermann@contoso.com", "m***@contoso.com")]
    [InlineData("a@b.de", "a***@b.de")]
    [InlineData("no-address", "***")]
    [InlineData("@contoso.com", "***")]
    public void MailAddressesAreMaskedInTheLog(string address, string expected)
        => Assert.Equal(expected, DocumentProcessor.MaskAddress(address));

    [Fact]
    public void SeveralAddressesStayCommaSeparated()
        => Assert.Equal("a***@x.de, b***@y.de", DocumentProcessor.MaskAddresses(new[] { "anna@x.de", "bob@y.de" }));

    [Fact]
    public void LineBreaksFromARequestCannotStartANewLogEntry()
        => Assert.Equal("GET /status  [INF] fake entry", LocalStatusServer.ForLog("GET /status\r\n[INF] fake entry"));
}
