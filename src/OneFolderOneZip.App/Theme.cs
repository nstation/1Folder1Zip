using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OneFolderOneZip.App;

internal static class Theme
{
    public static void Apply(Window? window = null)
    {
        bool dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException) { }
        var app = Application.Current;
        Set("BackgroundBrush", dark ? "#181C23" : "#F7F8FA");
        Set("SurfaceBrush", dark ? "#232A35" : "#FFFFFF");
        Set("TextBrush", dark ? "#F0F3F8" : "#192130");
        Set("MutedBrush", dark ? "#A5B2C5" : "#647083");
        Set("BorderBrush", dark ? "#384353" : "#DFE4ED");
        Set("AccentBrush", dark ? "#7BABFF" : "#2468D8");
        Set("HighlightBrush", dark ? "#263853" : "#EAF1FF");
        Set("ErrorBrush", dark ? "#FF9299" : "#C33744");
        if (window != null)
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) { int enabled = dark ? 1 : 0; _ = DwmSetWindowAttribute(handle, 20, ref enabled, 4); }
        }
        void Set(string name, string color) => app.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, uint size);
}
