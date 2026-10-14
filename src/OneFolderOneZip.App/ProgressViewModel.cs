using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OneFolderOneZip.Core;

namespace OneFolderOneZip.App;

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void AllChanged() => Changed(string.Empty);
}

internal sealed class JobRow(JobSnapshot snapshot) : Observable
{
    public JobSnapshot Job { get; private set; } = snapshot;
    public string Name => System.IO.Path.GetFileName(Job.Folder);
    public string Folder => Job.Folder;
    public string? OutputPath => Job.OutputPath;
    public string Status => Strings.State(Job.State) + (Job.Retry > 0 && Job.IsActive ? $" ({Job.Retry}/3)" : "");
    public double Progress => Job.Progress;
    public string Description => Job.State == JobState.Completed ? System.IO.Path.GetFileName(Job.OutputPath)!
        : Job.Error?.Message ?? $"{Job.FileCount:N0} {Strings.Pick("ファイル", "files")} · {DisplayFormat.Bytes(Job.ProcessedBytes)} / {DisplayFormat.Bytes(Job.TotalBytes)}";
    public string Metrics => $"{DisplayFormat.Duration(Job.Elapsed)} · " + (Job.State == JobState.Completed
        ? $"{DisplayFormat.Bytes(Job.TotalBytes)} → {DisplayFormat.Bytes(Job.ZipBytes)} · {DisplayFormat.Ratio(Job.ZipBytes, Job.TotalBytes)}"
        : DisplayFormat.Bytes(Job.TotalBytes)) + (Job.SkippedCount > 0 ? $" · {Strings.Pick("除外", "Skipped")} {Job.SkippedCount:N0}" : "");
    public string ErrorVisibility => Job.Error == null ? "Collapsed" : "Visible";
    public string ErrorButtonText => Strings.Pick("エラー詳細", "Error details");
    public void Update(JobSnapshot snapshot) { Job = snapshot; AllChanged(); }
}

internal sealed class ProgressViewModel : Observable
{
    public ObservableCollection<JobRow> Rows { get; } = [];
    public string Heading { get; private set; } = "";
    public string Counts { get; private set; } = "";
    public string PercentText { get; private set; } = "";
    public string Amounts { get; private set; } = "";
    public string Timing { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public double Progress { get; private set; }
    private double _speed;
    private long _previousBytes;
    private TimeSpan _previousElapsed;

    public void Update(QueueSnapshot state)
    {
        var jobs = state.Jobs;
        if (jobs.Count < Rows.Count) Rows.Clear();
        for (int index = 0; index < jobs.Count; index++)
        { if (index == Rows.Count) Rows.Add(new(jobs[index])); else Rows[index].Update(jobs[index]); }
        long total = jobs.Sum(job => job.TotalBytes);
        long processed = jobs.Sum(job => Math.Min(job.ProcessedBytes, job.TotalBytes));
        int done = jobs.Count(job => !job.IsActive);
        int success = jobs.Count(job => job.State == JobState.Completed);
        int failed = jobs.Count(job => job.State == JobState.Error);
        int cancelled = jobs.Count(job => job.State == JobState.Cancelled);
        bool scanning = jobs.Any(job => job.State == JobState.Scanning);
        Progress = !state.IsActive && jobs.Count > 0 ? 100 : total > 0 ? Math.Min(99.9, 100d * processed / total) : 0;
        Heading = state.IsActive ? Strings.Pick("フォルダーを圧縮しています", "Compressing folders") : Strings.Pick("処理が完了しました", "All jobs finished");
        Counts = $"{done} / {jobs.Count} {Strings.Pick("完了", "finished")}";
        PercentText = $"{Progress:0}%";
        Amounts = $"{DisplayFormat.Bytes(processed)} / {DisplayFormat.Bytes(total)}";
        double seconds = (state.Elapsed - _previousElapsed).TotalSeconds;
        if (seconds > 0 && processed >= _previousBytes)
        {
            double sample = (processed - _previousBytes) / seconds;
            _speed = _speed == 0 ? sample : _speed * 0.85 + sample * 0.15;
        }
        else if (processed < _previousBytes) _speed = 0;
        _previousBytes = processed; _previousElapsed = state.Elapsed;
        string remaining = !scanning && _speed > 1 && state.Elapsed.TotalSeconds >= 2
            ? DisplayFormat.Duration(TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2, Math.Max(0, total - processed) / _speed)))
            : Strings.Pick("計算中...", "Calculating...");
        Timing = $"{Strings.Pick("経過", "Elapsed")} {DisplayFormat.Duration(state.Elapsed)}"
            + (state.IsActive ? $"  ·  {Strings.Pick("残り", "Remaining")} {remaining}" : "");
        if (state.IsActive)
        {
            long scanned = jobs.Where(job => job.State == JobState.Scanning).Sum(job => job.FileCount);
            long scannedBytes = jobs.Where(job => job.State == JobState.Scanning).Sum(job => job.TotalBytes);
            Summary = scanning ? Strings.Pick($"スキャン済み {scanned:N0} ファイル · {DisplayFormat.Bytes(scannedBytes)}", $"Scanned {scanned:N0} files · {DisplayFormat.Bytes(scannedBytes)}")
                : Strings.Pick("フォルダーは引き続きドロップできます", "You can keep dropping folders");
        }
        else
        {
            long source = jobs.Where(job => job.State == JobState.Completed).Sum(job => job.TotalBytes);
            long zip = jobs.Sum(job => job.ZipBytes);
            Summary = Strings.Pick($"成功 {success} · 失敗 {failed} · キャンセル {cancelled}", $"Success {success} · Failed {failed} · Cancelled {cancelled}")
                + $"\n{DisplayFormat.Bytes(source)} → {DisplayFormat.Bytes(zip)} · {Strings.Pick("削減", "Saved")} {DisplayFormat.Bytes(source - zip)} · {DisplayFormat.Ratio(zip, source)}";
        }
        AllChanged();
    }

    public void Reset() { Rows.Clear(); _speed = 0; _previousBytes = 0; _previousElapsed = TimeSpan.Zero; }
}
