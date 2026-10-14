using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OneFolderOneZip.App;
using OneFolderOneZip.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            if (args.Contains("--english")) CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            else if (args.Contains("--japanese")) CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            var app = new App();
            app.InitializeComponent();
            var window = new MainWindow(false) { ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            window.Show();
            window.UpdateLayout();
            Assert(window.Title == "1Folder1Zip" && Math.Abs(window.Width - 450) < 1 && Math.Abs(window.Height - 350) < 1,
                $"Initial window: {window.Width} × {window.Height}.");
            Assert(((FrameworkElement)window.FindName("Welcome")).Visibility == Visibility.Visible, "Welcome visibility.");
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ui-work"));
            Directory.CreateDirectory(root);
            Save(window, Path.Combine(root, "welcome.png"));
            string source = Path.Combine(root, "UI 日本語😀"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "hello.txt"), "test");
            Drop(window, [source]);
            var queue = (JobQueue)typeof(MainWindow).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (queue.Snapshot().Jobs.Count == 0 || queue.Snapshot().IsActive)
            {
                Assert(DateTime.UtcNow < deadline, "UI processing timed out.");
                Pump();
            }
            Pump();
            Assert(queue.Snapshot().Jobs[0].State == JobState.Completed, queue.Snapshot().Jobs[0].Error?.Details ?? "Job failed.");
            Assert(((FrameworkElement)window.FindName("WorkArea")).Visibility == Visibility.Visible, "Progress visibility.");
            Save(window, Path.Combine(root, "result.png"));
            ((Button)window.FindName("DetailsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert(((ListBox)window.FindName("DetailsList")).Items.Count == 1, "Details rows.");
            Save(window, Path.Combine(root, "details.png"));
            ((Button)window.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(queue.Snapshot().Jobs.Count == 0, "Clear history.");
            Drop(window, [Path.Combine(source, "hello.txt")]);
            Pump(); Pump();
            Assert(((FrameworkElement)window.FindName("Notice")).Visibility == Visibility.Visible, "File-only drop guidance.");
            window.Close(); Pump();
            Console.WriteLine("PASS: WPF construction, folder drop, processing, details, clear and file-only guidance");
            Console.WriteLine(root);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Drop(Window window, string[] paths)
    {
        // WPF constructs DragEventArgs internally. Exercise the routed drop handler
        // with that same event shape without requiring a physical mouse in CI.
        var data = new DataObject(DataFormats.FileDrop, paths);
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [data, DragDropKeyStates.None, DragDropEffects.Copy, window, new Point(100, 100)], CultureInfo.InvariantCulture)!;
        args.RoutedEvent = DragDrop.DropEvent;
        window.RaiseEvent(args);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
