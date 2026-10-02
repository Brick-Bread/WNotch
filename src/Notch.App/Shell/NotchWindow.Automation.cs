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
