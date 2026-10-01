using Notch.Core.Shell;
using Notch.Platform.Interop;

namespace Notch.Platform.Display;

public static unsafe class FullscreenDetector
{
    // Walking the window stack races with windows being created and destroyed; this bounds it.
    private const int MaxWindows = 2000;

    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow"];

    /// <summary>
    /// True when a fullscreen app (game, video, F11 browser) is what shows on <paramref name="display"/>:
    /// either it has the keyboard, or it is the uppermost application window there, as when the
    /// user is working on another display while a game runs on this one.
    /// </summary>
    /// <param name="ownWindow">The notch window, which never counts.</param>
    public static bool IsFullscreenAppOn(DisplayInfo display, nint ownWindow)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground != 0 && foreground != ownWindow && CoversDisplay(foreground, display))
        {
            return true;
        }

        return FullscreenRule.IsCovered(WindowsOn(display, ownWindow));
    }

    /// <summary>The application windows that overlap the display, uppermost first.</summary>
    private static IEnumerable<StackedWindow> WindowsOn(DisplayInfo display, nint ownWindow)
    {
        int seen = 0;
        for (nint hwnd = NativeMethods.GetTopWindow(0); hwnd != 0 && seen < MaxWindows; hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT), seen++)
        {
            if (hwnd == ownWindow || !IsApplicationWindow(hwnd, out long exStyle))
            {
                continue;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out RECT rect) || !PixelRect.From(rect).Intersects(display.Bounds))
            {
                continue;
            }

            yield return new StackedWindow(CoversDisplay(hwnd, display), (exStyle & NativeMethods.WS_EX_TOPMOST) != 0);
        }
    }

    /// <summary>
    /// Leaves out what the user does not see as a window of an app: hidden, minimised and
    /// cloaked windows (other virtual desktops, suspended store apps), and the click-through
    /// overlays that graphics drivers and chat apps keep stretched over the whole screen.
    /// </summary>
    private static bool IsApplicationWindow(nint hwnd, out long exStyle)
    {
        exStyle = 0;
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT)) != 0)
        {
            return false;
        }

        return NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static bool CoversDisplay(nint hwnd, DisplayInfo display)
    {
        if (hwnd == NativeMethods.GetShellWindow())
        {
            return false;
        }

        if (NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST) != display.Handle)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out RECT rect) || !PixelRect.From(rect).Contains(display.Bounds))
        {
            return false;
        }

        // A maximized window also covers the monitor when the taskbar auto-hides, but it keeps its caption.
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION)
        {
            return false;
        }

        return !ShellClasses.Contains(GetClassName(hwnd));
    }

    private static string GetClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = NativeMethods.GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, length);
    }
}
