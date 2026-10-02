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
/// </summary>
internal sealed class AgentTracker : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);

    private readonly Dispatcher _ui;
    private readonly AgentBoard _board;
    private readonly AppSettings _settings;
    private readonly AgentHookServer _hooks;
    private readonly Func<AgentHooks.HookMessage, bool>? _routeToTerminal;
    private readonly ProcessScanner _scanner = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Tracked> _tracked = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);

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
            _ui.BeginInvoke(() => MarkViewed(entry.Key));
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
            if (!_settings.DetectAgents)
            {
                _processes = [];
                _detected = [];
                _ = _ui.BeginInvoke(() => Apply([]));
                return;
            }

            IReadOnlyList<ProcessSnapshot> processes = _scanner.Scan();
            IReadOnlyList<DetectedAgent> detected = AgentDetector.Detect(processes, Environment.ProcessId, [.. _settings.HiddenAgents]);
            _processes = processes;
            _detected = detected;

            int foreground = WindowFocus.ForegroundProcessId();
            _ = _ui.BeginInvoke(() => Apply(detected, foreground));
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
    private void Apply(IReadOnlyList<DetectedAgent> detected, int foregroundPid = 0)
    {
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

    private void MarkViewed(string key)
    {
        if (_tracked.TryGetValue(key, out Tracked? tracked))
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
