using System.Windows.Controls;
using H.NotifyIcon;
using Notch.Core.Settings;
using Drawing = System.Drawing;

namespace Notch.App.Shell;

internal static class TrayIcon
{
    public static TaskbarIcon Create(Action openSettings, Func<NotchTheme> getTheme, Action<NotchTheme> setTheme, Action quit)
    {
        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => openSettings();

        var themeItem = new MenuItem { Header = "Theme" };
        foreach ((NotchTheme theme, string name) in new[]
        {
            (NotchTheme.Dark, "Dark"),
            (NotchTheme.Light, "Light"),
            (NotchTheme.System, "Follow Windows"),
        })
        {
            var item = new MenuItem { Header = name, Tag = theme };
            item.Click += (_, _) => setTheme(theme);
            themeItem.Items.Add(item);
        }

        // The tick is set each time the menu opens, as settings can change the theme too.
        themeItem.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in themeItem.Items)
            {
                item.IsChecked = (NotchTheme)item.Tag == getTheme();
            }
        };

        var quitItem = new MenuItem { Header = "Quit Notch" };
        quitItem.Click += (_, _) => quit();

        var icon = new TaskbarIcon
        {
            ToolTipText = "Notch",
            Icon = DrawIcon(),
            ContextMenu = new ContextMenu { Items = { settingsItem, themeItem, new Separator(), quitItem } },
        };
        icon.TrayLeftMouseUp += (_, _) => openSettings();

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
