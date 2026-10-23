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

    // Plugins' hotkeys are registered with Windows under this number plus their own.
    private const int PluginHotkeyIdBase = 1000;

    private readonly HashSet<int> _pluginHotkeysRegistered = [];

    /// <summary>The hotkeys plugins hold; set by the app.</summary>
    internal Notch.Core.Plugins.PluginHotkeyBoard? PluginHotkeys
    {
        get => _pluginHotkeys;
        set
        {
            if (_pluginHotkeys is not null)
            {
                _pluginHotkeys.Changed -= OnPluginHotkeysChanged;
            }

            _pluginHotkeys = value;
            if (value is not null)
            {
                // Asked while a plugin starts, off the UI thread; it only reads two strings of the settings.
                value.UsedByNotch = IsNotchHotkey;
                value.Changed += OnPluginHotkeysChanged;
            }

            SyncPluginHotkeys();
        }
    }

    private Notch.Core.Plugins.PluginHotkeyBoard? _pluginHotkeys;

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

    private bool IsNotchHotkey(Hotkey hotkey) =>
        (Hotkey.TryParse(_settings.OpenHotkey, out Hotkey open) && open == hotkey)
        || (Hotkey.TryParse(_settings.PaletteHotkey, out Hotkey palette) && palette == hotkey);

    private void OnPluginHotkeysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(SyncPluginHotkeys);

    /// <summary>Registers plugins' new hotkeys with Windows and gives back the ones they let go of.</summary>
    private void SyncPluginHotkeys()
    {
        if (_hwnd == 0 || _pluginHotkeys is not { } board)
        {
            return;
        }

        IReadOnlyList<Notch.Core.Plugins.PluginHotkey> wanted = board.Snapshot();
        foreach (int id in _pluginHotkeysRegistered.Except(wanted.Select(h => h.Id)).ToList())
        {
            Keyboard.UnregisterHotkey(_hwnd, PluginHotkeyIdBase + id);
            _pluginHotkeysRegistered.Remove(id);
        }

        foreach (Notch.Core.Plugins.PluginHotkey hotkey in wanted.Where(h => !_pluginHotkeysRegistered.Contains(h.Id)))
        {
            if (Keyboard.RegisterHotkey(_hwnd, PluginHotkeyIdBase + hotkey.Id, hotkey.Hotkey))
            {
                _pluginHotkeysRegistered.Add(hotkey.Id);
            }
            else
            {
                board.Fail(hotkey.Id);
            }
        }
    }

    /// <summary>A hotkey arrived; true when it was one of a plugin's.</summary>
    private bool OnPluginHotkey(nint id) =>
        id > PluginHotkeyIdBase && _pluginHotkeys?.Press((int)(id - PluginHotkeyIdBase)) == true;

    private void UnregisterPluginHotkeys()
    {
        foreach (int id in _pluginHotkeysRegistered)
        {
            Keyboard.UnregisterHotkey(_hwnd, PluginHotkeyIdBase + id);
        }

        _pluginHotkeysRegistered.Clear();
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
