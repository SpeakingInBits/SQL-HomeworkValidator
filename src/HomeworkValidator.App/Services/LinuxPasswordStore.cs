using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeworkValidator.App.Services;

/// <summary>
/// The desktop's keyring through the Secret Service (GNOME Keyring, KWallet), using libsecret. Passwords are filed under
/// libsecret's network-password schema with server, user and object = "SQL Homework Validator", so they show up in
/// Passwords and Keys (Seahorse).
/// </summary>
/// <remarks>
/// Minimal installs, WSL and servers often have no keyring running, or no libsecret at all. Then <see cref="IsAvailable"/>
/// is false and the app doesn't offer to remember the password.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxPasswordStore : IPasswordStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const int CompatNetworkSchema = 1; // SECRET_SCHEMA_TYPE_COMPAT_NETWORK

    private bool? _available;

    /// <summary>Looks up a password that doesn't exist: that works only when libsecret is installed and a keyring answers.</summary>
    public bool IsAvailable => _available ??= Try(() => Lookup("", ""), "reach the keyring", out _);

    public string Description => "your keyring";

    public string? Load(string server, string user) =>
        IsAvailable && Try(() => Lookup(server, user), "read a password", out var password) ? password : null;

    public bool Save(string server, string user, string password) =>
        IsAvailable && Try(() => WithAttributes(server, user, (schema, attributes) =>
        {
            var stored = secret_password_storev_sync(schema, attributes, IntPtr.Zero,
                $"{PasswordStore.Service}: {PasswordStore.Account(server, user)}", password, IntPtr.Zero, out var error);
            ThrowIfError(error);
            return stored;
        }), "save a password", out var saved) && saved;

    public void Delete(string server, string user)
    {
        if (!IsAvailable)
            return;

        Try(() => WithAttributes(server, user, (schema, attributes) =>
        {
            var cleared = secret_password_clearv_sync(schema, attributes, IntPtr.Zero, out var error);
            ThrowIfError(error);
            return cleared;
        }), "delete a password", out _);
    }

    private static string? Lookup(string server, string user) => WithAttributes(server, user, (schema, attributes) =>
    {
        var found = secret_password_lookupv_sync(schema, attributes, IntPtr.Zero, out var error);
        ThrowIfError(error);
        if (found == IntPtr.Zero)
            return null;

        var password = Marshal.PtrToStringUTF8(found);
        secret_password_free(found);
        return password;
    });

    /// <summary>Runs <paramref name="action"/> with the schema and a GHashTable of this entry's attributes.</summary>
    private static T WithAttributes<T>(string server, string user, Func<IntPtr, IntPtr, T> action)
    {
        var glib = NativeLibrary.Load(GLib);
        var schema = secret_get_schema(CompatNetworkSchema);
        var attributes = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
        // The table doesn't own its strings, so they're freed after it's gone.
        var strings = new List<IntPtr>();
        try
        {
            foreach (var (key, value) in new[] { ("server", server), ("user", user), ("object", PasswordStore.Service) })
            {
                var k = Marshal.StringToCoTaskMemUTF8(key);
                var v = Marshal.StringToCoTaskMemUTF8(value);
                strings.Add(k);
                strings.Add(v);
                g_hash_table_insert(attributes, k, v);
            }

            return action(schema, attributes);
        }
        finally
        {
            g_hash_table_unref(attributes);
            strings.ForEach(Marshal.FreeCoTaskMem);
        }
    }

    private static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return;

        // GError: a 32-bit domain, a 32-bit code, then the message.
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
        g_error_free(error);
        throw new ExternalException(message);
    }

    /// <summary>Runs a keyring call, treating a missing library or an unreachable keyring as a failure rather than a crash.</summary>
    private static bool Try<T>(Func<T> call, string action, out T? result)
    {
        try
        {
            result = call();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Trace.WriteLine($"Couldn't {action}: {ex.Message}");
            result = default;
            return false;
        }
    }

    [DllImport(LibSecret)]
    private static extern IntPtr secret_get_schema(int type);

    [DllImport(LibSecret)]
    private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes,
        IntPtr collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(GLib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [DllImport(GLib)]
    private static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(GLib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(GLib)]
    private static extern void g_error_free(IntPtr error);
}
