using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UniDashboard.Core.Auth;

/// <summary>Token persisted with DPAPI (CurrentUser scope).</summary>
public class TokenStore(string? path = null)
{
    readonly string _path = path ?? Paths.TokenFile;

    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
    }

    public void Save(string token) =>
        File.WriteAllBytes(_path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public void Clear() { if (File.Exists(_path)) File.Delete(_path); }
}

/// <summary>Windows Credential Manager (generic credential) for the univie login.</summary>
public static class CredentialStore
{
    const string Target = "UniDashboard/univie";
    const string LegacyTarget = "MoodleLite/univie";
    const uint CredTypeGeneric = 1, PersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buf);

    public static void Save(string user, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CredTypeGeneric, TargetName = Target, UserName = user,
                CredentialBlob = ptr, CredentialBlobSize = (uint)blob.Length, Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref c, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static (string User, string Password)? Load()
    {
        if (!CredRead(Target, CredTypeGeneric, 0, out var p))
        {
            // Migrate the entry from the app's former name.
            if (!CredRead(LegacyTarget, CredTypeGeneric, 0, out var old)) return null;
            try
            {
                var oc = Marshal.PtrToStructure<CREDENTIAL>(old);
                var opw = Marshal.PtrToStringUni(oc.CredentialBlob, (int)oc.CredentialBlobSize / 2) ?? "";
                Save(oc.UserName, opw);
            }
            finally { CredFree(old); }
            CredDelete(LegacyTarget, CredTypeGeneric, 0);
            if (!CredRead(Target, CredTypeGeneric, 0, out p)) return null;
        }
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var pw = Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2) ?? "";
            return (c.UserName, pw);
        }
        finally { CredFree(p); }
    }

    public static void Delete() => CredDelete(Target, CredTypeGeneric, 0);
}
