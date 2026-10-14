using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneFolderOneZip.Core;

public sealed record QueueSnapshot(IReadOnlyList<JobSnapshot> Jobs, TimeSpan Elapsed, int Parallelism)
{
    public bool IsActive => Jobs.Any(job => job.IsActive);
}

public sealed class JobQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<ZipJob> _jobs = [];
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _tasks = [];
    private readonly SemaphoreSlim _admission = new(1);
    private readonly SemaphoreSlim _scanners = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ArchiveEngine _engine = new();
    private readonly DiskBudget _budget = new();
    private readonly ResourceGovernor _governor = new();
    private readonly Stopwatch _clock = new();
    private readonly Task _pump;
    private long _nextId;
    private int _compressing;
    private readonly Dictionary<long, ScanProgress> _scans = [];
    private bool _disposed;
    private readonly HashSet<CancellationTokenSource> _submissions = [];

    public JobQueue() => _pump = Task.Run(PumpAsync);

    public QueueSnapshot Snapshot()
    {
        lock (_gate) return new(_jobs.Select(job => job.Snapshot).ToArray(), _clock.Elapsed, _governor.Limit);
    }

    public async Task<AddReport> AddPathsAsync(IEnumerable<string> paths)
    {
        using var submission = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _submissions.Add(submission); }
        // All path inspection runs off the dispatcher, including slow NAS identity queries.
        try { return await Task.Run(async () =>
        {
            await _admission.WaitAsync(submission.Token).ConfigureAwait(false);
            try
            {
                int added = 0, duplicate = 0, ignored = 0;
                foreach (string input in paths)
                {
                    submission.Token.ThrowIfCancellationRequested();
                    string folder;
                    string identity;
                    Exception? failure = null;
                    try
                    {
                        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Trim('"')));
                        FileAttributes attrs = File.GetAttributes(folder);
                        if ((attrs & FileAttributes.Directory) == 0 || !WindowsFileSystem.IsSupported(folder, attrs))
                        { ignored++; continue; }
                        identity = WindowsFileSystem.Identity(folder);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        if (error is FileNotFoundException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
                        { ignored++; continue; }
                        folder = Path.GetFullPath(input);
                        identity = "unresolved:" + folder;
                        failure = error;
                    }
                    ZipJob job;
                    lock (_gate)
                    {
                        submission.Token.ThrowIfCancellationRequested();
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        if (!_identities.Add(identity)) { duplicate++; continue; }
                        job = new(++_nextId, folder, identity);
                        _jobs.Add(job);
                        _clock.Start();
                        added++;
                        if (failure != null)
                        {
                            job.Update(state => state with { State = JobState.Error, Error = JobError.From(failure, folder) });
                            Finish(job);
                        }
                        else Track(Task.Run(() => ScanAsync(job)));
                    }
                }
                return new AddReport(added, duplicate, ignored);
            }
            finally { _admission.Release(); }
        }).ConfigureAwait(false); }
        finally { lock (_gate) _submissions.Remove(submission); }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var submission in _submissions) submission.Cancel();
            foreach (var job in _jobs.Where(job => job.Snapshot.IsActive)) job.Cancellation.Cancel();
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            if (_jobs.Any(job => job.Snapshot.IsActive)) return false;
            foreach (var job in _jobs) job.Cancellation.Dispose();
            _jobs.Clear(); _scans.Clear(); _clock.Reset();
            return true;
        }
    }

    private async Task ScanAsync(ZipJob job)
    {
        bool entered = false;
        try
        {
            await _scanners.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
            entered = true;
            string folder = job.Snapshot.Folder;
            string parent = Directory.GetParent(folder)?.FullName
                ?? throw new PathException(folder, new IOException("A drive/share root has no sibling output directory."));
            TemporaryArchive.Cleanup(parent);
            for (int retry = 0; ; retry++)
            {
                try
                {
                    ScanProgress scan = _engine.Scan(folder, job.Cancellation.Token, state => ScanUpdate(job, state));
                    lock (_gate)
                    {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        _scans[job.Snapshot.Id] = scan;
                        job.Update(state => state with { State = JobState.Waiting });
                    }
                    break;
                }
                catch (Exception error) when (retry < 3 && WindowsFileSystem.IsTransientNetworkError(error, folder))
                {
                    job.Update(state => state with { Retry = retry + 1, FileCount = 0, TotalBytes = 0 });
                    await Task.Delay(TimeSpan.FromSeconds(5), job.Cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { job.Update(state => state with { State = JobState.Cancelled }); Finish(job); }
        catch (Exception error) { job.Update(state => state with { State = JobState.Error, Error = JobError.From(error, state.Folder) }); Finish(job); }
        finally { if (entered) _scanners.Release(); }
    }

    private static void ScanUpdate(ZipJob job, ScanProgress scan) => job.Update(state => state with
    { FileCount = scan.Files, TotalBytes = scan.Bytes, SkippedCount = scan.Skipped });

    private async Task PumpAsync()
    {
        int ticks = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (++ticks % 4 == 0) _governor.Sample();
                lock (_gate)
                {
                    foreach (var job in _jobs.Where(job => job.Snapshot.State == JobState.Waiting).ToArray())
                    {
                        if (job.Cancellation.IsCancellationRequested)
                        { job.Update(state => state with { State = JobState.Cancelled }); Finish(job); continue; }
                        if (_compressing >= _governor.Limit) break;
                        _compressing++;
                        job.Update(state => state with { State = JobState.Compressing });
                        Track(Task.Run(() => CompressAsync(job)));
                    }
                }
                await Task.Delay(500, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CompressAsync(ZipJob job)
    {
        string folder = job.Snapshot.Folder;
        try
        {
            for (int retry = 0; ; retry++)
            {
                try
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    ScanProgress scan;
                    if (retry > 0)
                    {
                        scan = _engine.Scan(folder, job.Cancellation.Token, state => ScanUpdate(job, state));
                        lock (_gate) _scans[job.Snapshot.Id] = scan;
                    }
                    else { lock (_gate) scan = _scans[job.Snapshot.Id]; }
                    string parent = Directory.GetParent(folder)!.FullName;
                    using var reservation = await ReserveAsync(parent, DiskBudget.Estimate(scan), job.Cancellation.Token).ConfigureAwait(false);
                    CompressionResult result = CompressWithPriority(folder, job, reservation);
                    job.Update(state => state with { State = JobState.Completed, OutputPath = result.Path,
                        TotalBytes = result.SourceBytes, ProcessedBytes = result.SourceBytes, FileCount = result.Files,
                        ProcessedFiles = result.Files, ZipBytes = result.ZipBytes, SkippedCount = result.Skipped });
                    break;
                }
                catch (Exception error) when (retry < 3 && WindowsFileSystem.IsTransientNetworkError(error, folder))
                {
                    job.Update(state => state with { Retry = retry + 1, ProcessedBytes = 0, ProcessedFiles = 0 });
                    await Task.Delay(TimeSpan.FromSeconds(5), job.Cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { job.Update(state => state with { State = JobState.Cancelled }); }
        catch (Exception error) { job.Update(state => state with { State = JobState.Error, Error = JobError.From(error, folder) }); }
        finally { lock (_gate) _compressing--; Finish(job); }
    }

    private async Task<DiskBudget.Reservation> ReserveAsync(string parent, long estimate, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return _budget.Reserve(parent, estimate); }
            catch (ReservationConflictException) { await Task.Delay(500, cancellation).ConfigureAwait(false); }
        }
    }

    private CompressionResult CompressWithPriority(string folder, ZipJob job, DiskBudget.Reservation reservation)
    {
        ThreadPriority previous = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            // This synchronous worker owns the execution-state request on one OS thread.
            _ = SetThreadExecutionState(0x80000001);
            return _engine.Compress(folder, job.Cancellation.Token, progress => job.Update(state => state with
            { ProcessedBytes = progress.Bytes, ProcessedFiles = progress.Files, TotalBytes = Math.Max(state.TotalBytes, progress.Bytes) }), reservation);
        }
        finally { _ = SetThreadExecutionState(0x80000000); Thread.CurrentThread.Priority = previous; }
    }

    private void Finish(ZipJob job)
    {
        lock (_gate)
        {
            _identities.Remove(job.Identity);
            _scans.Remove(job.Snapshot.Id);
            if (!_jobs.Any(item => item.Snapshot.IsActive)) _clock.Stop();
        }
    }

    private void Track(Task task)
    {
        _tasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_gate) _tasks.Remove(completed); }, TaskScheduler.Default);
    }

    public async Task WaitForIdleAsync(CancellationToken cancellation = default)
    {
        while (Snapshot().IsActive) await Task.Delay(50, cancellation).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; CancelAll(); _lifetime.Cancel(); }
        await _pump.ConfigureAwait(false);
        Task[] tasks;
        lock (_gate) tasks = _tasks.ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _governor.Dispose();
        foreach (var job in _jobs) job.Cancellation.Dispose();
        _admission.Dispose(); _scanners.Dispose(); _lifetime.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
