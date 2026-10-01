using System.Windows;
using H.NotifyIcon;
using Notch.App.Shell;
using Notch.Core.Activities;

namespace Notch.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ActivityManager? _activities;
    private TaskbarIcon? _tray;
    private DemoDriver? _demo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Notch.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        _activities = new ActivityManager();

        var window = new NotchWindow(_activities);
        window.Show();

        _tray = TrayIcon.Create(Shutdown);

        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            _demo = new DemoDriver(_activities);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _demo?.Dispose();
        _tray?.Dispose();
        _activities?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
