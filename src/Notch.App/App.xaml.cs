using System.IO;
using System.Windows;
using H.NotifyIcon;
using Notch.App.Shell;
using Notch.App.Terminal;
using Notch.Core.Activities;
using Notch.Core.Media;
using Notch.Core.Settings;
using Notch.Core.Terminal;
using Notch.Platform.Hud;
using Notch.Platform.Media;

namespace Notch.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ActivityManager? _activities;
    private GsmtcMediaService? _media;
    private MediaActivityPublisher? _mediaPublisher;
    private TerminalController? _terminal;
    private volatile SystemHudSources? _huds;
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

        bool demo = HasFlag(e, "--demo");

        var settingsStore = new SettingsStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "settings.json"));
        AppSettings settings = settingsStore.Load();

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
        _terminal = new TerminalController(_activities, Dispatcher);

        var window = new NotchWindow(_activities, media, _terminal, settingsStore, settings);
        window.Show();
        if (HasFlag(e, "--pin-open"))
        {
            window.PinOpen();
        }

        if (Option(e, "--tab=") is { } tab)
        {
            window.ShowTab(tab);
        }

        // --open=claude|codex|shell starts a terminal session straight away.
        string? open = Option(e, "--open=");
        if (TerminalProfile.All.FirstOrDefault(p => p.Id.Equals(open, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            window.OpenTerminal(profile);
        }

        _tray = TrayIcon.Create(Shutdown);

        ActivityManager activities = _activities;
        Task.Run(() => _huds = SystemHudSources.Start(activities));

        if (demo)
        {
            _demo = new DemoDriver(_activities);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _demo?.Dispose();
        _terminal?.Dispose();
        _huds?.Dispose();
        _tray?.Dispose();
        _mediaPublisher?.Dispose();
        _media?.Dispose();
        _activities?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static bool HasFlag(StartupEventArgs e, string flag) =>
        e.Args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The value of a <c>--name=value</c> argument, or null when it was not given.</summary>
    private static string? Option(StartupEventArgs e, string prefix) =>
        e.Args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

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
