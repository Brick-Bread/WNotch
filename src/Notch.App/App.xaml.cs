using System.Windows;
using H.NotifyIcon;
using Notch.App.Shell;
using Notch.Core.Activities;
using Notch.Core.Media;
using Notch.Platform.Media;

namespace Notch.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ActivityManager? _activities;
    private GsmtcMediaService? _media;
    private MediaActivityPublisher? _mediaPublisher;
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

        bool demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        _activities = new ActivityManager();
        IMediaService media;
        if (demo)
        {
            media = new DemoMediaService();
        }
        else
        {
            media = _media = new GsmtcMediaService();
            _ = StartMediaAsync(_media);
        }

        _mediaPublisher = new MediaActivityPublisher(media, _activities);

        var window = new NotchWindow(_activities, media);
        window.Show();
        if (e.Args.Contains("--pin-open", StringComparer.OrdinalIgnoreCase))
        {
            window.PinOpen();
        }

        _tray = TrayIcon.Create(Shutdown);

        if (demo)
        {
            _demo = new DemoDriver(_activities);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _demo?.Dispose();
        _tray?.Dispose();
        _mediaPublisher?.Dispose();
        _media?.Dispose();
        _activities?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static async Task StartMediaAsync(GsmtcMediaService media)
    {
        try
        {
            await media.StartAsync();
        }
        catch (Exception)
        {
            // Media sessions are unavailable (e.g. stripped-down Windows editions); the rest of the notch still works.
        }
    }
}
