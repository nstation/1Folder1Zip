using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OneFolderOneZip.App;

internal sealed class CompletionNotification : IDisposable
{
    private const int Callback = 0x8001;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timeout;
    private NotifyData _data;
    private bool _shown;

    public CompletionNotification(Window owner)
    {
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(Hook);
        _data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = handle, Id = 1,
            CallbackMessage = Callback, Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            Tip = "1Folder1Zip", Info = "", Title = "1Folder1Zip" };
        _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timeout.Tick += (_, _) => Remove();
    }

    public void Show(string text)
    {
        Remove();
        // The icon exists only for the silent Windows notification. The application
        // stays in the taskbar; it never minimizes to or resides in the tray.
        _data.Flags = 1 | 2 | 4 | 8;
        _data.State = 1; _data.StateMask = 1; // NIS_HIDDEN
        if (!ShellNotifyIcon(0, ref _data)) return;
        _shown = true;
        _data.Flags = 0x10;
        _data.Info = text.Length > 255 ? text[..255] : text;
        _data.InfoFlags = 1 | 0x10 | 0x80; // Information, no sound, respect quiet time.
        _ = ShellNotifyIcon(1, ref _data);
        _timeout.Start();
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == Callback && (lparam.ToInt64() & 0xffff) is 0x403 or 0x404 or 0x405) Remove();
        return IntPtr.Zero;
    }
    private void Remove() { _timeout.Stop(); if (_shown) { _ = ShellNotifyIcon(2, ref _data); _shown = false; } }
    public void Dispose() { Remove(); _source.RemoveHook(Hook); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
}
