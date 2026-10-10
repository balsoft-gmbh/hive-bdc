using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>
/// Grants the "Log on as a service" (SeServiceLogonRight) privilege to an account
/// via the LSA policy API — sc.exe does NOT do this, which is the usual cause of
/// service start error 1069 ("logon failure") with a domain/user account.
/// </summary>
[SupportedOSPlatform("windows")]
static class ServiceAccount
{
    const uint POLICY_CREATE_ACCOUNT = 0x00000010;
    const uint POLICY_LOOKUP_NAMES = 0x00000800;
    const string SeServiceLogonRight = "SeServiceLogonRight";

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint LsaOpenPolicy(IntPtr SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, uint DesiredAccess, out IntPtr PolicyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint LsaAddAccountRights(IntPtr PolicyHandle, byte[] AccountSid, LSA_UNICODE_STRING[] UserRights, uint CountOfRights);

    [DllImport("advapi32.dll")]
    static extern uint LsaClose(IntPtr PolicyHandle);

    [DllImport("advapi32.dll")]
    static extern int LsaNtStatusToWinError(uint status);

    /// <summary>Grant "Log on as a service" to the account (e.g. DOMAIN\\user). Returns (ok, message).</summary>
    public static (bool ok, string message) GrantLogonAsService(string account)
    {
        try
        {
            // Resolve the account name to its binary SID.
            var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            byte[] sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);

            var objAttr = new LSA_OBJECT_ATTRIBUTES();
            uint open = LsaOpenPolicy(IntPtr.Zero, ref objAttr,
                POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES, out IntPtr policy);
            if (open != 0)
                return (false, $"LsaOpenPolicy failed (Win32 error {LsaNtStatusToWinError(open)}).");

            try
            {
                var rights = new LSA_UNICODE_STRING[1];
                rights[0].Buffer = Marshal.StringToHGlobalUni(SeServiceLogonRight);
                rights[0].Length = (ushort)(SeServiceLogonRight.Length * 2);
                rights[0].MaximumLength = (ushort)((SeServiceLogonRight.Length + 1) * 2);
                try
                {
                    uint add = LsaAddAccountRights(policy, sidBytes, rights, 1);
                    if (add != 0)
                        return (false, $"LsaAddAccountRights failed (Win32 error {LsaNtStatusToWinError(add)}).");
                    return (true, $"Granted 'Log on as a service' to {account}.");
                }
                finally { Marshal.FreeHGlobal(rights[0].Buffer); }
            }
            finally { LsaClose(policy); }
        }
        catch (IdentityNotMappedException)
        {
            return (false, $"Account '{account}' could not be resolved — check DOMAIN\\user spelling and that the domain is reachable.");
        }
        catch (Exception ex)
        {
            return (false, "Could not grant the right: " + ex.Message);
        }
    }
}
