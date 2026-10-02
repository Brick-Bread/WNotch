using System.Windows.Threading;
using Notch.Core.Hud;
using Notch.Core.Shell;
using Notch.Platform.Display;
using Notch.Platform.Input;

namespace Notch.App.Shell;

// The hotkey that opens the notch from any app, and the Caps Lock HUD.
public partial class NotchWindow
{
    private const int OpenHotkeyId = 1;

    private readonly CapsLockHudTracker _capsLock = new();

    // No keyboard hook: those slow every keystroke down and look like a cheat to some games.
    private readonly DispatcherTimer _capsLockTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private bool _hotkeyRegistered;

    /// <summary>Set when the hotkey opened the notch, which then stays open with the pointer elsewhere.</summary>
    private bool _hotkeyHold;

    /// <summary>What is wrong with the hotkey in the settings, for telling the user; null when it works or none is wanted.</summary>
    public string? HotkeyProblem { get; private set; }

    private void InitializeKeyboard()
    {
        _capsLockTimer.Tick += (_, _) =>
        {
            if (_capsLock.Update(Keyboard.IsCapsLockOn) is { } hud)
            {
                _activities.Publish(hud);
            }
        };
        _capsLockTimer.Start();
    }

    /// <summary>Registers the hotkey from the settings in place of the one registered before.</summary>
    private void RegisterOpenHotkey()
    {
        if (_hwnd == 0)
        {
            return;
        }

        if (_hotkeyRegistered)
        {
            Keyboard.UnregisterHotkey(_hwnd, OpenHotkeyId);
            _hotkeyRegistered = false;
        }

        HotkeyProblem = null;
        string text = _settings.OpenHotkey.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!Hotkey.TryParse(text, out Hotkey hotkey))
        {
            HotkeyProblem = $"\"{text}\" is not a hotkey Notch can use.";
            return;
        }

        _hotkeyRegistered = Keyboard.RegisterHotkey(_hwnd, OpenHotkeyId, hotkey);
        HoverTrace.Write($"hotkey {hotkey} registered={_hotkeyRegistered}");
        if (!_hotkeyRegistered)
        {
            HotkeyProblem = $"{hotkey} is already used by Windows or another app.";
        }
    }

    /// <summary>The hotkey opens the notch and holds it open; pressed again, it closes it.</summary>
    private void OnOpenHotkey()
    {
        HoverTrace.Write($"hotkey pressed, expanded={_expanded}");
        if (_hiddenForFullscreen)
        {
            return;
        }

        _hoverTimer.Stop();
        if (!_expanded)
        {
            _hotkeyHold = true;
            SetExpanded(true);
            return;
        }

        // Give the keyboard back first if the notch has it, as closing the timer's entry box does.
        bool handBack = IsActive && _lastOtherWindow != 0;
        SetExpanded(false);
        if (handBack)
        {
            OverlayWindow.SetForeground(_lastOtherWindow);
        }
    }
}
