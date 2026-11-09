using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BorgStudio.Core.Secrets;

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet, KeePassXC, ...) through libsecret.
/// Items carry the attributes application=borgstudio and key=&lt;key&gt;.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class LibSecretStore : ISecretStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string Application = "borgstudio";

    // Only probed when first needed: talking to the Secret Service may start it or ask to unlock the keyring.
    private static readonly Lazy<bool> Available = new(Probe);
    private static readonly Lazy<Native> NativeApi = new(() => new Native());

    public bool IsAvailable => Available.Value;

    public string? Get(string key)
    {
        using var attributes = new Attributes(key);
        var secret = secret_password_lookupv_sync(NativeApi.Value.Schema, attributes.Table, IntPtr.Zero, out var error);
        ThrowOnError(error, "read");
        if (secret == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(secret);
        }
        finally
        {
            secret_password_free(secret);
        }
    }

    public void Set(string key, string label, string secret)
    {
        using var attributes = new Attributes(key);
        secret_password_storev_sync(NativeApi.Value.Schema, attributes.Table, null, label, secret, IntPtr.Zero, out var error);
        ThrowOnError(error, "write");
    }

    public void Delete(string key)
    {
        using var attributes = new Attributes(key);
        secret_password_clearv_sync(NativeApi.Value.Schema, attributes.Table, IntPtr.Zero, out var error);
        ThrowOnError(error, "delete");
    }

    private static bool Probe()
    {
        try
        {
            using var attributes = new Attributes("availability-probe");
            var secret = secret_password_lookupv_sync(NativeApi.Value.Schema, attributes.Table, IntPtr.Zero, out var error);
            if (secret != IntPtr.Zero)
                secret_password_free(secret);
            if (error == IntPtr.Zero)
                return true;
            g_error_free(error);
            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void ThrowOnError(IntPtr error, string action)
    {
        if (error == IntPtr.Zero)
            return;

        // GError { GQuark domain; gint code; gchar *message; }
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
        g_error_free(error);
        throw new SecretStoreException($"Secret Service: could not {action} the secret ({message}).");
    }

    /// <summary>The SecretSchema and GLib helpers, created once for the lifetime of the process.</summary>
    private sealed class Native
    {
        // SecretSchema: name (ptr), flags (int + padding), 32 x { name (ptr), type (int + padding) },
        // reserved (int + padding), 7 reserved pointers.
        private const int SchemaSize = 16 + 32 * 16 + 8 + 7 * 8;

        public Native()
        {
            var glib = NativeLibrary.Load(LibGLib);
            StrHash = NativeLibrary.GetExport(glib, "g_str_hash");
            StrEqual = NativeLibrary.GetExport(glib, "g_str_equal");

            Schema = Marshal.AllocHGlobal(SchemaSize);
            Marshal.Copy(new byte[SchemaSize], 0, Schema, SchemaSize);
            Marshal.WriteIntPtr(Schema, 0, Marshal.StringToCoTaskMemUTF8("io.github.cuban8io.BorgStudio"));
            Marshal.WriteInt32(Schema, 8, 0); // SECRET_SCHEMA_NONE
            WriteAttribute(0, "application");
            WriteAttribute(1, "key");
        }

        public IntPtr Schema { get; }

        public IntPtr StrHash { get; }

        public IntPtr StrEqual { get; }

        private void WriteAttribute(int index, string name)
        {
            var offset = 16 + index * 16;
            Marshal.WriteIntPtr(Schema, offset, Marshal.StringToCoTaskMemUTF8(name));
            Marshal.WriteInt32(Schema, offset + 8, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        }
    }

    /// <summary>GHashTable with the item attributes; owns the UTF-8 strings it points to.</summary>
    private sealed class Attributes : IDisposable
    {
        private readonly List<IntPtr> _strings = [];

        public Attributes(string key)
        {
            Table = g_hash_table_new(NativeApi.Value.StrHash, NativeApi.Value.StrEqual);
            Insert("application", Application);
            Insert("key", key);
        }

        public IntPtr Table { get; }

        public void Dispose()
        {
            g_hash_table_unref(Table);
            foreach (var pointer in _strings)
                Marshal.FreeCoTaskMem(pointer);
        }

        private void Insert(string name, string value)
        {
            var namePointer = Marshal.StringToCoTaskMemUTF8(name);
            var valuePointer = Marshal.StringToCoTaskMemUTF8(value);
            _strings.Add(namePointer);
            _strings.Add(valuePointer);
            g_hash_table_insert(Table, namePointer, valuePointer);
        }
    }

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(LibGLib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunction, IntPtr equalFunction);

    [DllImport(LibGLib)]
    private static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGLib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGLib)]
    private static extern void g_error_free(IntPtr error);
}
