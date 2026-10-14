using System.IO;
using System.Windows;
using OneFolderOneZip.Core;

namespace OneFolderOneZip.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance();
        if (!_instance.IsPrimary)
        {
            try { await _instance.ForwardAsync(e.Args); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            { MessageBox.Show(Strings.Pick("既存のウィンドウへの転送に失敗しました。", "Could not forward to the running window.") + "\n" + error.Message, "1Folder1Zip", MessageBoxButton.OK, MessageBoxImage.Warning); }
            Shutdown(); return;
        }
        Theme.Apply();
        _ = Task.Run(() => TemporaryArchive.Cleanup(AppContext.BaseDirectory));
        var window = new MainWindow(e.Args.Length > 0);
        MainWindow = window;
        _instance.Listen(paths => window.Dispatcher.BeginInvoke(() => window.ReceivePaths(paths, true)));
        window.Show();
        if (e.Args.Length > 0) window.ReceivePaths(e.Args, false);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.CancelForShutdown();
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
