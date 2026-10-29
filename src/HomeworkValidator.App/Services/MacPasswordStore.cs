using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HomeworkValidator.App.Services;

/// <summary>
/// The student's macOS login keychain (Keychain Access → login → "SQL Homework Validator"). Uses the generic-password calls,
/// which are deprecated in favor of SecItem but still supported, and need no Core Foundation dictionaries.
/// </summary>
/// <remarks>
/// macOS lets the app that saved a password read it without asking. The app is only ad-hoc signed, so after an update macOS
/// sees a different app and asks once whether it may use the keychain item ("Always Allow").
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacPasswordStore : IPasswordStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;
    private const int ItemNotFound = -25300;

    public bool IsAvailable => true;
    public string Description => "your Mac's keychain";

    public string? Load(string server, string user)
    {
        var (service, account) = Names(server, user);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out var length, out var data, out var item);
        if (status != Success)
        {
            Log("read", status);
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            CFRelease(item);
        }
    }

    public bool Save(string server, string user, string password)
    {
        var (service, account) = Names(server, user);
        var secret = Encoding.UTF8.GetBytes(password);
        try
        {
            // Update the existing item, so it keeps the access the student already granted.
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, IntPtr.Zero, out var item);
            if (status == Success)
            {
                try
                {
                    status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret);
                }
                finally
                {
                    CFRelease(item);
                }
            }
            else if (status == ItemNotFound)
            {
                status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    (uint)secret.Length, secret, out var added);
                if (status == Success)
                    CFRelease(added);
            }

            if (status == Success)
                return true;
            Log("save", status);
            return false;
        }
        finally
        {
            Array.Clear(secret);
        }
    }

    public void Delete(string server, string user)
    {
        var (service, account) = Names(server, user);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out _, IntPtr.Zero, out var item);
        if (status != Success)
        {
            Log("delete", status);
            return;
        }

        try
        {
            status = SecKeychainItemDelete(item);
            if (status != Success)
                Log("delete", status);
        }
        finally
        {
            CFRelease(item);
        }
    }

    private static (byte[] Service, byte[] Account) Names(string server, string user) =>
        (Encoding.UTF8.GetBytes(PasswordStore.Service), Encoding.UTF8.GetBytes(PasswordStore.Account(server, user)));

    private static void Log(string action, int status)
    {
        if (status != ItemNotFound)
            Trace.WriteLine($"The keychain couldn't {action} a password: OSStatus {status}.");
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    /// <summary>The same call without reading the password, for finding the item to change or delete.</summary>
    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
