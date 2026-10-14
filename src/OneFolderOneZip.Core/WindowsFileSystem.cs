using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OneFolderOneZip.Core;

public static class WindowsFileSystem
{
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    internal static string NativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    public static bool IsSupported(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Device) != 0) return false;
        if ((attributes & FileAttributes.ReparsePoint) == 0) return true;
        using var handle = OpenMetadata(path);
        return IsSupported(handle);
    }

    private static bool IsSupported(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8)) ThrowLastError();
        if ((info.Attributes & (uint)FileAttributes.Device) != 0) return false;
        // Cloud placeholders contain file data and may hydrate. Never follow name-surrogate links.
        return (info.Attributes & (uint)FileAttributes.ReparsePoint) == 0
            || (info.Tag & 0xffff0fff) == 0x9000001a;
    }

    public static string Identity(string path)
    {
        using var handle = OpenMetadata(path);
        if (!GetFileInformationByHandle(handle, out FileInformation info)) ThrowLastError();
        var final = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (size == 0 || size >= final.Capacity) ThrowLastError();
        // The final path unifies mapped drives/UNC and parent aliases. The file ID also
        // preserves case-sensitive folders that differ only in case on NTFS.
        string resolved = final.ToString();
        string root = Path.GetPathRoot(resolved) ?? resolved;
        return info.IndexHigh != 0 || info.IndexLow != 0
            ? $"{root.ToUpperInvariant()}|{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}"
            : resolved;
    }

    public static FileStream? OpenRead(string path)
    {
        // First open the link itself and check its tag to avoid following a link
        // substituted between enumeration and opening.
        var handle = CreateFile(NativePath(path), 0x80000000, 7, IntPtr.Zero, 3,
            OpenReparsePoint | 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); ThrowLastError(); }
        try
        {
            if (!IsSupported(handle)) { handle.Dispose(); return null; }
            // Cloud files need a normal open for their provider to hydrate content.
            if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8)) ThrowLastError();
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                handle.Dispose();
                handle = CreateFile(NativePath(path), 0x80000000, 7, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
                if (handle.IsInvalid) ThrowLastError();
                if (!IsSupported(handle)) { handle.Dispose(); return null; }
            }
            return new FileStream(handle, FileAccess.Read, 128 * 1024, false);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenMetadata(string path)
    {
        var handle = CreateFile(NativePath(path), 0x80, 7, IntPtr.Zero, 3,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); ThrowLastError(); }
        return handle;
    }

    public static (string Key, long? FreeBytes) VolumeSpace(string directory)
    {
        using var handle = OpenMetadata(directory);
        var final = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        string resolved = size > 0 && size < final.Capacity ? final.ToString() : directory;
        var mount = new StringBuilder(32768);
        string key = Path.GetPathRoot(resolved) ?? resolved;
        if (GetVolumePathName(NativePath(directory), mount, (uint)mount.Capacity))
        {
            var volume = new StringBuilder(128);
            if (GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, (uint)volume.Capacity)) key = volume.ToString();
        }
        long? free = GetDiskFreeSpaceEx(NativePath(directory), out ulong available, out _, out _)
            ? (long)Math.Min(available, long.MaxValue) : null;
        return (key.ToUpperInvariant(), free);
    }

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        string? root = Path.GetPathRoot(path);
        return root != null && GetDriveType(root) == 4;
    }

    public static bool IsTransientNetworkError(Exception exception, string folder)
    {
        if (!IsNetworkPath(folder) && !(exception is PathException pe && IsNetworkPath(pe.Path))) return false;
        Exception cause = exception.InnerException ?? exception;
        if (cause is not IOException && cause is not Win32Exception) return false;
        int code = cause is Win32Exception win ? win.NativeErrorCode : cause.HResult & 0xffff;
        return code is 53 or 54 or 55 or 59 or 64 or 121 or 1201 or 1222 or 1231 or 1232 or 1236 or 1237 or 2250;
    }

    private static void ThrowLastError()
    {
        int error = Marshal.GetLastWin32Error();
        throw new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode)] private static extern bool GetVolumePathName(string file, StringBuilder volume, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode)] private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)] private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
}
