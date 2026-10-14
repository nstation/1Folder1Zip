using System.Text;

namespace OneFolderOneZip.Core;

internal sealed record TreeEntry(string Path, string RelativePath, bool Directory, FileAttributes Attributes, long Length, DateTime Modified);

internal static class FileTree
{
    private static readonly EnumerationOptions Options = new()
    { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    public static IEnumerable<TreeEntry> Walk(string root, CancellationToken cancellation, Action skip)
    {
        var stack = new Stack<IEnumerator<FileSystemInfo>>();
        stack.Push(Enumerate(root).GetEnumerator());
        try
        {
            while (stack.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var current = stack.Peek();
                if (!current.MoveNext()) { current.Dispose(); stack.Pop(); continue; }
                FileSystemInfo info = current.Current;
                TreeEntry? entry = Inspect(root, info);
                if (entry == null) { skip(); continue; }
                yield return entry;
                if (entry.Directory) stack.Push(Enumerate(entry.Path).GetEnumerator());
            }
        }
        finally { foreach (var item in stack) item.Dispose(); }
    }

    private static IEnumerable<FileSystemInfo> Enumerate(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!WindowsFileSystem.IsSupported(path, directory.Attributes))
                throw new IOException("The directory became an unsupported reparse point.");
            // Sort only a single directory at a time; never store the whole tree.
            return directory.EnumerateFileSystemInfos("*", Options)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new PathException(path, error); }
    }

    private static TreeEntry? Inspect(string root, FileSystemInfo info)
    {
        try
        {
            FileAttributes attributes = info.Attributes;
            if (TemporaryArchive.IsOwnedName(info.Name) || !WindowsFileSystem.IsSupported(info.FullName, attributes)) return null;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            string relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            if (directory) relative += "/";
            return new(info.FullName, relative, directory, attributes, directory ? 0 : ((FileInfo)info).Length, info.LastWriteTime);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new PathException(info.FullName, error); }
    }

    public static ScanProgress Scan(string root, CancellationToken cancellation, Action<ScanProgress>? progress = null)
    {
        long files = 0, bytes = 0, skipped = 0, overhead = 22;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var entry in Walk(root, cancellation, () => skipped++))
        {
            if (!entry.Directory) { files++; bytes = checked(bytes + entry.Length); }
            // UTF-8 name appears in both headers; include ZIP64 and data descriptor allowance.
            overhead = checked(overhead + 160 + Encoding.UTF8.GetByteCount(entry.RelativePath) * 2L);
            if (clock.ElapsedMilliseconds >= 100)
            { progress?.Invoke(new(files, bytes, skipped, overhead)); clock.Restart(); }
        }
        var result = new ScanProgress(files, bytes, skipped, overhead);
        progress?.Invoke(result);
        return result;
    }
}
