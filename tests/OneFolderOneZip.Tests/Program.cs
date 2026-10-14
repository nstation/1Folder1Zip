using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OneFolderOneZip.Core;

internal static class Program
{
    private static readonly string TestBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-work"));
    private static readonly string Root = Path.Combine(TestBase, Guid.NewGuid().ToString("N"));
    private static readonly ArchiveEngine Engine = new();
    private static int _passed;

    private static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        try
        {
            Run("Unicode, contents, empty folders, metadata and stable ordering", Contents);
            Run("Empty source produces a valid empty ZIP", Empty);
            Run("Existing files and directories are never overwritten", Collision);
            await RunAsync("Simultaneous finalization selects unique names", AtomicRace);
            Run("Cancellation deletes incomplete temporary files", Cancellation);
            Run("Read errors fail the whole archive and retain originals", ReadFailure);
            Run("Long paths are archived", LongPath);
            Run("Junctions are excluded without entering their targets", Junction);
            Run("Read-sharing permits open files", SharedRead);
            Run("Disappearing input fails and removes its temporary", DisappearingInput);
            Run("Concurrent jobs reserve remaining disk space", SpaceReservations);
            Run("Cleanup deletes only identifiable dead-owner ZIP temporaries", Cleanup);
            Run("Local errors are not retried as network errors", RetryClassification);
            Run("Capacity and undefined ratio formatting", Formatting);
            await RunAsync("Queue deduplication, order, additional jobs and repeat submission", Queue);
            await RunAsync("One failed folder does not stop the queue", QueueFailure);
            await RunAsync("All-job cancellation permits later jobs", QueueCancellation);
            if (args.Contains("--stress"))
            {
                Run("100,000 files, ZIP64 entry count", ManyFiles);
                Run("ZIP64 source larger than 4 GiB", LargeFile);
            }
            Console.WriteLine($"PASS: {_passed} tests");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // Delete only the unique test directory under the verified test parent.
            string resolved = Path.GetFullPath(Root);
            if (resolved.StartsWith(TestBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                for (int attempt = 0; Directory.Exists(resolved); attempt++)
                {
                    try { Directory.Delete(resolved, true); }
                    catch (IOException) when (attempt < 9) { await Task.Delay(500); }
                }
            }
        }
    }

    private static string Folder(string name) { string path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Run(string name, Action test) { test(); _passed++; Console.WriteLine("PASS " + name); }
    private static async Task RunAsync(string name, Func<Task> test) { await test(); _passed++; Console.WriteLine("PASS " + name); }

    private static void Contents()
    {
        string folder = Folder("写真 📷");
        File.WriteAllText(Path.Combine(folder, "日本語😀.txt"), "こんにちは 🗂️", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(folder, "zero.bin"), []);
        File.WriteAllText(Path.Combine(folder, "existing.zip"), "ZIP is also a normal source file");
        File.WriteAllText(Path.Combine(folder, "shortcut.lnk"), "shortcut bytes");
        File.WriteAllText(Path.Combine(folder, "hidden.txt"), "hidden");
        File.SetAttributes(Path.Combine(folder, "hidden.txt"), FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "data.txt"), "nested");
        DateTime stamp = new(2020, 3, 4, 5, 6, 8);
        File.SetLastWriteTime(Path.Combine(folder, "zero.bin"), stamp);
        var scan = Engine.Scan(folder, default);
        var result = Engine.Compress(folder, default);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert(scan.Files == 6 && result.Files == 6, "Unexpected file count.");
        Assert(zip.Entries.Select(e => e.FullName).SequenceEqual(new[] { "empty/", "existing.zip", "hidden.txt", "nested/", "nested/data.txt", "shortcut.lnk", "zero.bin", "日本語😀.txt" }), "Unexpected ZIP order or root folder.");
        using var reader = new StreamReader(zip.GetEntry("日本語😀.txt")!.Open());
        Assert(reader.ReadToEnd() == "こんにちは 🗂️", "Unicode data changed.");
        Assert(zip.GetEntry("zero.bin")!.Length == 0 && zip.GetEntry("empty/") != null, "Empty entries lost.");
        Assert(zip.GetEntry("zero.bin")!.LastWriteTime.DateTime == stamp, "Timestamp changed.");
        Assert((zip.GetEntry("hidden.txt")!.ExternalAttributes & 7) == 7, "DOS attributes lost.");
        Assert((File.GetAttributes(result.Path) & FileAttributes.Hidden) == 0, "Output ZIP is hidden.");
        Assert(File.Exists(Path.Combine(folder, "日本語😀.txt")), "Original disappeared.");
    }

