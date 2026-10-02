using System.Windows;
using Notch.Core.Activities;
using Notch.Core.Automation;
using Notch.Core.Widgets;

namespace Notch.App.Shell;

// What the notch does when asked from outside: a notch:// link, notchctl, the webhook or the palette.
// All of it runs on the UI thread.
public partial class NotchWindow
{
    private const string CommandNoticeId = "notice.command";

    /// <summary>Starts a plain countdown, replacing whatever the timer was doing.</summary>
    public void StartTimer(TimeSpan duration)
    {
        _pomodoro = null;
        _timerName = null;
        StartCountdown(duration);
    }

    /// <summary>Stops, pauses or resumes the timer. Does nothing when that does not apply.</summary>
    public void ControlTimer(TimerAction action)
    {
        switch (action)
        {
            case TimerAction.Stop:
                _pomodoro = null;
                _timerName = null;
                _countdown.Reset();
                break;
            case TimerAction.Pause when _countdown.State == CountdownState.Running:
                _countdown.Pause();
                break;
            case TimerAction.Resume when _countdown.State == CountdownState.Paused:
                _countdown.Resume();
                break;
        }

        UpdateTimer();
    }

    /// <summary>Shows a short notice in the pill. A new one replaces the last.</summary>
    public void ShowNotice(ToastCommand toast) => _activities.Publish(new Activity
    {
        Id = CommandNoticeId,
        Tier = ActivityTier.Transient,
        Title = toast.Title,
        Detail = toast.Detail,
        Glyph = toast.Glyph ?? "",
        Glow = toast.Color is { } color ? new Glow(color, GlowPattern.Flash) : null,
        Lifetime = toast.Lifetime ?? TimeSpan.FromSeconds(4),
    });

    /// <summary>Opens the notch on a tab and keeps it open, like the hotkey does.</summary>
    public void OpenOnTab(string tab)
    {
        ShowTab(tab);
        RevealNotch();
    }

    // For the website's pictures (--demo --screenshots): made-up content, never read from the machine.

    internal void SeedDemoAgents()
    {
        _agents.Set(new Notch.Core.Agents.AgentEntry("demo1", "claude", "Claude", "", Notch.Core.Agents.AgentState.Working, "website", Notch.Core.Agents.AgentSource.Detected, 1, "Windows Terminal"));
        _agents.Set(new Notch.Core.Agents.AgentEntry("demo2", "codex", "Codex", "", Notch.Core.Agents.AgentState.NeedsInput, "api", Notch.Core.Agents.AgentSource.Detected, 2, "VS Code"));
        _agents.Set(new Notch.Core.Agents.AgentEntry("demo3", "gemini", "Gemini", "", Notch.Core.Agents.AgentState.Idle, "notes", Notch.Core.Agents.AgentSource.Detected, 3, "Windows Terminal"));
        UpdateAgentCard();
    }

    internal void ClearDemoAgents()
    {
        foreach (string key in new[] { "demo1", "demo2", "demo3" })
        {
            _agents.Remove(key);
        }

        UpdateAgentCard();
    }

    internal void SeedDemoClipboard()
    {
        _clipboardDemo = true;
        TabClipboard.Visibility = Visibility.Visible;
        DateTime now = DateTime.Now;
        _clipboard.AddText("git rebase --onto main feature~3 feature", now.AddMinutes(-1), "WindowsTerminal");
        Notch.Core.Clipboard.ClipboardItem? pinned = _clipboard.AddText("Thanks for getting back to me. I'll send the report over by Friday.", now.AddMinutes(-8), "Outlook");
        _clipboard.AddFiles([@"C:\Demo\Quarterly report.pdf", @"C:\Demo\budget.xlsx"], now.AddMinutes(-15), "explorer");
        _clipboard.AddText("https://example.com/docs/getting-started", now.AddMinutes(-40), "msedge");
        if (pinned is not null)
        {
            _clipboard.SetPinned(pinned.Id, true);
        }

        UpdateClipboardList();
    }

    /// <summary>Opens the palette with <paramref name="text"/> typed in, or closes it when null.</summary>
    internal void ShowDemoPalette(string? text)
    {
        if (text is null)
        {
            ClosePalette(handBack: false);
            return;
        }

        if (!PaletteShowing)
        {
            TogglePalette();
        }

        PaletteInput.Text = text;
    }

    /// <summary>Opens the notch and holds it open with the pointer elsewhere. Does nothing while a fullscreen app has it hidden.</summary>
    public void RevealNotch()
    {
        if (_hiddenForFullscreen)
        {
            return;
        }

        _hoverTimer.Stop();
        _hotkeyHold = true;
        SetExpanded(true);
    }
}
