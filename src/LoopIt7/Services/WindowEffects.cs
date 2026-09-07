using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LoopIt7.Services;

/// <summary>
/// Opts the window into the Windows 11 Mica backdrop and the dark title bar. Both calls are
/// no ops on builds that do not know the attributes, so the solid brush underneath stays.
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>DWMSBT_MAINWINDOW, the Mica backdrop.</summary>
    private const int BackdropMica = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkMica(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int dark = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        int backdrop = BackdropMica;
        int result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        if (result == 0)
        {
            // Mica only shows through where the window is not painted, so hand the composition
            // the transparent root and let the app surface sit on top of it.
            window.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(handle) is { } source)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
        }
    }
}
