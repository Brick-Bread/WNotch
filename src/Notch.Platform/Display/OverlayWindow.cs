using Notch.Platform.Interop;

namespace Notch.Platform.Display;

/// <summary>Win32 styling and placement for the always-on-top notch window.</summary>
public static class OverlayWindow
{
    /// <summary>Keeps the window out of Alt+Tab and stops clicks on it from taking focus.</summary>
    public static void ApplyOverlayStyles(nint hwnd)
    {
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)style);
    }

    /// <summary>Turn off while something inside the notch needs keyboard input.</summary>
    public static void SetNoActivate(nint hwnd, bool noActivate)
    {
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        style = noActivate ? style | NativeMethods.WS_EX_NOACTIVATE : style & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)style);
    }

    public static PixelRect GetBounds(nint hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out RECT rect) ? PixelRect.From(rect) : default;

    public static void SetBounds(nint hwnd, PixelRect bounds) =>
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE);

    /// <summary>The window that has the keyboard, to hand it back to later with <see cref="SetForeground"/>.</summary>
    public static nint GetForeground() => NativeMethods.GetForegroundWindow();

    public static void SetForeground(nint hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    /// <summary>The pointer's position in physical screen pixels, or null when Windows will not say (e.g. on the lock screen).</summary>
    public static (int X, int Y)? GetCursorPosition() =>
        NativeMethods.GetCursorPos(out POINT point) ? (point.X, point.Y) : null;

    /// <summary>Other topmost windows can end up above the notch; this puts it back on top.</summary>
    public static void BringToTop(nint hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
}
