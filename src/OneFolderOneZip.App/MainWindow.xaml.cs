using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using OneFolderOneZip.Core;

namespace OneFolderOneZip.App;

public partial class MainWindow : Window
{
    private readonly JobQueue _queue = new();
    private readonly ProgressViewModel _view = new();
    private readonly DispatcherTimer _timer;
    private CompletionNotification? _notification;
    private bool _autoClose, _wasActive, _details, _closing, _allowClose, _shutdown;
    private int _pending;
    private DateTime _noticeUntil;

    public MainWindow(bool autoClose)
    {
        InitializeComponent();
        DataContext = _view;
        _autoClose = autoClose;
        DropTitle.Text = Strings.Pick("フォルダーをドロップ", "Drop your folders here");
        DropSubtitle.Text = Strings.Pick("1フォルダーにつき、1つのZIP", "One folder, one ZIP");
        IdleHint.Text = Strings.Pick("元データは残ります", "Originals are kept");
        CancelButton.Content = Strings.Pick("キャンセル", "Cancel");
        ClearButton.Content = Strings.Pick("クリア", "Clear");
        InfoButton.ToolTip = Strings.Pick("アプリについて", "About");
        UpdateDetailsLabel();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        SourceInitialized += (_, _) =>
        {
            Theme.Apply(this);
            _notification = new(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
        };
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Closed += (_, _) => { _timer.Stop(); _notification?.Dispose(); SystemEvents.UserPreferenceChanged -= OnPreferenceChanged; };
    }

    public async void ReceivePaths(string[] paths, bool forwarded)
    {
        if (_closing) return;
        if (forwarded)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show(); Activate();
            // A launch without folders explicitly asks to see the existing results.
            if (paths.Length == 0) _autoClose = false;
        }
        if (paths.Length == 0) return;
        _pending++;
        _wasActive = true;
        try
        {
            AddReport report = await _queue.AddPathsAsync(paths);
            if (report.Added == 0)
                ShowNotice(report.Duplicate > 0 ? Strings.Pick("処理中のフォルダーは追加済みです。", "These folders are already in the queue.")
                    : Strings.Pick("フォルダーをドロップしてください。通常ファイルとリンクは対象外です。", "Drop folders. Regular files and links are ignored."));
            if (report.Added == 0 && _queue.Snapshot().Jobs.Count == 0) _autoClose = false;
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        finally { _pending--; Refresh(); }
    }

    private void Refresh()
    {
        QueueSnapshot state = _queue.Snapshot();
        bool active = state.IsActive || _pending > 0;
        bool hasJobs = state.Jobs.Count > 0;
        Welcome.Visibility = hasJobs ? Visibility.Collapsed : Visibility.Visible;
        WorkArea.Visibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;
        JobActions.Visibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Visibility = hasJobs ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = state.IsActive;
        ClearButton.Visibility = hasJobs && !active ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = !active;
        if (hasJobs) _view.Update(state);
        TaskbarItemInfo.ProgressState = active
            ? (state.Jobs.All(job => job.State == JobState.Scanning) ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal)
            : state.Jobs.Any(job => job.State == JobState.Error) ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
        TaskbarItemInfo.ProgressValue = _view.Progress / 100;
        if (DateTime.UtcNow >= _noticeUntil) Notice.Visibility = Visibility.Collapsed;
        if (!active && _wasActive && hasJobs)
        {
            if (_autoClose && state.Jobs.All(job => job.State == JobState.Completed))
            { _allowClose = true; Close(); return; }
            if (WindowState == WindowState.Minimized)
                _notification?.Show(Strings.Pick($"完了 {state.Jobs.Count(job => job.State == JobState.Completed)} 件 · エラー {state.Jobs.Count(job => job.State == JobState.Error)} 件", $"Completed {state.Jobs.Count(job => job.State == JobState.Completed)} · Errors {state.Jobs.Count(job => job.State == JobState.Error)}"));
        }
        _wasActive = active;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool valid = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = valid && !_closing ? DragDropEffects.Copy : DragDropEffects.None;
        DropHighlight.BorderBrush = valid ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        e.Handled = true;
    }
    private void OnDragLeave(object sender, DragEventArgs e) => DropHighlight.BorderBrush = Brushes.Transparent;
    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.BorderBrush = Brushes.Transparent;
        _autoClose = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) ReceivePaths(paths, false);
        e.Handled = true;
    }
    private void OnCancel(object sender, RoutedEventArgs e) { _queue.CancelAll(); _autoClose = false; }
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_pending > 0 || !_queue.Clear()) return;
        _view.Reset(); _details = false; DetailsList.Visibility = Visibility.Collapsed;
        _autoClose = false; _wasActive = false; UpdateDetailsLabel(); Refresh();
    }
    private void OnDetails(object sender, RoutedEventArgs e)
    {
        _details = !_details;
        DetailsList.Visibility = _details ? Visibility.Visible : Visibility.Collapsed;
        if (_details && Height < 520) Height = 520;
        UpdateDetailsLabel();
    }
    private void UpdateDetailsLabel() => DetailsButton.Content = _details
        ? Strings.Pick("⌃ 詳細を隠す", "⌃ Hide details") : Strings.Pick("⌄ 詳細を表示", "⌄ Show details");

    private void OnInfo(object sender, RoutedEventArgs e)
    {
        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        ShowTextDialog(Strings.Pick("アプリについて", "About"), $"1Folder1Zip\nVersion {version}\n\n" + Strings.Pick("開発者: @nstation\n\nフォルダーごとにZIPを作成するポータブルアプリ。\n設定・履歴・ログは保存しません。", "Developer: @nstation\n\nA portable app that creates one ZIP per folder.\nNo settings, history, or logs are saved."), false);
    }
    private void OnError(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: JobRow row } && row.Job.Error is JobError error)
        {
            var text = new StringBuilder().AppendLine(row.Folder).AppendLine()
                .AppendLine(error.Message).AppendLine()
                .AppendLine(Strings.Pick("原因ファイル: ", "Affected path: ") + error.Path)
                .AppendLine($"HRESULT: 0x{error.Code:X8}").AppendLine().AppendLine(error.Details).ToString();
            ShowTextDialog(Strings.Pick("エラー詳細", "Error details"), text, true);
        }
    }
    private void ShowTextDialog(string title, string text, bool copy)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 570, Height = copy ? 410 : 290,
            MinWidth = 350, MinHeight = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var body = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        body.SetResourceReference(ForegroundProperty, "TextBrush");
        grid.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        if (copy)
        {
            var copyButton = new Button { Content = Strings.Pick("コピー", "Copy"), Margin = new Thickness(0, 0, 8, 0) };
            copyButton.Click += (_, _) => { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { ShowNotice(Strings.Pick("クリップボードを使用できません。", "The clipboard is unavailable.")); } };
            buttons.Children.Add(copyButton);
        }
        var close = new Button { Content = Strings.Pick("閉じる", "Close"), IsCancel = true };
        close.Click += (_, _) => dialog.Close(); buttons.Children.Add(close);
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons); dialog.Content = grid;
        dialog.SourceInitialized += (_, _) => Theme.Apply(dialog);
        dialog.ShowDialog();
    }

    private void ShowNotice(string text) { Notice.Text = text; Notice.Visibility = Visibility.Visible; _noticeUntil = DateTime.UtcNow.AddSeconds(8); }
    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => Theme.Apply(this));
    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0011 || message == 0x0016) CancelForShutdown();
        return IntPtr.Zero;
    }
    public void CancelForShutdown()
    {
        _shutdown = true; _queue.CancelAll();
        // OS shutdown has a bounded cleanup window. Never hold it indefinitely for a NAS.
        try { _queue.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _shutdown) return;
        e.Cancel = true;
        if (_closing) return;
        if (_queue.Snapshot().IsActive || _pending > 0)
        {
            if (!ConfirmExit()) return;
        }
        _closing = true; _queue.CancelAll();
        await _queue.DisposeAsync();
        _allowClose = true; Close();
    }

    private bool ConfirmExit()
    {
        bool confirmed = false;
        var dialog = new Window { Owner = this, Title = "1Folder1Zip", Width = 370, Height = 175, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = Strings.Pick("処理を中止して終了しますか？\n完成済みのZIPは残ります。", "Cancel all jobs and exit?\nCompleted ZIPs will be kept."), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var back = new Button { Content = Strings.Pick("戻る", "Back"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) => dialog.Close();
        var exit = new Button { Content = Strings.Pick("中止して終了", "Cancel and exit") };
        exit.Click += (_, _) => { confirmed = true; dialog.Close(); };
        buttons.Children.Add(back); buttons.Children.Add(exit); panel.Children.Add(buttons); dialog.Content = panel;
        dialog.SourceInitialized += (_, _) => Theme.Apply(dialog); dialog.ShowDialog();
        return confirmed;
    }
}
