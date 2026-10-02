using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Terminal;
using Notch.Platform.Agents;
using Notch.Platform.Terminal;

namespace Notch.App.Terminal;

/// <summary>
/// Runs the built-in terminal: starts profiles in pseudo consoles, pipes them to the xterm.js
/// view, and turns agent hook events into pill activities.
/// </summary>
internal sealed class TerminalController : IDisposable
{
    private static readonly TimeSpan QuietThreshold = TimeSpan.FromSeconds(15);

    private readonly ActivityManager _activities;
    private readonly Dispatcher _dispatcher;
    private readonly AgentHookServer _hookServer = new();
    private readonly DispatcherTimer _quietTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, PendingOutput> _pendingOutput = [];
    private readonly string _hookExecutable = Path.Combine(AppContext.BaseDirectory, "Notch.Hook.exe");
    private readonly string _claudeSettingsFile;
    private bool _isViewing;

    public TerminalController(ActivityManager activities, Dispatcher dispatcher)
    {
        _activities = activities;
        _dispatcher = dispatcher;

        Bridge.Created += OnTerminalCreated;
        Bridge.Resized += (id, columns, rows) => Find(id)?.Process?.Resize(columns, rows);
        Bridge.Input += OnInput;
        Bridge.Bell += id => Find(id)?.Agent.OnBell();

        _hookServer.EventReceived += (sessionId, eventName) =>
            _dispatcher.BeginInvoke(() => Find(sessionId)?.Agent.OnHookEvent(eventName));

        _quietTimer.Tick += (_, _) => CheckForStalledAgents();
        _quietTimer.Start();

        _claudeSettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "claude-hooks.json");
    }

    public TerminalBridge Bridge { get; } = new();

    public ObservableCollection<TerminalSession> Sessions { get; } = [];

    public TerminalSession? Active { get; private set; }

    /// <summary>True while the terminal tab is on screen. A turn that finishes in view needs no "done" badge.</summary>
    public bool IsViewing
    {
        get => _isViewing;
        set
        {
            _isViewing = value;
            if (value)
            {
                Active?.Agent.OnViewed();
            }
        }
    }

    public async Task OpenAsync(TerminalProfile profile, string folder)
    {
        await Bridge.InitializeAsync();

        var session = new TerminalSession(profile, folder);
        session.Agent.Changed += () => OnAgentChanged(session);
        Sessions.Add(session);
        SetActive(session);

        // The page answers with the terminal's size, which is when the process starts.
        Bridge.Create(session.Id);
    }

    public void Activate(TerminalSession session)
    {
        SetActive(session);
        Bridge.Activate(session.Id);
        if (IsViewing)
        {
            session.Agent.OnViewed();
        }
    }

    public void Close(TerminalSession session)
    {
        session.Process?.Dispose();
        session.Process = null;
        _activities.Remove(AgentActivities.IdFor(session.Id));
        Bridge.Close(session.Id);
        lock (_pendingOutput)
        {
            _pendingOutput.Remove(session.Id);
        }

        int index = Sessions.IndexOf(session);
        Sessions.Remove(session);
        if (ReferenceEquals(Active, session))
        {
            Active = null;
            if (Sessions.Count > 0)
            {
                Activate(Sessions[Math.Min(index, Sessions.Count - 1)]);
            }
        }

        PublishAgents();
    }

    public void Dispose()
    {
        _quietTimer.Stop();
        _hookServer.Dispose();
        foreach (TerminalSession session in Sessions)
        {
            session.Process?.Dispose();
        }
    }

    private TerminalSession? Find(string id) => Sessions.FirstOrDefault(s => s.Id == id);

    private void SetActive(TerminalSession session)
    {
        Active = session;
        foreach (TerminalSession other in Sessions)
        {
            other.IsActive = ReferenceEquals(other, session);
        }
    }

    private void OnTerminalCreated(string id, int columns, int rows)
    {
        if (Find(id) is not { Process: null } session)
        {
            return;
        }

        string? executable = Resolve(session.Profile);
        if (executable is null)
        {
            WriteNotice(session, session.Profile.InstallHint);
            return;
        }

        try
        {
            string commandLine = CommandResolver.BuildCommandLine(executable, BuildArguments(session.Profile));
            var environment = new Dictionary<string, string>
            {
                [AgentHooks.PipeVariable] = _hookServer.PipeName,
                [AgentHooks.SessionVariable] = session.Id,
            };

            PseudoConsoleSession process = PseudoConsoleSession.Start(commandLine, session.Folder, columns, rows, environment);
            process.Output += bytes => QueueOutput(session, bytes.Span);
            process.Exited += code => _dispatcher.BeginInvoke(() => OnExited(session, process, code));
            session.Process = process;
        }
        catch (Win32Exception e)
        {
            WriteNotice(session, $"Could not start {session.Profile.DisplayName}: {e.Message}");
        }
    }

    private static string? Resolve(TerminalProfile profile) =>
        ReferenceEquals(profile, TerminalProfile.Shell)
            ? CommandResolver.FindOnPath("pwsh") ?? CommandResolver.FindOnPath(profile.Command)
            : CommandResolver.FindOnPath(profile.Command);

    private IReadOnlyList<string> BuildArguments(TerminalProfile profile)
    {
        if (profile.Agent == AgentKind.None)
        {
            return ["-NoLogo"];
        }

        if (!File.Exists(_hookExecutable))
        {
            return [];
        }

        if (profile.Agent == AgentKind.Claude)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_claudeSettingsFile)!);
            File.WriteAllText(_claudeSettingsFile, AgentHooks.BuildClaudeSettings(_hookExecutable));
        }

        return AgentHooks.BuildArguments(profile.Agent, _hookExecutable, _claudeSettingsFile);
    }

    private void OnInput(string id, string data)
    {
        if (Find(id) is not { } session)
        {
            return;
        }

        session.Process?.Write(data);

        bool submitted = data.Contains('\r');
        if (submitted)
        {
            session.LastActivity = DateTime.UtcNow;
        }

        session.Agent.OnUserInput(submitted);
    }

    /// <summary>
    /// Output arrives on the pseudo console's reader thread, often in many small chunks. They
    /// are gathered here and handed to the page once per dispatcher pass.
    /// </summary>
    private void QueueOutput(TerminalSession session, ReadOnlySpan<byte> bytes)
    {
        bool schedule;
        lock (_pendingOutput)
        {
            if (!_pendingOutput.TryGetValue(session.Id, out PendingOutput? pending))
            {
                _pendingOutput[session.Id] = pending = new PendingOutput();
            }

            pending.Buffer.Write(bytes);
            schedule = !pending.FlushScheduled;
            pending.FlushScheduled = true;
        }

        if (schedule)
        {
            _dispatcher.BeginInvoke(() => FlushOutput(session));
        }
    }

    private void FlushOutput(TerminalSession session)
    {
        byte[] data;
        lock (_pendingOutput)
        {
            if (!_pendingOutput.TryGetValue(session.Id, out PendingOutput? pending))
            {
                return;
            }

            data = pending.Buffer.ToArray();
            pending.Buffer.SetLength(0);
            pending.FlushScheduled = false;
        }

        session.LastActivity = DateTime.UtcNow;
        Bridge.Write(session.Id, data);
    }

    private void OnExited(TerminalSession session, PseudoConsoleSession process, int exitCode)
    {
        if (!ReferenceEquals(session.Process, process))
        {
            return;
        }

        process.Dispose();
        session.Process = null;
        session.Agent.Reset();
        WriteNotice(session, $"Process exited with code {exitCode}.");
    }

    private void WriteNotice(TerminalSession session, string text) =>
        Bridge.Write(session.Id, Encoding.UTF8.GetBytes($"\r\n\u001b[90m{text}\u001b[0m\r\n"));

    private void OnAgentChanged(TerminalSession session)
    {
        if (session.Agent.State == AgentState.Done && IsViewing && ReferenceEquals(Active, session))
        {
            session.Agent.OnViewed();
            return;
        }

        PublishAgents();
    }

    /// <summary>
    /// Puts every session's state in the pill. All of them, not just the one that changed: each
    /// says how many others have something to report, since the pill only shows one.
    /// </summary>
    private void PublishAgents()
    {
        int reporting = Sessions.Count(session => session.Agent.State != AgentState.Idle);
        foreach (TerminalSession session in Sessions)
        {
            Activity? activity = AgentActivities.For(
                session.Id, session.Profile.DisplayName, session.Profile.Glyph, session.Agent.State, session.FolderName, reporting - 1);
            if (activity is null)
            {
                _activities.Remove(AgentActivities.IdFor(session.Id));
            }
            else
            {
                _activities.Publish(activity);
            }
        }
    }

    private void CheckForStalledAgents()
    {
        DateTime threshold = DateTime.UtcNow - QuietThreshold;
        foreach (TerminalSession session in Sessions)
        {
            if (session.Agent.State == AgentState.Working && session.LastActivity < threshold)
            {
                session.Agent.OnOutputQuiet();
            }
        }
    }

    private sealed class PendingOutput
    {
        public MemoryStream Buffer { get; } = new();

        public bool FlushScheduled { get; set; }
    }
}
