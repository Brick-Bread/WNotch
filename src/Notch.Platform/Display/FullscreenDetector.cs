using Notch.Platform.Interop;

namespace Notch.Platform.Display;

public static unsafe class FullscreenDetector
{
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow"];

    /// <summary>True when the foreground window is a fullscreen app (game, video, F11 browser) on <paramref name="display"/>.</summary>
    /// <param name="ownWindow">The notch window, which never counts.</param>
    public static bool IsFullscreenAppOn(DisplayInfo display, nint ownWindow)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0 || foreground == ownWindow || foreground == NativeMethods.GetShellWindow())
        {
            return false;
        }

        if (NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST) != display.Handle)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(foreground, out RECT rect) || !PixelRect.From(rect).Contains(display.Bounds))
        {
            return false;
        }

        // A maximized window also covers the monitor when the taskbar auto-hides, but it keeps its caption.
        long style = NativeMethods.GetWindowLongPtr(foreground, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION)
        {
            return false;
        }

        return !ShellClasses.Contains(GetClassName(foreground));
    }

    private static string GetClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = NativeMethods.GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, length);
    }
}
