using System.Diagnostics;

namespace OneFolderOneZip.Core;

public enum JobState { Scanning, Waiting, Compressing, Completed, Error, Cancelled }

public sealed record JobError(string Path, string Message, string Details, int Code)
{
    public static JobError From(Exception error, string fallback) => new(
        error is PathException pe ? pe.Path : fallback,
        error.InnerException?.Message ?? error.Message,
        error.ToString(), error.InnerException?.HResult ?? error.HResult);
}

public sealed class PathException(string path, Exception inner) : IOException(inner.Message, inner)
{
    public string Path { get; } = path;
}

public sealed record JobSnapshot(long Id, string Folder, JobState State, long FileCount,
    long TotalBytes, long ProcessedBytes, long ProcessedFiles, long SkippedCount,
    string? OutputPath, long ZipBytes, TimeSpan Elapsed, int Retry, JobError? Error)
{
    public bool IsActive => State is JobState.Scanning or JobState.Waiting or JobState.Compressing;
    public double Progress => State == JobState.Completed ? 100 : TotalBytes > 0
        ? Math.Clamp(100d * ProcessedBytes / TotalBytes, 0, 99.9) : 0;
}

internal sealed class ZipJob(long id, string folder, string identity)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private JobSnapshot _snapshot = new(id, folder, JobState.Scanning, 0, 0, 0, 0, 0, null, 0, TimeSpan.Zero, 0, null);
    public string Identity { get; } = identity;
    public CancellationTokenSource Cancellation { get; } = new();
    public JobSnapshot Snapshot { get { lock (_gate) return _snapshot with { Elapsed = _clock.Elapsed }; } }
    public void Update(Func<JobSnapshot, JobSnapshot> change)
    {
        lock (_gate)
        {
            _snapshot = change(_snapshot);
            if (!_snapshot.IsActive) _clock.Stop();
        }
    }
}

public sealed record ScanProgress(long Files, long Bytes, long Skipped, long EntryOverhead);
public sealed record CompressionProgress(long Bytes, long Files);
public sealed record CompressionResult(string Path, long SourceBytes, long Files, long ZipBytes, long Skipped);
public sealed record AddReport(int Added, int Duplicate, int Ignored);

public static class DisplayFormat
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Abs((double)bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{(bytes < 0 ? -value : value):0.##} {units[unit]}";
    }
    public static string Duration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    public static string Ratio(long zip, long source) => source == 0 ? "—" : $"{100d * zip / source:0.##}%";
}
