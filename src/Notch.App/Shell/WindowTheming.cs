using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Notch.App.Shell;

/// <summary>
/// Gives an ordinary window's title bar, which Windows draws, the dark or light look of the notch's theme, so it
/// matches the window under it, and keeps it matching when the theme changes.
/// </summary>
internal static class WindowTheming
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void Follow(Window window)
    {
        void Apply()
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != 0)
            {
                int dark = ThemeManager.IsLight ? 0 : 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
        }

        window.SourceInitialized += (_, _) => Apply();
        ThemeManager.Changed += Apply;
        window.Closed += (_, _) => ThemeManager.Changed -= Apply;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
