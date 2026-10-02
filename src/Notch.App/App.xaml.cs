using System.Diagnostics;
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
using Notch.Core.Shelf;
using Notch.Core.Terminal;
using Notch.Platform.Hud;
using Notch.Platform.Media;

namespace Notch.App;

public partial class App : Application
{
    private static readonly HttpClient PluginHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>What is on the shelf under <c>--demo</c>. None of it exists, so each tile shows a plain glyph.</summary>
    private static readonly string[] DemoShelf =
    [
        @"C:\Demo\Quarterly report.pdf",
        @"C:\Demo\Holiday photos",
        @"C:\Demo\budget.xlsx",
        @"C:\Demo\notes.txt",
        @"C:\Demo\logo.png",
    ];

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

        // A restarted copy starts while the old one is still shutting down. Its plugin files must
        // be free before pending plugin updates are put in place, so wait for it to exit first.
        WaitForPreviousInstance(e);

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
        // A screenshot run is a separate short-lived process and works beside a running notch.
        if (!isFirstInstance && Option(e, "--screenshots=") is null)
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

        // The demo's shelf is made up and never saved, so it cannot touch the real one.
        FileShelf shelf;
        if (demo)
        {
            shelf = new FileShelf(DemoShelf);
        }
        else
        {
            var shelfStore = new ShelfStore(Path.Combine(appData, "shelf.json"));
            shelf = new FileShelf(shelfStore.Load());

            // One save at a time: the shelf reports changes on whichever thread made them.
            shelf.Changed += (_, _) => Dispatcher.BeginInvoke(() => shelfStore.Save(shelf.Snapshot()));
        }

        // Before any window exists, so nothing is ever drawn without its colours.
        ThemeManager.Apply(settings);
        var window = new NotchWindow(_activities, media, _terminal, pluginCards, _plugins.Pages, _plugins.ShellState, shelf, settingsStore, settings)
        {
            ShelfKeepsMissingFiles = demo,
        };

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

        _plugins.ShellState.OpenSettings = () => Dispatcher.Invoke(() => OpenSettings(window, settings, settingsStore));

        // --screenshots=<folder> (with --demo) saves pictures of the notch for the website, then quits.
        if (demo && Option(e, "--screenshots=") is { } shots)
        {
            _ = TakeScreenshotsAsync(window, shots);
            return;
        }

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

    private async Task TakeScreenshotsAsync(NotchWindow window, string folder)
    {
        int exitCode = 0;
        try
        {
            await ScreenshotRunner.RunAsync(window, _activities!, folder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        Shutdown(exitCode);
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

        _settingsWindow = new SettingsWindow(
            settings,
            store,
            _plugins!,
            _pluginInstaller!,
            confirm => _updates?.ForceUpdateAsync(confirm) ?? Task.FromResult("Updates are not available yet."),
            () => notch.HasTerminalSessions,
            Restart);
        _settingsWindow.Saved += (_, _) =>
        {
            _activities?.SetSuppressed(settings.SuppressedActivityIds());
            notch.ApplySettings();
            _updates?.CheckSoon();

            // Only now is it known whether Windows lets Notch have the hotkey.
            if (notch.HotkeyProblem is { } problem)
            {
                MessageBox.Show(problem + " Choose another one in Settings.", "Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            string[] enabledPlugins = [.. settings.EnabledPlugins];
            Task.Run(() => _plugins?.SetEnabled(enabledPlugins));
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private const string WaitForFlag = "--wait-for=";

    /// <summary>
    /// Starts a new copy of the app that waits for this one to exit, then closes this one. Used
    /// after a plugin update, which only takes over when the plugin's files are no longer loaded.
    /// </summary>
    private void Restart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null)
            {
                return;
            }

            var start = new ProcessStartInfo(exe) { UseShellExecute = false };

            // Same switches as this run (--plugin=, --display=, ...), minus the ones that only mean "just restarted".
            foreach (string arg in Environment.GetCommandLineArgs().Skip(1)
                .Where(a => !a.StartsWith(WaitForFlag, StringComparison.OrdinalIgnoreCase)
                    && !a.Equals(UpdateService.UpdatedFlag, StringComparison.OrdinalIgnoreCase)))
            {
                start.ArgumentList.Add(arg);
            }

            start.ArgumentList.Add(WaitForFlag + Environment.ProcessId);
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogError(e);
            return;
        }

        Shutdown();
    }

    private static void WaitForPreviousInstance(StartupEventArgs e)
    {
        if (!int.TryParse(Option(e, WaitForFlag), out int pid))
        {
            return;
        }

        try
        {
            using Process previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It has already exited, which is what was being waited for.
        }
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
