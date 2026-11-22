using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BorgStudio.Providers.Usb.Resources;
using Microsoft.Win32.SafeHandles;

namespace BorgStudio.Providers.Usb;

/// <summary>
/// The real drives. Ejecting: Windows asks the device manager (as "Safely remove hardware" does, without its dialogs),
/// Linux uses udisksctl (unmount, power off), macOS diskutil eject.
/// </summary>
public sealed class SystemDrives : IDriveSystem
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Linux/macOS mount points that never hold user data.</summary>
    private static readonly string[] SystemMounts = ["/proc", "/sys", "/dev", "/run/user", "/snap", "/boot", "/System/Volumes", "/private/var/vm"];

    public IReadOnlyList<DriveRoot> Roots()
    {
        var roots = new List<DriveRoot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Ram or DriveType.NoRootDirectory)
                    continue;
                var root = drive.RootDirectory.FullName;
                if (!OperatingSystem.IsWindows()
                    && (drive.DriveType == DriveType.Unknown
                        || SystemMounts.Any(system => root == system || root.StartsWith(system + "/", StringComparison.Ordinal))
                        || (root.StartsWith("/run/", StringComparison.Ordinal) && !root.StartsWith("/run/media/", StringComparison.Ordinal))))
                    continue;
                roots.Add(new DriveRoot(root, drive.DriveType == DriveType.Network));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive that disappeared meanwhile.
            }
        }
        return roots;
    }

    public async Task<bool> IsExternalAsync(string path, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return IsExternalOnWindows(path);
        if (OperatingSystem.IsMacOS())
            return await RunAsync("diskutil", ["info", MountRoot(path)], cancellationToken) is { ExitCode: 0 } info
                && DiskUtil.IsExternal(info.Output);
        if (await LinuxDeviceAsync(path, cancellationToken) is not { } device)
            return false;
        var disk = await RunAsync("lsblk", ["-no", "PKNAME", device], cancellationToken) is { ExitCode: 0 } parent
            && parent.Output.Trim() is { Length: > 0 } name ? "/dev/" + name.Split('\n')[0].Trim() : device;
        if (await RunAsync("lsblk", ["-ndo", "HOTPLUG,RM,TRAN", disk], cancellationToken) is not { ExitCode: 0 } columns)
            return false;
        var parts = columns.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Take(2).Any(flag => flag == "1") || parts.Skip(2).Any(transport => transport is "usb" or "ieee1394" or "mmc");
    }

    public async Task<string?> EjectAsync(string path, CancellationToken cancellationToken = default)
    {
        var name = MountRoot(path);
        if (!await IsExternalAsync(path, cancellationToken))
            return Format(Strings.EjectNotExternal, name);
        if (OperatingSystem.IsWindows())
            return await Task.Run(() => EjectOnWindows(path), cancellationToken);
        if (OperatingSystem.IsMacOS())
            return await RunAsync("diskutil", ["eject", name], cancellationToken) is { ExitCode: 0 } ? null
                : Format(Strings.EjectFailed, name, Strings.EjectVetoed);

        if (await LinuxDeviceAsync(path, cancellationToken) is not { } device)
            return Format(Strings.EjectFailed, name, "findmnt");
        if (await RunAsync("udisksctl", ["unmount", "--no-user-interaction", "-b", device], cancellationToken) is not { ExitCode: 0 } unmounted)
            return Format(Strings.EjectFailed, name, Strings.EjectVetoed);
        return await RunAsync("udisksctl", ["power-off", "--no-user-interaction", "-b", device], cancellationToken) is { ExitCode: 0 } ? null
            : Format(Strings.EjectFailed, name, unmounted.Output.Trim());
    }

    /// <summary>The root of the drive or mount point holding <paramref name="path"/>.</summary>
    public static string MountRoot(string path)
    {
        if (OperatingSystem.IsWindows())
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        var full = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName.TrimEnd('/'))
            .Where(root => root.Length == 0 || full == root || full.StartsWith(root + "/", StringComparison.Ordinal))
            .OrderByDescending(root => root.Length)
            .Select(root => root.Length == 0 ? "/" : root)
            .FirstOrDefault() ?? "/";
    }

    private static string Format(string text, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, text, arguments);

    private static async Task<string?> LinuxDeviceAsync(string path, CancellationToken cancellationToken) =>
        await RunAsync("findmnt", ["-n", "-o", "SOURCE", "--target", path], cancellationToken) is { ExitCode: 0 } found
        && found.Output.Trim() is { } device && device.StartsWith("/dev/", StringComparison.Ordinal) ? device : null;

    private sealed record CommandResult(int ExitCode, string Output);

    private static async Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return new CommandResult(process.ExitCode, await output + await error);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static class DiskUtil
    {
        /// <summary>diskutil info: "Device Location: External", "Removable Media: Removable" or "Protocol: USB".</summary>
        public static bool IsExternal(string info) => info.Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .Any(parts => (parts[0].Trim(), parts[1].Trim()) is ("Device Location", "External") or ("Removable Media", "Removable")
                or ("Protocol", "USB") or ("Protocol", "FireWire") or ("Protocol", "Secure Digital"));
    }

    // --- Windows ---

    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;
    private const int BusType1394 = 0x4, BusTypeUsb = 0x7, BusTypeSd = 0xC, BusTypeMmc = 0xD;
    private static Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    private static bool IsExternalOnWindows(string path)
    {
        var root = MountRoot(path);
        if (new DriveInfo(root).DriveType == DriveType.Removable)
            return true;
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
            return false;
        using var volume = OpenVolume(root);
        if (volume.IsInvalid)
            return false;
        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery } → STORAGE_DEVICE_DESCRIPTOR.
        var query = new byte[12];
        var descriptor = new byte[1024];
        if (!DeviceIoControl(volume, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out _, IntPtr.Zero))
            return false;
        var removable = descriptor[10] != 0;
        var bus = BitConverter.ToInt32(descriptor, 28);
        return removable || bus is BusTypeUsb or BusType1394 or BusTypeSd or BusTypeMmc;
    }

    private static SafeFileHandle OpenVolume(string root) =>
        CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private static bool TryGetDeviceNumber(SafeFileHandle handle, out StorageDeviceNumber number) =>
        DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out number, Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero);

    /// <summary>
    /// Finds the disk device of the volume and asks its parent (the USB device) to be removed, as "Safely remove
    /// hardware" does. A veto (a program still uses the drive) comes back as the message.
    /// </summary>
    private static string? EjectOnWindows(string path)
    {
        var root = MountRoot(path);
        StorageDeviceNumber volumeNumber;
        using (var volume = OpenVolume(root))
        {
            if (volume.IsInvalid || !TryGetDeviceNumber(volume, out volumeNumber))
                return Format(Strings.EjectFailed, root, new System.ComponentModel.Win32Exception().Message);
        }

        if (FindDisk(volumeNumber) is not { } diskInstance)
            return Format(Strings.EjectFailed, root, "device not found");
        if (CM_Get_Parent(out var parent, diskInstance, 0) != 0)
            return Format(Strings.EjectFailed, root, "device not found");

        // The device manager may be busy right after the last write: a few tries, as Windows' own tools do.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var vetoName = new StringBuilder(260);
            if (CM_Request_Device_EjectW(parent, out var vetoType, vetoName, vetoName.Capacity, 0) == 0 && vetoType == 0)
                return null;
            Thread.Sleep(500);
        }
        return Format(Strings.EjectFailed, root, Strings.EjectVetoed);
    }

    private static int? FindDisk(StorageDeviceNumber volumeNumber)
    {
        var devices = SetupDiGetClassDevsW(ref DiskInterface, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devices == IntPtr.Zero || devices == new IntPtr(-1))
            return null;
        try
        {
            for (var index = 0; ; index++)
            {
                var interfaceData = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref DiskInterface, index, ref interfaceData))
                    return null;

                var infoData = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                SetupDiGetDeviceInterfaceDetailW(devices, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0)
                    continue;
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is the size of the fixed part (8 on 64-bit, 6 on 32-bit).
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devices, ref interfaceData, detail, required, out _, ref infoData))
                        continue;
                    var devicePath = Marshal.PtrToStringUni(detail + 4);
                    if (devicePath is null)
                        continue;
                    using var disk = CreateFileW(devicePath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (!disk.IsInvalid && TryGetDeviceNumber(disk, out var diskNumber)
                        && diskNumber.DeviceType == volumeNumber.DeviceType && diskNumber.DeviceNumber == volumeNumber.DeviceNumber)
                        return infoData.DevInst;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devices);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
        out StorageDeviceNumber output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr infoData, ref Guid interfaceGuid, int index,
        ref DeviceInterfaceData interfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref DeviceInterfaceData interfaceData, IntPtr detail,
        int detailSize, out int requiredSize, ref DeviceInfoData infoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref DeviceInterfaceData interfaceData, IntPtr detail,
        int detailSize, out int requiredSize, IntPtr infoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out int parent, int devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(int devInst, out int vetoType, StringBuilder vetoName, int vetoNameLength, int flags);
}
