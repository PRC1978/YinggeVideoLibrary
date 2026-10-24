using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace YinggeVideoLibrary;

// Generic credentials are protected by Windows for the current signed-in user.
internal static class WindowsCredentialStore
{
    const int Generic = 1;
    const int LocalMachinePersistence = 2;
    static string Target(string name) => "YinggeVideoLibrary/" + name;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    static extern void CredFree(IntPtr credential);

    public static string? Read(string name)
    {
        if (!CredRead(Target(name), Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            if (value.CredentialBlobSize == 0) return "";
            var bytes = new byte[value.CredentialBlobSize];
            Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string name, string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            if (!CredDelete(Target(name), Generic, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560) throw new ArgumentException("凭据超过 Windows 凭据管理器的长度限制。", nameof(secret));
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = Generic, TargetName = Target(name), UserName = Environment.UserName,
                CredentialBlob = pointer, CredentialBlobSize = bytes.Length, Persist = LocalMachinePersistence };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(pointer); Array.Clear(bytes); }
    }
}
