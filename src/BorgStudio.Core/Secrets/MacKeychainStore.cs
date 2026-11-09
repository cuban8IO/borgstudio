using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BorgStudio.Core.Secrets;

/// <summary>
/// macOS keychain: generic passwords with service "BorgStudio" and the key as account.
/// Uses the classic SecKeychain API (deprecated, but available and much simpler than SecItem*).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    private static readonly byte[] Service = Encoding.UTF8.GetBytes(SecretStores.ServiceName);

    public bool IsAvailable => true;

    public string? Get(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)account.Length, account, out var length, out var data, out var item);
        try
        {
            if (status == ErrSecItemNotFound)
                return null;
            Check(status, "read");
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            if (data != IntPtr.Zero)
                SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
                CFRelease(item);
        }
    }

    public void Set(string key, string label, string secret)
    {
        Delete(key);
        var account = Encoding.UTF8.GetBytes(key);
        var password = Encoding.UTF8.GetBytes(secret);
        var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)account.Length, account, (uint)password.Length, password, out var item);
        Array.Clear(password);
        if (item != IntPtr.Zero)
            CFRelease(item);
        Check(status, "write");
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)account.Length, account, out _, out var data, out var item);
        try
        {
            if (status == ErrSecItemNotFound)
                return;
            Check(status, "find");
            Check(SecKeychainItemDelete(item), "delete");
        }
        finally
        {
            if (data != IntPtr.Zero)
                SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
                CFRelease(item);
        }
    }

    private static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
            throw new SecretStoreException($"macOS keychain: could not {action} the password (OSStatus {status}).");
    }

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
