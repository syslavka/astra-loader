using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NLCleaner.Services;

public static class GlassBackdrop
{
    // Documented DWM backdrop hints, available on Windows 11 build 22621+.
    // The OS may reduce transparency for power, accessibility, or user preferences.
    public static bool TryEnable(Window window)
    {
        if (Environment.OSVersion.Version.Build < 22621 || SystemParameters.HighContrast) return false;
        var handle = new WindowInteropHelper(window).Handle;
        var dark = 1;
        var round = 2;
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        var acrylic = 3;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        if (DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int)) != 0) return false;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        return true;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
