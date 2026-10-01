using System.Windows.Controls;
using H.NotifyIcon;
using Drawing = System.Drawing;

namespace Notch.App.Shell;

internal static class TrayIcon
{
    public static TaskbarIcon Create(Action quit)
    {
        var quitItem = new MenuItem { Header = "Quit Notch" };
        quitItem.Click += (_, _) => quit();

        var icon = new TaskbarIcon
        {
            ToolTipText = "Notch",
            Icon = DrawIcon(),
            ContextMenu = new ContextMenu { Items = { quitItem } },
        };

        // Efficiency mode would throttle the animation timers of the whole process.
        icon.ForceCreate(enablesEfficiencyMode: false);
        return icon;
    }

    private static Drawing.Icon DrawIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
        using (var path = new Drawing.Drawing2D.GraphicsPath())
        using (var outline = new Drawing.Pen(Drawing.Color.White, 2))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // A pill: two half circles joined by straight edges.
            path.AddArc(2, 9, 14, 14, 90, 180);
            path.AddArc(16, 9, 14, 14, 270, 180);
            path.CloseFigure();

            g.FillPath(Drawing.Brushes.Black, path);
            g.DrawPath(outline, path);
        }

        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
