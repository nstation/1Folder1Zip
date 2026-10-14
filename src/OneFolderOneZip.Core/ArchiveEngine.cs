using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace OneFolderOneZip.Core;

public sealed class ArchiveEngine
{
    public ScanProgress Scan(string folder, CancellationToken cancellation, Action<ScanProgress>? progress = null)
        => FileTree.Scan(folder, cancellation, progress);

    public CompressionResult Compress(string folder, CancellationToken cancellation,
        Action<CompressionProgress>? progress = null, DiskBudget.Reservation? reservation = null)
    {
        string? temporary = null;
        DateTime started = DateTime.Now;
        long bytes = 0, files = 0, skipped = 0;
        string parent = Directory.GetParent(folder)?.FullName
            ?? throw new PathException(folder, new IOException("A drive/share root cannot be archived beside itself."));
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            var created = TemporaryArchive.Create(parent);
            temporary = created.Path;
            using (var output = created.Stream)
            {
                progress?.Invoke(new(0, 0));
                cancellation.ThrowIfCancellationRequested();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    byte[] buffer = new byte[128 * 1024];
                    var clock = Stopwatch.StartNew();
                    foreach (var item in FileTree.Walk(folder, cancellation, () => skipped++))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            using var input = item.Directory ? null : WindowsFileSystem.OpenRead(item.Path);
                            if (!item.Directory && input == null) { skipped++; continue; }
                            var entry = zip.CreateEntry(item.RelativePath, item.Directory ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                            entry.LastWriteTime = ZipTime(item.Modified);
                            entry.ExternalAttributes = (int)(item.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden
                                | FileAttributes.System | FileAttributes.Directory | FileAttributes.Archive));
                            if (!item.Directory)
                            {
                                using var target = entry.Open();
                                int read;
                                while ((read = input!.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    target.Write(buffer, 0, read);
                                    bytes = checked(bytes + read);
                                    if (clock.ElapsedMilliseconds >= 100)
                                    { progress?.Invoke(new(bytes, files)); reservation?.UpdateWritten(output.Position); clock.Restart(); }
                                }
                                files++;
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                        { throw new PathException(item.Path, error); }
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                output.Flush(true);
                reservation?.UpdateWritten(output.Length);
            }
            cancellation.ThrowIfCancellationRequested();
            File.SetCreationTime(temporary, started);
            File.SetLastWriteTime(temporary, DateTime.Now);
            long zipBytes = new FileInfo(temporary).Length;
            string final = Commit(temporary, parent, Path.GetFileName(folder), cancellation);
            temporary = null;
            // Keep the temporary hidden until the atomic rename has succeeded.
            // Completed output is never deleted if its metadata cannot be updated.
            try { File.SetAttributes(final, FileAttributes.Normal); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new PathException(final, error); }
            progress?.Invoke(new(bytes, files));
            return new(final, bytes, files, zipBytes, skipped);
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && error is not PathException)
        { throw new PathException(temporary ?? folder, error); }
        finally { if (temporary != null) TemporaryArchive.TryDelete(temporary); }
    }

    public static string Commit(string temporary, string directory, string name, CancellationToken cancellation = default)
    {
        for (long suffix = 0; ; suffix++)
        {
            cancellation.ThrowIfCancellationRequested();
            string candidate = Path.Combine(directory, suffix == 0 ? $"{name}.zip" : $"{name} ({suffix}).zip");
            try { File.Move(temporary, candidate, false); return candidate; }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && (File.Exists(candidate) || Directory.Exists(candidate))) { }
        }
    }

    private static DateTimeOffset ZipTime(DateTime value)
    {
        if (value.Year < 1980) value = new DateTime(1980, 1, 1);
        if (value.Year > 2107) value = new DateTime(2107, 12, 31, 23, 59, 58);
        return new DateTimeOffset(value);
    }
}
