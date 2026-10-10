using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Balsoft.Hive.BDC.App.Services;

internal static class InstallerSignature
{
    public static bool Verify(string file, out string detail)
    {
        detail = "Authenticode verification requires Windows";
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            int status = VerifyTrust(Path.GetFullPath(file), stream.SafeFileHandle.DangerousGetHandle());
            if (status != 0)
            {
                detail = $"Windows signature verification failed (0x{status:X8})";
                return false;
            }

            // Trust and file integrity have been checked; now require our publisher identity.
#pragma warning disable SYSLIB0057
            using var signedCertificate = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signedCertificate.GetRawCertData());
            detail = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.Equals(detail, "Balsoft GmbH", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            detail = "Signature verification failed: " + ex.Message;
            return false;
        }
    }

    private static int VerifyTrust(string path, IntPtr handle)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        IntPtr pathPointer = Marshal.StringToCoTaskMemUni(path);
        IntPtr filePointer = IntPtr.Zero;
        try
        {
            var file = new TrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<TrustFileInfo>(),
                Path = pathPointer,
                Handle = handle,
            };
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = filePointer,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x80, // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            try
            {
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE, including failed verification
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            if (filePointer != IntPtr.Zero) Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFileInfo
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
