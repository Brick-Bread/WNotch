using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using Notch.App.Agents;
using Notch.App.Automation;
using Notch.App.Notifications;
using Notch.App.Plugins;
using Notch.App.Settings;
using Notch.App.Shell;
using Notch.App.Terminal;
using Notch.App.Updates;
using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Automation;
using Notch.Core.Media;
using Notch.Core.Plugins;
using Notch.Core.Plugins.Checks;
using Notch.Core.Settings;
using Notch.Core.Shelf;
using Notch.Core.Telemetry;
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
    private string? _pluginStartMarker;
    private PluginInstaller? _pluginInstaller;
    private TaskbarIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private UpdateService? _updates;
    private DemoDriver? _demo;
    private PluginInstallFlow? _installFlow;
    private CommandDispatcher? _commands;
    private CommandPipe? _commandPipe;
    private WebhookService? _webhook;
    private AgentBoard? _agentBoard;
    private AgentPublisher? _agentPublisher;
    private AgentTracker? _agentTracker;
    private NotificationService? _notifications;

    /// <summary>The opt-in usage record; see <see cref="UsageLog"/>.</summary>
    internal static UsageLog Usage { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "usage.jsonl"));

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
            // Hand a notch:// link (or just "show yourself") to the copy that is running, then leave.
            // Off the UI thread: waiting for the answer must not block this thread's own message loop.
            string[] forwarded = [.. e.Args.Where(a => !a.StartsWith(WaitForFlag, StringComparison.OrdinalIgnoreCase))];
            Task.Run(() => CommandPipe.SendAsync(forwarded, TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
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
        Usage.Enabled = settings.UsageStats;
        Usage.Record("app_start", new Dictionary<string, string>
        {
            ["version"] = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            ["os"] = Environment.OSVersion.Version.ToString(),
        });

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
        _agentBoard = new AgentBoard();
        _agentPublisher = new AgentPublisher(_activities, _agentBoard);
        _terminal = new TerminalController(_agentBoard, Dispatcher);

        var pluginCards = new PluginCardBoard();
        string appData = Path.GetDirectoryName(settingsStore.FilePath)!;

        // Plugins are looked over before they start: what the user approved, what the registry withdrew,
        // and what is inside them. See "Plugin checks" in docs/plugins.md.
        string localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch");
        string registryCache = Path.Combine(appData, "registry.json");
        string trustFile = Path.Combine(appData, "plugin-trust.json");
        bool firstCheckedRun = !File.Exists(trustFile);
        var checks = new PluginChecks
        {
            Audit = new FilePluginAudit(Path.Combine(localData, "plugin-audit.log")),
            Trust = new PluginTrustStore(trustFile),
            Revocations = () => PluginRevocations.Read(registryCache),
        };
        _plugins = new PluginManager(
            Path.Combine(appData, "plugins"),
            Path.Combine(appData, "plugin-data"),
            _activities,
            pluginCards,
            new PluginLog(Path.Combine(localData, "plugins.log")),
            checks: checks);
        _pluginStartMarker = Path.Combine(appData, "plugins-starting");

        // Before anything is loaded, so plugin updates downloaded during the last run can replace their files.
        PluginInstaller.ApplyPendingUpdates(_plugins.PluginsDirectory);
        _pluginInstaller = new PluginInstaller(PluginHttp, _plugins.PluginsDirectory, _plugins.IsLoaded);
        _installFlow = new PluginInstallFlow(
            PluginHttp, _pluginInstaller, _plugins, settings, settingsStore, _activities, registryCache);
        _installFlow.Installed += _ => _settingsWindow?.RefreshPlugins();

        // Only reads manifests, so it is cheap enough to do before the window shows.
        // --plugin=<folder> runs a plugin straight from its build output, enabled or not.
        _plugins.Discover(Options(e, "--plugin="));

        // Plugins switched on before approvals existed keep running, as they did; later ones are asked about.
        if (firstCheckedRun)
        {
            _plugins.ApproveExisting(settings.EnabledPlugins);
        }

        // --no-plugins starts without any plugin. So does the start after Notch stopped while plugins
        // were starting, so a plugin that breaks startup cannot lock the user out.
        bool stoppedWhileStarting = File.Exists(_pluginStartMarker);
        _plugins.SafeMode = HasFlag(e, "--no-plugins") || stoppedWhileStarting;
        if (stoppedWhileStarting)
        {
            DeleteQuietly(_pluginStartMarker);
            _activities.Publish(new Notch.Core.Activities.Activity
            {
                Id = "notice.plugins-safe-mode",
                Tier = ActivityTier.Transient,
                Title = "Plugins are off",
                Detail = "Notch stopped while plugins were starting. Restart Notch to turn them back on.",
                Glyph = "\uE7BA",
                Glow = new Glow(GlowColor.Orange, GlowPattern.Flash),
                Lifetime = TimeSpan.FromSeconds(8),
            });
        }

        ThemeManager.Attach(_plugins.Themes);

        // --plugin-theme=<plugin id>/<theme id> shows that plugin theme for this run only.
        ThemeManager.PluginThemeOverride = Option(e, "--plugin-theme=");

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
        var window = new NotchWindow(_activities, media, _terminal, pluginCards, _plugins.Pages, _plugins.ShellState, shelf, settingsStore, settings, _agentBoard, entry => _agentTracker?.Focus(entry) ?? false)
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

        // Not under --demo: the screenshots must not show whatever the developer happens to be running.
        if (!demo)
        {
            _agentTracker = new AgentTracker(Dispatcher, _agentBoard, settings, _terminal.HookServer, _terminal.TryRouteOpenCode);
            _notifications = new NotificationService(settings, _activities);
            _ = ApplyNotificationsAsync(showProblem: false);
            if (settings.GlobalAgentHooks)
            {
                // The hooks must name this copy of the hook program, wherever Notch is installed now.
                _ = Task.Run(AgentHookFiles.RefreshPaths);
            }
        }

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
        if (window.TerminalProfiles.FirstOrDefault(p => p.Id.Equals(open, StringComparison.OrdinalIgnoreCase)) is { } profile)
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
                settings.PluginTheme = null;
                settingsStore.Save(settings);
                ThemeManager.Apply(settings);
            },
            Shutdown);

        ActivityManager activities = _activities;
        Task.Run(() => _huds = SystemHudSources.Start(activities));

        // Off the UI thread: loading assemblies and starting plugins must not delay the notch.
        PluginManager plugins = _plugins;
        string[] enabledPlugins = [.. settings.EnabledPlugins];
        string? startMarker = plugins.SafeMode ? null : _pluginStartMarker;
        Task.Run(async () =>
        {
            // The marker is there only while plugins are starting; finding it at the next start means they broke it.
            if (startMarker is not null)
            {
                WriteQuietly(startMarker);
            }

            try
            {
                plugins.SetEnabled(enabledPlugins);
            }
            finally
            {
                DeleteQuietly(startMarker);
            }

            // The plugin list also names plugins withdrawn for being harmful; one already running is stopped.
            if (!demo && settings.NotifyOfUpdates && _installFlow is { } flow)
            {
                try
                {
                    await flow.GetRegistryAsync(forceReload: true);
                    plugins.EnforceRevocations();
                }
                catch (Exception ex) when (ex is PluginLoadException or HttpRequestException or TaskCanceledException)
                {
                    // Offline; the copy from the last time is used for the checks.
                }
            }
        });

        window.SettingsRequested += () => OpenSettings(window, settings, settingsStore);
        _plugins.ShellState.OpenSettings = () => Dispatcher.Invoke(() => OpenSettings(window, settings, settingsStore));

        // --screenshots=<folder> (with --demo) saves pictures of the notch for the website, then quits.
        if (demo && Option(e, "--screenshots=") is { } shots)
        {
            _ = TakeScreenshotsAsync(window, shots);
            return;
        }

        StartAutomation(window, settings, settingsStore, appData, e.Args);

        _updates = new UpdateService(settings, settingsStore, _activities, Shutdown);
        _updates.CleanUpDownloads();
        if (HasFlag(e, UpdateService.UpdatedFlag))
        {
            UpdateService.AnnounceUpdated(_activities);
        }
        else
        {
            UpdateService.AnnounceFailedUpdateIfAny(_activities);
        }

        if (demo)
        {
            _demo = new DemoDriver(_activities);
        }
    }

    /// <summary>
    /// Starts what lets other programs talk to the notch: the pipe a second copy, a notch:// link and
    /// notchctl use, the plugin installer they can trigger, and the webhook when it is switched on.
    /// </summary>
    private void StartAutomation(NotchWindow window, AppSettings settings, SettingsStore settingsStore, string appData, string[] startupArguments)
    {
        _commands = new CommandDispatcher(
            Dispatcher,
            window,
            _plugins!,
            _installFlow!,
            () => OpenSettings(window, settings, settingsStore),
            window.TogglePalette);
        window.PaletteExtras = () => PluginPaletteEntries(settings, settingsStore);
        _commandPipe = new CommandPipe(_commands.HandleArgumentsAsync);

        _webhook = new WebhookService(settings, settingsStore, _activities!, _commands);
        _webhook.Apply();
        if (_webhook.Problem is { } problem)
        {
            LogError(new InvalidOperationException(problem));
        }

        // A notch:// link that started this copy; it is handled once the window is up.
        string? link = startupArguments.FirstOrDefault(a => a.StartsWith(CommandParser.Scheme + ":", StringComparison.OrdinalIgnoreCase));
        if (link is not null)
        {
            Dispatcher.BeginInvoke(() => _ = _commands.HandleArgumentsAsync([link]), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Starts or stops showing Windows notifications; says why when Windows will not allow it and the user just asked.</summary>
    private async Task ApplyNotificationsAsync(bool showProblem)
    {
        if (_notifications is null)
        {
            return;
        }

        string? problem = await _notifications.ApplyAsync();
        if (problem is not null && showProblem)
        {
            MessageBox.Show(problem, "Notch", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>The palette's entries for plugins: switch each installed one on or off.</summary>
    private IEnumerable<PaletteEntry> PluginPaletteEntries(AppSettings settings, SettingsStore store)
    {
        foreach (PluginInfo plugin in _plugins!.Plugins.Where(p => p is { Id: not null, AlwaysEnabled: false }))
        {
            string id = plugin.Id!;
            bool on = settings.EnabledPlugins.Contains(id);
            yield return new PaletteEntry(
                (on ? "Switch off " : "Switch on ") + plugin.Name,
                "Plugin",
                "plugins enable disable",
                () => _ = _commands!.RunAsync(new SetPluginEnabledCommand(id, !on)));
        }

        yield return new PaletteEntry(
            "Stop all plugins",
            "Plugin",
            "plugins kill stop emergency off",
            () => StopAllPlugins(settings, store));
    }

    /// <summary>The kill switch: ends every plugin at once and switches them all off until the user turns each one on again.</summary>
    private void StopAllPlugins(AppSettings settings, SettingsStore store)
    {
        IReadOnlyList<string> stopped = _plugins!.KillAll("stopped by the user");
        settings.EnabledPlugins = [];
        store.Save(settings);
        _settingsWindow?.ShowAllPluginsOff();
        _activities!.Publish(new Notch.Core.Activities.Activity
        {
            Id = "notice.plugins-stopped",
            Tier = ActivityTier.Transient,
            Title = "Plugins stopped",
            Detail = stopped.Count == 0 ? "None were running. All are switched off." : $"{stopped.Count} stopped and switched off.",
            Glyph = "\uE7BA",
            Glow = new Glow(GlowColor.Orange, GlowPattern.Flash),
            Lifetime = TimeSpan.FromSeconds(5),
        });
    }

    private static void WriteQuietly(string path)
    {
        try
        {
            File.WriteAllText(path, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a crash during startup is not detected; nothing else changes.
        }
    }

    private static void DeleteQuietly(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place, it only makes the next start a safe-mode one.
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
        // Leaving normally while plugins are still starting is not a crash.
        DeleteQuietly(_pluginStartMarker);

        _demo?.Dispose();
        _webhook?.Dispose();
        _agentTracker?.Dispose();
        _notifications?.Dispose();
        _agentPublisher?.Dispose();
        _commandPipe?.Dispose();
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
            _installFlow!,
            confirm => _updates?.ForceUpdateAsync(confirm) ?? Task.FromResult("Updates are not available yet."),
            () => notch.HasTerminalSessions,
            Restart,
            notch.ClearClipboardHistory);
        _settingsWindow.Saved += (_, _) =>
        {
            _activities?.SetSuppressed(settings.SuppressedActivityIds());
            Usage.Enabled = settings.UsageStats;
            notch.ApplySettings();
            _updates?.CheckSoon();

            _webhook?.Apply();
            _agentTracker?.ScanSoon();
            _ = ApplyNotificationsAsync(showProblem: true);
            if (_webhook?.Problem is { } webhookProblem)
            {
                MessageBox.Show(webhookProblem, "Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Only now is it known whether Windows lets Notch have the hotkeys.
            if ((notch.HotkeyProblem ?? notch.PaletteHotkeyProblem) is { } problem)
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
