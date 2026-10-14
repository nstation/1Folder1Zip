using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OneFolderOneZip.Core;

public static partial class TemporaryArchive
{
    [GeneratedRegex(@"^\.1folder1zip-v1-(\d+)-([0-9a-f]+)-([0-9a-f]{32})\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    public static bool IsOwnedName(string name) => NamePattern().IsMatch(name);

    internal static (string Path, FileStream Stream) Create(string directory)
    {
        using var process = Process.GetCurrentProcess();
        string name = $".1folder1zip-v1-{Environment.ProcessId}-{process.StartTime.ToUniversalTime().Ticks:x}-{Guid.NewGuid():N}.tmp";
        string path = System.IO.Path.Combine(directory, name);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024);
        try { File.SetAttributes(path, FileAttributes.Hidden); return (path, stream); }
        catch { stream.Dispose(); TryDelete(path); throw; }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static void Cleanup(string directory)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, ".1folder1zip-v1-*.tmp", SearchOption.TopDirectoryOnly))
            {
                try { CleanupOne(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void CleanupOne(string path)
    {
        var match = NamePattern().Match(System.IO.Path.GetFileName(path));
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int pid)
            || !long.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long ticks)) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks == ticks) return;
        }
        catch (ArgumentException) { } // The owner no longer exists.
        // Access denial when inspecting the process is handled by the caller: skip it.
        FileAttributes attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.Hidden) == 0 || (attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return;
        // Exclusive handle plus handle-based deletion avoids deleting a substituted path.
        using var handle = WindowsFileSystem.CreateFile(WindowsFileSystem.NativePath(path), 0x80010000, 0,
            IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) return;
        using var stream = new FileStream(handle, FileAccess.Read);
        Span<byte> signature = stackalloc byte[4];
        int count = stream.Read(signature);
        if (count != 0 && (count != 4 || signature[0] != 'P' || signature[1] != 'K'
            || !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6)))) return;
        int delete = 1;
        _ = SetFileInformationByHandle(handle, 4, ref delete, 4);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int kind, ref int info, uint size);
}
