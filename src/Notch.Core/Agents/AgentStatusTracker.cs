namespace Notch.Core.Agents;

public enum AgentKind
{
    /// <summary>A plain shell; no status is tracked.</summary>
    None,
    Claude,
    Codex,

    /// <summary>Found running, but reports nothing about what it is doing.</summary>
    Gemini,

    /// <summary>Found running, but reports nothing about what it is doing.</summary>
    Aider,

    /// <summary>Reports through the plugin Notch puts in opencode's plugins folder.</summary>
    OpenCode,
}

public enum AgentState
{
    Idle,
    Working,
    NeedsInput,

    /// <summary>Finished a turn that the user has not looked at yet.</summary>
    Done,
}

/// <summary>
/// Works out what a coding agent is doing from the hook events it reports and from what
/// passes through its terminal.
/// </summary>
public sealed class AgentStatusTracker(AgentKind kind)
{
    public AgentState State { get; private set; }

    public event Action? Changed;

    /// <summary>Codex reports only the end of a turn, so its "working" state is inferred from the terminal.</summary>
    private bool InfersWorkFromTerminal => kind == AgentKind.Codex;

    /// <param name="eventName">A Claude Code hook event name, or a Codex notification type.</param>
    public void OnHookEvent(string eventName)
    {
        switch (eventName)
        {
            case "UserPromptSubmit":
            case "PreToolUse":
            case "PostToolUse":
                Set(AgentState.Working);
                break;

            // Claude raises Notification when it needs a permission or has been waiting on the user.
            case "Notification":
                Set(AgentState.NeedsInput);
                break;

            case "Stop":
            case "agent-turn-complete":
                Set(AgentState.Done);
                break;

            case "SessionEnd":
                Set(AgentState.Idle);
                break;
        }
    }

    /// <summary>The user typed into the terminal.</summary>
    public void OnUserInput(bool submitted)
    {
        if (kind == AgentKind.None)
        {
            return;
        }

        // Any keystroke answers whatever the agent was waiting for.
        if (State == AgentState.NeedsInput || (submitted && InfersWorkFromTerminal))
        {
            Set(AgentState.Working);
        }
    }

    /// <summary>The terminal bell rang, which agents use to ask for attention.</summary>
    public void OnBell()
    {
        if (InfersWorkFromTerminal && State == AgentState.Working)
        {
            Set(AgentState.NeedsInput);
        }
    }

    /// <summary>The terminal has printed nothing for a while.</summary>
    public void OnOutputQuiet()
    {
        // A working Codex animates a spinner, so silence means the "work" was a false start
        // (for example Enter on an empty prompt).
        if (InfersWorkFromTerminal && State == AgentState.Working)
        {
            Set(AgentState.Idle);
        }
    }

    /// <summary>The user is looking at this session.</summary>
    public void OnViewed()
    {
        if (State == AgentState.Done)
        {
            Set(AgentState.Idle);
        }
    }

    public void Reset() => Set(AgentState.Idle);

    private void Set(AgentState state)
    {
        if (State != state)
        {
            State = state;
            Changed?.Invoke();
        }
    }
}
