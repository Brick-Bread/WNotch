using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Notch.Platform.Display;

namespace Notch.App.Shell;

/// <summary>Which side of the island lies flat against the screen edge, and so has square corners.</summary>
internal enum FlushEdge
{
    None,
    Top,
    Bottom,
}

/// <summary>
/// Blurs the desktop behind the island of a glass look. Windows cannot blur behind part of a layered window
/// such as the notch's, so this is a small window of its own: never activated, kept right under the notch's
/// window, cut to the island's outline with a window region, and given Windows' blur-behind accent. The notch
/// moves and reshapes it on every frame of the island's animation.
/// </summary>
/// <remarks>
/// The accent is undocumented (SetWindowCompositionAttribute), so it may do nothing on some builds of Windows;
/// the glass is then simply clear. A window region has no anti-aliasing, so it is cut a pixel inside the island,
/// whose own edge is drawn over it.
/// </remarks>
internal sealed class IslandBackdrop : IDisposable
{
    private HwndSource? _source;
    private PixelRect _bounds;
    private (int Width, int Height, int Radius, FlushEdge Edge) _outline;
    private bool _visible;

    /// <summary>Puts the blur under <paramref name="island"/>, in physical pixels, just below the window <paramref name="notch"/>.</summary>
    public void Show(nint notch, PixelRect island, int radius, FlushEdge edge)
    {
        if (island.Width <= 2 || island.Height <= 2)
        {
            Hide();
            return;
        }

        _source ??= Create();
        nint hwnd = _source.Handle;

        var outline = (island.Width, island.Height, radius, edge);
        if (outline != _outline)
        {
            _outline = outline;

            // Windows owns the region once it is set, and frees it.
            _ = SetWindowRgn(hwnd, CreateOutline(island.Width, island.Height, radius, edge), true);
        }

        if (!_visible || island != _bounds)
        {
            // Inserted after the notch's window, which puts it in the topmost band right under it.
            _ = SetWindowPos(hwnd, notch, island.Left, island.Top, island.Width, island.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
            _bounds = island;
            _visible = true;
        }
    }

    public void Hide()
    {
        if (_visible && _source is not null)
        {
            _ = ShowWindow(_source.Handle, SW_HIDE);
        }

        _visible = false;
    }

    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
        _visible = false;
    }

    private static HwndSource Create()
    {
        var parameters = new HwndSourceParameters("Notch backdrop")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT,
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1,
        };
        var source = new HwndSource(parameters);
        source.AddHook(WndProc);
        nint hwnd = source.Handle;

        // Nothing of WPF's is drawn here: the client area is left clear, and the frame extended over all of it,
        // so what shows is what Windows draws behind the window, which the accent makes a blur of the desktop.
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // Windows 11 would round the corners and draw a border of its own.
        int corners = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        int border = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // Plain blur rather than acrylic: acrylic lags behind a window that moves or resizes, as this one does every frame.
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        int size = Marshal.SizeOf<AccentPolicy>();
        nint pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = pointer, SizeOfData = size };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return source;
    }

    /// <summary>The island's outline, a pixel inside it. Along a flush edge the rounded corners lie outside the window.</summary>
    private static nint CreateOutline(int width, int height, int radius, FlushEdge edge)
    {
        int diameter = Math.Max(0, 2 * (radius - 1));
        return edge switch
        {
            FlushEdge.Top => CreateRoundRectRgn(1, -radius, width, height, diameter, diameter),
            FlushEdge.Bottom => CreateRoundRectRgn(1, 1, width, height + radius, diameter, diameter),
            _ => CreateRoundRectRgn(1, 1, width, height, diameter, diameter),
        };
    }

    // The island lies over this window, so the pointer should never reach it; if it does, it goes on through.
    private static nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_NCHITTEST:
                handled = true;
                return HTTRANSPARENT;
            case WM_MOUSEACTIVATE:
                handled = true;
                return MA_NOACTIVATE;
            default:
                return 0;
        }
    }

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const int SW_HIDE = 0;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const nint HTTRANSPARENT = -1;
    private const nint MA_NOACTIVATE = 3;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
