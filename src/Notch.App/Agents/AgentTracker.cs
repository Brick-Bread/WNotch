using System.IO;
using System.Windows.Threading;
using Notch.Core.Agents;
using Notch.Core.Settings;
using Notch.Platform.Agents;

namespace Notch.App.Agents;

/// <summary>
/// Finds coding agents that were not started from Notch's terminal and keeps them on the
/// <see cref="AgentBoard"/>. Presence comes from looking at the running programs every few
/// seconds; what an agent is doing comes from its hooks, when the user has them set up.
/// Sessions inside the Claude and ChatGPT desktop apps are read from the files those apps keep,
/// which say what each session is doing without any hooks.
/// </summary>
internal sealed class AgentTracker : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long a desktop app session said to be working may stay quiet before it is taken to have stopped.</summary>
    private static readonly TimeSpan AppSessionStale = TimeSpan.FromMinutes(10);

    private readonly Dispatcher _ui;
    private readonly AgentBoard _board;
    private readonly AppSettings _settings;
    private readonly AgentHookServer _hooks;
    private readonly Func<AgentHooks.HookMessage, bool>? _routeToTerminal;
    private readonly ProcessScanner _scanner = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Tracked> _tracked = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly AppSessionReader _appReader = new();

    /// <summary>When the user last looked at each desktop app session through Notch or the app's window.</summary>
    private readonly Dictionary<string, DateTime> _appViewed = [];

    /// <summary>A ChatGPT turn that ended before Notch started has been seen, as far as Notch can tell.</summary>
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private volatile IReadOnlyList<ProcessSnapshot> _processes = [];
    private volatile IReadOnlyList<DetectedAgent> _detected = [];

    /// <param name="routeToTerminal">Gives an opencode event to the terminal's own sessions, on the UI thread; true when it was theirs.</param>
    public AgentTracker(Dispatcher ui, AgentBoard board, AppSettings settings, AgentHookServer hooks, Func<AgentHooks.HookMessage, bool>? routeToTerminal = null)
    {
        _routeToTerminal = routeToTerminal;
        _ui = ui;
        _board = board;
        _settings = settings;
        _hooks = hooks;
        _hooks.MessageReceived += OnHook;
        _ = Task.Run(RunAsync);
    }

    public void Dispose()
    {
        _hooks.MessageReceived -= OnHook;
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Looks at the running programs now rather than at the next tick, e.g. after settings changed.</summary>
    public void ScanSoon() => _ = Task.Run(() => ScanAsync());

    /// <summary>Brings the window the agent runs in to the front. False when none could be found.</summary>
    public bool Focus(AgentEntry entry)
    {
        if (entry.Pid is not { } pid)
        {
            return false;
        }

        IReadOnlyList<int> candidates = AgentDetector.WindowCandidates(pid, _processes);
        bool focused = candidates.Count > 0 && WindowFocus.Focus(candidates);
        if (focused)
        {
            // Looking at it is what clears a "Done".
            _ui.BeginInvoke(() => MarkViewed(entry));
        }

        return focused;
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await ScanAsync();
            try
            {
                await Task.Delay(ScanInterval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ScanAsync()
    {
        if (!await _scanGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!_settings.DetectAgents && !_settings.WatchAppSessions)
            {
                _processes = [];
                _detected = [];
                _ = _ui.BeginInvoke(() => Apply([], [], []));
                return;
            }

            IReadOnlyList<ProcessSnapshot> processes = _scanner.Scan();
            IReadOnlyList<DetectedAgent> detected = _settings.DetectAgents
                ? AgentDetector.Detect(processes, Environment.ProcessId, [.. _settings.HiddenAgents])
                : [];
            _processes = processes;
            _detected = detected;

            IReadOnlyList<RunningApp> apps = _settings.WatchAppSessions ? AppSessions.Running(processes) : [];
            IReadOnlyList<AppSession> appSessions = _appReader.Read(apps, DateTime.UtcNow);

            int foreground = WindowFocus.ForegroundProcessId();
            _ = _ui.BeginInvoke(() => Apply(detected, apps, appSessions, foreground));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // A scan that fails is tried again at the next tick.
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>On the UI thread: makes the board match what the scan found.</summary>
    private void Apply(IReadOnlyList<DetectedAgent> detected, IReadOnlyList<RunningApp> apps, IReadOnlyList<AppSession> appSessions, int foregroundPid = 0)
    {
        ApplyApps(apps, appSessions, foregroundPid);

        var keep = new HashSet<string>();
        foreach (DetectedAgent agent in detected)
        {
            keep.Add(agent.Key);
            if (!_tracked.TryGetValue(agent.Key, out Tracked? tracked))
            {
                _tracked[agent.Key] = tracked = new Tracked(agent, new AgentStatusTracker(agent.Definition.Kind));
            }

            tracked.Agent = agent;

            // A finished turn the user is looking at needs no badge.
            if (tracked.Status.State == AgentState.Done && foregroundPid != 0
                && AgentDetector.WindowCandidates(agent.Pid, _processes).Contains(foregroundPid))
            {
                tracked.Status.OnViewed();
            }

            Publish(tracked);
        }

        foreach (string gone in _tracked.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            _tracked.Remove(gone);
        }

        _board.KeepOnly(AgentSource.Detected, keep);
    }

    /// <summary>On the UI thread: puts the desktop apps' sessions on the board.</summary>
    private void ApplyApps(IReadOnlyList<RunningApp> apps, IReadOnlyList<AppSession> sessions, int foregroundPid)
    {
        DateTime now = DateTime.UtcNow;
        var keep = new HashSet<string>();
        foreach (RunningApp app in apps)
        {
            AppSession[] mine = [.. sessions.Where(s => s.App == app.App)];

            // The app's window is in front: the session it shows has been seen. The Claude app says
            // which one that is; the ChatGPT app does not, so all of its sessions count as seen.
            if (IsInFront(app, foregroundPid))
            {
                AppSession? shown = app.App == DesktopApp.Claude ? mine.MaxBy(s => s.Viewed ?? DateTime.MinValue) : null;
                foreach (AppSession session in app.App == DesktopApp.Claude ? (shown is null ? [] : [shown]) : mine)
                {
                    _appViewed[session.Key] = now;
                }
            }

            AgentDefinition agent = AppSessions.Agent(app.App);
            foreach (AppSession session in mine)
            {
                keep.Add(session.Key);
                DateTime? viewed = Latest(session.Viewed, _appViewed.TryGetValue(session.Key, out DateTime seen) ? seen : null);
                if (app.App == DesktopApp.ChatGpt)
                {
                    viewed = Latest(viewed, _startedAt);
                }

                _board.Set(new AgentEntry(
                    session.Key,
                    agent.Id,
                    AppSessions.DisplayName(app.App),
                    agent.Glyph,
                    AppSessions.StateFor(session.Turn, viewed, now, AppSessionStale, session.AwaitingPerson),
                    session.Title ?? AppSessions.FolderName(session.Folder),
                    AgentSource.App,
                    app.Pid,
                    AppSessions.DisplayName(app.App) + " app"));
            }
        }

        foreach (string gone in _appViewed.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            _appViewed.Remove(gone);
        }

        _board.KeepOnly(AgentSource.App, keep);
    }

    /// <summary>Whether the window in front belongs to the app: its main process, or one the main process started.</summary>
    private bool IsInFront(RunningApp app, int foregroundPid) =>
        foregroundPid != 0 && (foregroundPid == app.Pid || _processes.Any(p => p.Pid == foregroundPid && p.ParentPid == app.Pid));

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private void OnHook(AgentHooks.HookMessage message)
    {
        // Sessions Notch's terminal started are handled by the terminal.
        bool openCode = message.Agent == "opencode";
        if (message.Session.Length > 0 || (message.HookPid <= 0 && !openCode) || !_settings.DetectAgents)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            DetectedAgent? agent = message.HookPid > 0 ? AgentDetector.AgentOf(message.HookPid, _processes, _detected) : null;
            if (agent is null && !openCode)
            {
                // The agent started since the last scan.
                await ScanAsync();
                agent = AgentDetector.AgentOf(message.HookPid, _processes, _detected);
            }

            if (agent is not null)
            {
                _ = _ui.BeginInvoke(() => ApplyHook(agent, message));
                return;
            }

            if (openCode)
            {
                await RouteOpenCodeAsync(message);
            }
        });
    }

    /// <summary>
    /// opencode's plugin cannot say which window it reports for, only which folder. A session in
    /// Notch's own terminal in that folder takes it; otherwise the opencode windows found running there do.
    /// </summary>
    private async Task RouteOpenCodeAsync(AgentHooks.HookMessage message)
    {
        bool terminal = false;
        await _ui.InvokeAsync(() => terminal = _routeToTerminal?.Invoke(message) == true);
        if (terminal)
        {
            return;
        }

        IReadOnlyList<DetectedAgent> targets = AgentDetector.OpenCodeIn(message.Folder, _processes, _detected);
        if (targets.Count == 0)
        {
            // It may have started since the last scan.
            await ScanAsync();
            targets = AgentDetector.OpenCodeIn(message.Folder, _processes, _detected);
        }

        foreach (DetectedAgent agent in targets)
        {
            _ = _ui.BeginInvoke(() => ApplyHook(agent, message));
        }
    }

    private void ApplyHook(DetectedAgent agent, AgentHooks.HookMessage message)
    {
        if (!_tracked.TryGetValue(agent.Key, out Tracked? tracked))
        {
            // Not on the board yet: the scan that found it has not been applied.
            _tracked[agent.Key] = tracked = new Tracked(agent, new AgentStatusTracker(agent.Definition.Kind));
        }

        if (message.Folder is { } folder)
        {
            tracked.Folder = Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : folder;
        }

        tracked.Status.OnHookEvent(message.EventName);
        Publish(tracked);
    }

    private void MarkViewed(AgentEntry entry)
    {
        if (entry.Source == AgentSource.App)
        {
            // Shown again at the next scan, without its "Done".
            _appViewed[entry.Key] = DateTime.UtcNow;
            if (entry.State == AgentState.Done)
            {
                _board.Set(entry with { State = AgentState.Idle });
            }

            return;
        }

        if (_tracked.TryGetValue(entry.Key, out Tracked? tracked))
        {
            tracked.Status.OnViewed();
            Publish(tracked);
        }
    }

    private void Publish(Tracked tracked) => _board.Set(new AgentEntry(
        tracked.Agent.Key,
        tracked.Agent.Definition.Id,
        tracked.Agent.Definition.DisplayName,
        tracked.Agent.Definition.Glyph,
        tracked.Status.State,
        tracked.Folder,
        AgentSource.Detected,
        tracked.Agent.Pid,
        tracked.Agent.HostName));

    private sealed class Tracked
    {
        public Tracked(DetectedAgent agent, AgentStatusTracker status)
        {
            Agent = agent;
            Status = status;
        }

        public DetectedAgent Agent { get; set; }

        public AgentStatusTracker Status { get; }

        public string? Folder { get; set; }
    }
}
