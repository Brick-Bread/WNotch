using System.Runtime.InteropServices;
using Notch.Platform.Interop;

namespace Notch.Platform.Display;

/// <summary>A rectangle in physical screen pixels (virtual-desktop coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(PixelRect other) =>
        Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;

    /// <summary>True when the two share any area; touching edges do not count.</summary>
    public bool Intersects(PixelRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

    internal static PixelRect From(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}

/// <param name="Scale">Physical pixels per WPF device-independent pixel (1.0 at 96 DPI).</param>
public sealed record DisplayInfo(nint Handle, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary);

public static unsafe class Displays
{
    public static IReadOnlyList<DisplayInfo> GetAll()
    {
        var displays = new List<DisplayInfo>();
        GCHandle handle = GCHandle.Alloc(displays);
        try
        {
            NativeMethods.EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return displays;
    }

    public static DisplayInfo GetPrimary()
    {
        IReadOnlyList<DisplayInfo> all = GetAll();
        return all.FirstOrDefault(d => d.IsPrimary)
            ?? all.FirstOrDefault()
            ?? throw new InvalidOperationException("No displays are attached.");
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var displays = (List<DisplayInfo>)GCHandle.FromIntPtr(data).Target!;

        var info = new MONITORINFO { Size = (uint)sizeof(MONITORINFO) };
        if (NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            double scale = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0
                ? dpi / 96.0
                : 1.0;

            displays.Add(new DisplayInfo(
                monitor,
                PixelRect.From(info.Monitor),
                PixelRect.From(info.Work),
                scale,
                (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
        }

        return 1;
    }
}
