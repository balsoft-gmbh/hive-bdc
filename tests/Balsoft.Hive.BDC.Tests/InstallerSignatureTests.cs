using Balsoft.Hive.BDC.App.Services;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class InstallerSignatureTests
{
    [Fact]
    public void MissingFileIsRejected()
    {
        Assert.False(InstallerSignature.Verify(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), out _));
    }

    [Fact]
    public void UnsignedAssemblyIsRejected()
    {
        Assert.False(InstallerSignature.Verify(typeof(InstallerSignatureTests).Assembly.Location, out _));
    }

    [SignedInstallerFact]
    public void TrustedBalsoftInstallerIsAccepted()
    {
        Assert.True(InstallerSignature.Verify(SignedInstallerFactAttribute.FilePath!, out var detail), detail);
    }

    [SignedInstallerFact]
    public void ModifiedBalsoftInstallerIsRejectedEvenThoughItsCertificateRemains()
    {
        string copy = Path.Combine(Path.GetTempPath(), "hivebdc-signature-test-" + Guid.NewGuid() + ".exe");
        try
        {
            File.Copy(SignedInstallerFactAttribute.FilePath!, copy);
            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = 40; // Reserved DOS-header byte covered by the signature.
                int original = stream.ReadByte();
                Assert.NotEqual(-1, original);
                stream.Position = 40;
                stream.WriteByte((byte)(original ^ 1));
            }
#pragma warning disable SYSLIB0057
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(copy);
#pragma warning restore SYSLIB0057
            Assert.Contains("Balsoft", certificate.Subject);
            Assert.False(InstallerSignature.Verify(copy, out var detail));
            Assert.Contains("0x80096010", detail); // TRUST_E_BAD_DIGEST
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
    }
}

public sealed class SignedInstallerFactAttribute : FactAttribute
{
    public static string? FilePath => Environment.GetEnvironmentVariable("HIVEBDC_SIGNED_TEST_FILE");

    public SignedInstallerFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows Authenticode integration test.";
        else if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            Skip = "Set HIVEBDC_SIGNED_TEST_FILE to a signed Balsoft installer to run this integration test.";
    }
}