    private static void Empty()
    {
        var result = Engine.Compress(Folder("empty-root"), default);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert(zip.Entries.Count == 0 && result.ZipBytes == 22, "Invalid empty archive.");
    }

    private static void Collision()
    {
        string source = Folder("collision");
        File.WriteAllText(source + ".zip", "do not overwrite");
        Directory.CreateDirectory(Path.Combine(Root, "collision (1).zip"));
        var result = Engine.Compress(source, default);
        Assert(Path.GetFileName(result.Path) == "collision (2).zip", "Wrong suffix.");
        Assert(File.ReadAllText(source + ".zip") == "do not overwrite", "Existing output overwritten.");
    }

    private static async Task AtomicRace()
    {
        string directory = Folder("race");
        var tasks = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            string temp = Path.Combine(directory, $"{index}.tmp");
            File.WriteAllText(temp, $"content {index}");
            return ArchiveEngine.Commit(temp, directory, "same");
        })).ToArray();
        string[] outputs = await Task.WhenAll(tasks);
        Assert(outputs.Distinct().Count() == 24, "Output names collided.");
        Assert(outputs.Select(File.ReadAllText).Distinct().Count() == 24, "A file was overwritten.");
    }

    private static void Cancellation()
    {
        string folder = Folder("cancel");
        using (var file = File.Create(Path.Combine(folder, "big"))) file.SetLength(128 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        try { Engine.Compress(folder, cancellation.Token, _ => cancellation.Cancel()); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && !File.Exists(folder + ".zip"), "Cancelled ZIP was committed.");
        Assert(!Directory.EnumerateFiles(Root, ".1folder1zip-v1-*.tmp").Any(), "Temporary file leaked.");
        Assert(File.Exists(Path.Combine(folder, "big")), "Cancelled source disappeared.");
    }

    private static void ReadFailure()
    {
        string folder = Folder("read-failure");
        string file = Path.Combine(folder, "locked.txt"); File.WriteAllText(file, "locked");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { Engine.Compress(folder, default); throw new InvalidOperationException("A locked file was accepted."); }
        catch (PathException error) { Assert(error.Path == file, "Error path was lost."); }
        Assert(!File.Exists(folder + ".zip"), "Partial archive was committed.");
        Assert(!Directory.EnumerateFiles(Root, ".1folder1zip-v1-*.tmp").Any(), "Failed temporary leaked.");
    }

    private static void LongPath()
    {
        string folder = Folder("long"); string deep = folder;
        for (int i = 0; i < 12; i++) deep = Path.Combine(deep, $"nested-{i}-abcdefghijklmnop");
        Directory.CreateDirectory(deep); string file = Path.Combine(deep, "長い名前.txt");
        File.WriteAllText(file, "long path");
        var result = Engine.Compress(folder, default);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert(zip.Entries.Any(e => e.FullName.EndsWith("長い名前.txt", StringComparison.Ordinal)), "Long path lost.");
    }

    private static void SharedRead()
    {
        string folder = Folder("shared"); string file = Path.Combine(folder, "open.txt");
        File.WriteAllText(file, "open but readable");
        using var opened = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert(Engine.Compress(folder, default).Files == 1, "Readable open file was rejected.");
    }

    private static void DisappearingInput()
    {
        string source = Folder("disappear"); File.WriteAllText(Path.Combine(source, "file"), "source");
        try
        {
            Engine.Compress(source, default, progress => { if (progress.Bytes == 0) Directory.Delete(source, true); });
            throw new InvalidOperationException("Missing input was accepted.");
        }
        catch (PathException) { }
        Assert(!File.Exists(source + ".zip") && !Directory.EnumerateFiles(Root, ".1folder1zip-v1-*.tmp").Any(), "Partial output after missing input.");
    }

    private static void Junction()
    {
        string source = Folder("junction-source"), target = Folder("junction-target");
        File.WriteAllText(Path.Combine(target, "must-not-enter.txt"), "target");
        string junction = Path.Combine(source, "link"); Directory.CreateDirectory(junction);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] printable = Encoding.Unicode.GetBytes(target);
        byte[] data = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        BitConverter.GetBytes(0xa0000003u).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)printable.Length).CopyTo(data, 14);
        substitute.CopyTo(data, 16); printable.CopyTo(data, 18 + substitute.Length);
        using (var handle = CreateFile(junction, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert(!handle.IsInvalid && DeviceIoControl(handle, 0x000900a4, data, (uint)data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero), $"Junction creation: {Marshal.GetLastWin32Error()}");
        }
        try
        {
            var scan = Engine.Scan(source, default); var result = Engine.Compress(source, default);
            using var zip = ZipFile.OpenRead(result.Path);
            Assert(scan.Files == 0 && scan.Skipped == 1 && zip.Entries.Count == 0, "A junction target was entered.");
        }
        finally { Directory.Delete(junction); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    private static void SpaceReservations()
    {
        long free = 1000;
        var budget = new DiskBudget(_ => ("same-volume", free));
        using var first = budget.Reserve("a", 700);
        try { using var unexpected = budget.Reserve("b", 400); throw new InvalidOperationException("Oversubscription accepted."); }
        catch (ReservationConflictException) { }
        free = 600; first.UpdateWritten(400);
        using var second = budget.Reserve("b", 300);
        Assert(second != null, "Already written bytes counted twice.");
    }

    private static void Cleanup()
    {
        string folder = Folder("cleanup");
        string dead = Path.Combine(folder, $".1folder1zip-v1-2147483647-0-{Guid.NewGuid():N}.tmp");
        using (var zip = new ZipArchive(File.Create(dead), ZipArchiveMode.Create)) { }
        File.SetAttributes(dead, FileAttributes.Hidden);
        string foreign = Path.Combine(folder, $".1folder1zip-v1-2147483647-0-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(foreign, "not a ZIP"); File.SetAttributes(foreign, FileAttributes.Hidden);
        using var process = Process.GetCurrentProcess();
        string active = Path.Combine(folder, $".1folder1zip-v1-{Environment.ProcessId}-{process.StartTime.ToUniversalTime().Ticks:x}-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(active, []); File.SetAttributes(active, FileAttributes.Hidden);
        TemporaryArchive.Cleanup(folder);
        Assert(!File.Exists(dead) && File.Exists(foreign) && File.Exists(active), "Unsafe temporary cleanup.");
    }

    private static void RetryClassification()
    {
        var transient = new IOException("network", unchecked((int)0x80070040));
        Assert(WindowsFileSystem.IsTransientNetworkError(transient, @"\\server\share\folder"), "Network disconnect not classified.");
        Assert(!WindowsFileSystem.IsTransientNetworkError(transient, Root), "Local error classified as network.");
        Assert(!WindowsFileSystem.IsTransientNetworkError(new UnauthorizedAccessException(), @"\\server\share\folder"), "Access denial classified transient.");
    }
    private static void Formatting()
    {
        Assert(DisplayFormat.Bytes(1024) == "1 KB" && DisplayFormat.Bytes(1536) == "1.5 KB", "Capacity formatting.");
        Assert(DisplayFormat.Ratio(22, 0) == "—", "Undefined ratio.");
    }

    private static async Task Queue()
    {
        string first = Folder("queue-a"), second = Folder("queue-b");
        using (var file = File.Create(Path.Combine(first, "large"))) file.SetLength(32 * 1024 * 1024);
        await using var queue = new JobQueue();
        var report = await queue.AddPathsAsync([first, first + Path.DirectorySeparatorChar, Path.Combine(first, "large")]);
        Assert(report.Added == 1 && report.Duplicate == 1 && report.Ignored == 1, "Admission deduplication.");
        await queue.AddPathsAsync([second]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await queue.WaitForIdleAsync(timeout.Token);
        var snapshots = queue.Snapshot().Jobs;
        Assert(snapshots.Count == 2 && snapshots[0].Folder == first && snapshots[1].Folder == second, "Registration order.");
        Assert(snapshots.All(job => job.State == JobState.Completed), string.Join("\n", snapshots.Select(job => job.Error?.Details)));
        Assert((await queue.AddPathsAsync([first])).Added == 1, "Resubmission after completion.");
        await queue.WaitForIdleAsync(timeout.Token);
        Assert(queue.Snapshot().Jobs.Count == 3 && queue.Clear() && queue.Snapshot().Jobs.Count == 0, "History and clear.");
    }

    private static async Task QueueFailure()
    {
        string bad = Folder("queue-bad"), good = Folder("queue-good");
        string file = Path.Combine(bad, "locked"); File.WriteAllText(file, "locked");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var queue = new JobQueue();
        await queue.AddPathsAsync([bad, good]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await queue.WaitForIdleAsync(timeout.Token);
        var jobs = queue.Snapshot().Jobs;
        Assert(jobs[0].State == JobState.Error && jobs[1].State == JobState.Completed, "Failure stopped other folders.");
        Assert(jobs[0].Error?.Path == file && jobs[0].Retry == 0, "Read error detail/retry.");
    }

    private static async Task QueueCancellation()
    {
        string folder = Folder("queue-cancel");
        using (var file = File.Create(Path.Combine(folder, "large"))) file.SetLength(256 * 1024 * 1024);
        await using var queue = new JobQueue();
        await queue.AddPathsAsync([folder]); queue.CancelAll();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await queue.WaitForIdleAsync(timeout.Token);
        Assert(queue.Snapshot().Jobs[0].State == JobState.Cancelled, "All-job cancellation.");
        await queue.AddPathsAsync([Folder("after-cancel")]); await queue.WaitForIdleAsync(timeout.Token);
        Assert(queue.Snapshot().Jobs[1].State == JobState.Completed, "New jobs cancelled by old token.");
    }

    private static void ManyFiles()
    {
        string folder = Folder("zip64-many");
        for (int i = 0; i < 100000; i++)
        {
            string group = Path.Combine(folder, (i / 1000).ToString("000")); Directory.CreateDirectory(group);
            using var file = File.Create(Path.Combine(group, i.ToString("000000")));
        }
        var scan = Engine.Scan(folder, default); Assert(scan.Files == 100000, "Scan count.");
        var result = Engine.Compress(folder, default); using var zip = ZipFile.OpenRead(result.Path);
        Assert(zip.Entries.Count == 100100, "ZIP64 entry count truncated.");
        Assert(result.Files == 100000, "Compression count.");
    }
    private static void LargeFile()
    {
        string folder = Folder("zip64-large"); long length = (1L << 32) + 1;
        using (var file = File.Create(Path.Combine(folder, "large.bin"))) file.SetLength(length);
        var result = Engine.Compress(folder, default); using var zip = ZipFile.OpenRead(result.Path);
        Assert(zip.Entries[0].Length == length && result.SourceBytes == length, "ZIP64 size truncated.");
    }
}
