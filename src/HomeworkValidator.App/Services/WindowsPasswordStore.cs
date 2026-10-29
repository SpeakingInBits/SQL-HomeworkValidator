using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace HomeworkValidator.App.Services;

/// <summary>
/// Windows Credential Manager (Control Panel → Credential Manager → Windows Credentials), where Windows keeps passwords
/// encrypted for the signed-in user. Entries are named "SQL Homework Validator:login@server".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPasswordStore : IPasswordStore
{
    private const int GenericCredential = 1;
    // Kept on this computer for this user, across sign-ins, but not copied to other computers with a roaming profile.
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public bool IsAvailable => true;
    public string Description => "Windows Credential Manager";

    public string? Load(string server, string user)
    {
        if (!CredRead(Target(server, user), GenericCredential, 0, out var handle))
        {
            Log("read");
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public bool Save(string server, string user, string password)
    {
        var target = Marshal.StringToCoTaskMemUni(Target(server, user));
        var userName = Marshal.StringToCoTaskMemUni(user);
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = Encoding.Unicode.GetByteCount(password),
                Persist = PersistLocalMachine,
            };

            if (CredWrite(ref credential, 0))
                return true;
            Log("save");
            return false;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
            Marshal.FreeCoTaskMem(userName);
            Marshal.FreeCoTaskMem(target);
        }
    }

    public void Delete(string server, string user)
    {
        if (!CredDelete(Target(server, user), GenericCredential, 0))
            Log("delete");
    }

    private static string Target(string server, string user) => $"{PasswordStore.Service}:{PasswordStore.Account(server, user)}";

    private static void Log(string action)
    {
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNotFound)
            Trace.WriteLine($"Credential Manager couldn't {action} a password: error {error}.");
    }

    // CREDENTIALW from wincred.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
