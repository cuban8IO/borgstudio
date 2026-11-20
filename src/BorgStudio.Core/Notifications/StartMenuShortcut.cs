using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BorgStudio.Core.Notifications;

/// <summary>
/// A shortcut to BorgStudio in the user's start menu, carrying the app's AppUserModelID. Windows shows toasts of a
/// desktop app without package only if such a shortcut exists (the registry entry alone lets them reach the history,
/// but never the screen). Kept pointing at the running executable, so a moved app keeps working.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartMenuShortcut
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BorgStudio.lnk");

    /// <summary>Creates or updates the shortcut unless it already points at <paramref name="executable"/> with <paramref name="appUserModelId"/>.</summary>
    /// <returns><c>true</c> if it was written now (Windows may need a moment before it shows toasts for it).</returns>
    /// <exception cref="COMException">The shortcut could not be read or written.</exception>
    public static bool Ensure(string path, string executable, string appUserModelId)
    {
        if (File.Exists(path) && Read(path) is { } existing
            && string.Equals(existing.Target, executable, StringComparison.OrdinalIgnoreCase) && existing.AppUserModelId == appUserModelId)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("BorgStudio");
            var store = (IPropertyStore)link;
            var key = AppUserModelIdKey;
            var value = PropVariant.FromString(appUserModelId);
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                value.Clear();
            }
            ((IPersistFile)link).Save(path, true);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The shortcut's target and AppUserModelID; <c>null</c> if it cannot be read.</summary>
    public static (string Target, string? AppUserModelId)? Read(string path)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new char[1024];
            link.GetPath(target, target.Length, IntPtr.Zero, 0);
            return (new string(target).TrimEnd(char.MinValue), ReadAppUserModelId(path));
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The file's System.AppUserModel.ID, as the shell sees it.</summary>
    private static string? ReadAppUserModelId(string path)
    {
        var storeId = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref storeId, out var store) != 0)
            return null;
        try
        {
            // A PROPVARIANT (24 bytes on 64 bit) read as raw memory: marshalling it as an out struct comes back empty.
            var value = Marshal.AllocCoTaskMem(24);
            try
            {
                for (var offset = 0; offset < 24; offset++)
                    Marshal.WriteByte(value, offset, 0);
                var key = AppUserModelIdKey;
                if (store.GetValue(ref key, value) != 0)
                    return null;
                var text = Marshal.ReadInt16(value) == PropVariant.StringType ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(value, 8)) : null;
                PropVariantClear(value);
                return text;
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr variant);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid interfaceId,
        out IPropertyStore store);

    public static void Remove(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>PKEY_AppUserModel_ID.</summary>
    private static PropertyKey AppUserModelIdKey => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, int propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        public const short StringType = 31; // VT_LPWSTR

        [FieldOffset(0)]
        private short _type;

        [FieldOffset(8)]
        private IntPtr _pointer;

        public static PropVariant FromString(string text) => new() { _type = StringType, _pointer = Marshal.StringToCoTaskMemUni(text) };

        public void Clear() => PropVariantClear(ref this);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant variant);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray)] char[] file, int length, IntPtr findData, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray)] char[] name, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray)] char[] directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray)] char[] arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray)] char[] iconPath, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig]
        int GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
