using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using H.NotifyIcon;
using Notch.App.Settings;
using Notch.App.Shell;
using Notch.App.Terminal;
using Notch.App.Updates;
using Notch.Core.Activities;
using Notch.Core.Media;
using Notch.Core.Plugins;
using Notch.Core.Settings;
using Notch.Core.Terminal;
using Notch.Platform.Hud;
using Notch.Platform.Media;

namespace Notch.App;

public partial class App : Application
{
    private static readonly HttpClient PluginHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    private Mutex? _singleInstance;
    private ActivityManager? _activities;
    private GsmtcMediaService? _media;
    private MediaActivityPublisher? _mediaPublisher;
    private TerminalController? _terminal;
    private volatile SystemHudSources? _huds;
    private PluginManager? _plugins;
    private PluginInstaller? _pluginInstaller;
    private TaskbarIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private UpdateService? _updates;
    private DemoDriver? _demo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A background utility should survive a bug in one feature; record it and carry on.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        // Debug builds use their own name so they can run next to an installed copy.
#if DEBUG
        const string instanceName = @"Local\Notch.SingleInstance.Debug";
#else
        const string instanceName = @"Local\Notch.SingleInstance";
#endif
        _singleInstance = new Mutex(initiallyOwned: true, instanceName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        bool demo = HasFlag(e, "--demo");
        if (HasFlag(e, "--trace-hover"))
        {
            HoverTrace.Enable();
        }

        // --software-render draws without the GPU, which makes timing problems easier to reproduce.
        if (HasFlag(e, "--software-render"))
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }


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

        var pluginCards = new PluginCardBoard();
        string appData = Path.GetDirectoryName(settingsStore.FilePath)!;
        _plugins = new PluginManager(
            Path.Combine(appData, "plugins"),
            Path.Combine(appData, "plugin-data"),
            _activities,
            pluginCards,
            new PluginLog(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "plugins.log")));

        // Before anything is loaded, so plugin updates downloaded during the last run can replace their files.
        PluginInstaller.ApplyPendingUpdates(_plugins.PluginsDirectory);
        _pluginInstaller = new PluginInstaller(PluginHttp, _plugins.PluginsDirectory, _plugins.IsLoaded);

        // Only reads manifests, so it is cheap enough to do before the window shows.
        // --plugin=<folder> runs a plugin straight from its build output, enabled or not.
        _plugins.Discover(Options(e, "--plugin="));

        // --theme=dark|light|system shows that theme for this run only.
        if (Enum.TryParse(Option(e, "--theme="), ignoreCase: true, out NotchTheme theme))
        {
            ThemeManager.Override = theme;
        }

        // Before any window exists, so nothing is ever drawn without its colours.
        ThemeManager.Apply(settings);
        var window = new NotchWindow(_activities, media, _terminal, pluginCards, _plugins.Pages, settingsStore, settings);

        // --display=2 uses the display the settings window lists as "Display 2", for this run only.
        if (int.TryParse(Option(e, "--display="), out int display))
        {
            window.UseDisplay(display - 1);
        }

        // --style=notch|island and --position=topcenter|taskbarleft show that look for this run only.
        window.UseAppearance(
            Enum.TryParse(Option(e, "--style="), ignoreCase: true, out NotchStyle style) ? style : null,
            Enum.TryParse(Option(e, "--position="), ignoreCase: true, out NotchPosition position) ? position : null);

        window.Show();
        if (HasFlag(e, "--pin-open"))
        {
            window.PinOpen();
        }

        if (HasFlag(e, "--settings"))
        {
            OpenSettings(window, settings, settingsStore);
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

        _activities.SetSuppressed(settings.SuppressedActivityIds());
        _tray = TrayIcon.Create(
            () => OpenSettings(window, settings, settingsStore),
            () => settings.Theme,
            chosen =>
            {
                settings.Theme = chosen;
                settingsStore.Save(settings);
                ThemeManager.Apply(settings);
            },
            Shutdown);

        ActivityManager activities = _activities;
        Task.Run(() => _huds = SystemHudSources.Start(activities));

        // Off the UI thread: loading assemblies and starting plugins must not delay the notch.
        PluginManager plugins = _plugins;
        string[] enabledPlugins = [.. settings.EnabledPlugins];
        Task.Run(() => plugins.SetEnabled(enabledPlugins));

        _updates = new UpdateService(settings, settingsStore, _activities, () => window.IsBusy, Shutdown);
        _updates.CleanUpDownloads();
        if (HasFlag(e, UpdateService.UpdatedFlag))
        {
            UpdateService.AnnounceUpdated(_activities);
        }

        if (demo)
        {
            _demo = new DemoDriver(_activities);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _demo?.Dispose();
        _plugins?.Dispose();
        _updates?.Dispose();
        _terminal?.Dispose();
        _huds?.Dispose();
        _tray?.Dispose();
        _mediaPublisher?.Dispose();
        _media?.Dispose();
        _activities?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OpenSettings(NotchWindow notch, AppSettings settings, SettingsStore store)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(settings, store, _plugins!, _pluginInstaller!);
        _settingsWindow.Saved += (_, _) =>
        {
            _activities?.SetSuppressed(settings.SuppressedActivityIds());
            notch.ApplySettings();
            _updates?.CheckSoon();

            string[] enabledPlugins = [.. settings.EnabledPlugins];
            Task.Run(() => _plugins?.SetEnabled(enabledPlugins));
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private static void LogError(Exception exception)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTimeOffset.Now:u} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nowhere left to report to.
        }
    }

    private static bool HasFlag(StartupEventArgs e, string flag) =>
        e.Args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The value of a <c>--name=value</c> argument, or null when it was not given.</summary>
    private static string? Option(StartupEventArgs e, string prefix) => Options(e, prefix).FirstOrDefault();

    /// <summary>The values of every <c>--name=value</c> argument with this name.</summary>
    private static IEnumerable<string> Options(StartupEventArgs e, string prefix) =>
        e.Args.Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(a => a[prefix.Length..]);

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
