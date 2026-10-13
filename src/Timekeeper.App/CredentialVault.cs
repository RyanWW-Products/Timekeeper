using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Timekeeper.Core;

namespace Timekeeper.App;

internal static class CredentialVault
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential value, uint flags);
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint="CredDeleteW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    public static Credentials Load(AppSettings settings) => new(Read(Key(settings,"Toggl")), Read(Key(settings,"Quickbase")));
    public static string LoadUpdateToken() => Read("Timekeeper/GitHubUpdates");
    public static void SaveUpdateToken(string token) => Write("Timekeeper/GitHubUpdates", "GitHub", token);
    public static void Save(AppSettings settings, Credentials credentials)
    {
        Write(Key(settings,"Toggl"), settings.Email, credentials.TogglToken);
        Write(Key(settings,"Quickbase"), settings.Email, credentials.QuickbaseToken);
    }
    private static string Key(AppSettings settings, string service) => $"Timekeeper/{settings.ProfileKey}/{service}";
    private static string Read(string key)
    {
        if (!CredRead(key, 1, 0, out var pointer))
        {
            int code = Marshal.GetLastWin32Error();
            if (code == 1168) return "";
            throw new Win32Exception(code, "Windows could not read the saved Timekeeper credential.");
        }
        try
        {
            if (pointer == IntPtr.Zero) throw InvalidCredential();
            var item = Marshal.PtrToStructure<Credential>(pointer);
            return DecodeBlob(item.CredentialBlob, item.CredentialBlobSize);
        }
        finally { CredFree(pointer); }
    }
    internal static string DecodeBlob(IntPtr pointer, uint byteCount)
    {
        // Credential Manager can return a null blob for an existing empty token.
        // Handle older saved credentials as well as an optional token being cleared.
        if (byteCount == 0) return "";
        if (pointer == IntPtr.Zero || byteCount > 2560 || byteCount % 2 != 0) throw InvalidCredential();
        return Marshal.PtrToStringUni(pointer, (int)byteCount / 2) ?? "";
    }
    private static InvalidOperationException InvalidCredential() => new("Windows returned an invalid saved credential. Open Settings and save your tokens again.");
    private static void Write(string key, string email, string token)
    {
        if (token.Length == 0)
        {
            if (!CredDelete(key, 1, 0))
            {
                int code = Marshal.GetLastWin32Error();
                if (code != 1168) throw new Win32Exception(code, "Windows could not clear the saved Timekeeper credential.");
            }
            return;
        }
        byte[] bytes = Encoding.Unicode.GetBytes(token);
        if (bytes.Length > 2560) throw new ArgumentException("That API token is too long.");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,pointer,bytes.Length);
            var item = new Credential { Type=1,TargetName=key,UserName=email,CredentialBlob=pointer,CredentialBlobSize=(uint)bytes.Length,Persist=2 };
            if (!CredWrite(ref item,0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not save the API token.");
        }
        finally { Array.Clear(bytes); Marshal.Copy(bytes,0,pointer,bytes.Length); Marshal.FreeHGlobal(pointer); }
    }
}
