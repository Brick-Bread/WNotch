using Notch.Core.Shell;

namespace Notch.Core.Plugins;

/// <summary>Keyboard shortcuts that work from any app, like Notch's own Alt+Shift+N. API version 9.</summary>
public interface IPluginHotkeys
{
    /// <summary>
    /// Calls <paramref name="pressed"/>, on a background thread, whenever the user presses <paramref name="chord"/>
    /// in any app. The chord is written as in Notch's settings: modifiers and a key joined by "+", such as
    /// "Alt+Shift+C" or "Ctrl+Win+F9". Dispose the result to give the keys back; Notch also does when the plugin stops.
    /// </summary>
    /// <remarks>
    /// A chord Notch or another plugin already uses is refused, and so is one Windows or another app holds; the
    /// refusal is written to the plugin log, and the result then does nothing. Settings lists the plugin's chords.
    /// </remarks>
    /// <exception cref="ArgumentException">The chord is not one Notch can read.</exception>
    IDisposable Register(string chord, Action pressed);
}

/// <summary>One plugin's hotkey.</summary>
/// <param name="Id">Unique while Notch runs; the shell registers it with Windows under a number made from it.</param>
public sealed record PluginHotkey(int Id, string PluginId, Hotkey Hotkey, Action Pressed);

/// <summary>
/// The hotkeys plugins asked for. Plugins write through <see cref="IPluginHotkeys"/>; the shell registers them with
/// Windows when <see cref="Changed"/> fires and calls <see cref="Press"/> when one is pressed.
/// </summary>
public sealed class PluginHotkeyBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, PluginHotkey> _hotkeys = [];
    private int _next;

    /// <summary>Raised on whichever thread caused the change.</summary>
    public event EventHandler? Changed;

    /// <summary>Set by the shell: whether Notch itself uses a chord (its open and palette hotkeys).</summary>
    public Func<Hotkey, bool>? UsedByNotch { get; set; }

    /// <summary>Told, with the plugin's id and a sentence for the log, when the shell could not get a hotkey from Windows.</summary>
    public Action<string, string>? Refused { get; set; }

    /// <summary>Adds a hotkey, or says why it cannot be had.</summary>
    public PluginHotkey? Add(string pluginId, Hotkey hotkey, Action pressed, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        refusal = null;
        if (UsedByNotch?.Invoke(hotkey) == true)
        {
            refusal = $"{hotkey} is one of Notch's own hotkeys.";
            return null;
        }

        PluginHotkey added;
        lock (_gate)
        {
            if (_hotkeys.Values.FirstOrDefault(h => h.Hotkey == hotkey) is { } taken)
            {
                refusal = taken.PluginId == pluginId ? $"{hotkey} is registered already." : $"{hotkey} is used by the plugin {taken.PluginId}.";
                return null;
            }

            added = new PluginHotkey(++_next, pluginId, hotkey, pressed);
            _hotkeys[added.Id] = added;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public void Remove(int id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _hotkeys.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveAll(string pluginId)
    {
        bool removed = false;
        lock (_gate)
        {
            foreach (int id in _hotkeys.Values.Where(h => h.PluginId == pluginId).Select(h => h.Id).ToList())
            {
                removed |= _hotkeys.Remove(id);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<PluginHotkey> Snapshot()
    {
        lock (_gate)
        {
            return [.. _hotkeys.Values.OrderBy(h => h.Id)];
        }
    }

    /// <summary>The chords a plugin holds, for Settings.</summary>
    public IReadOnlyList<string> ChordsOf(string pluginId) => [.. Snapshot().Where(h => h.PluginId == pluginId).Select(h => h.Hotkey.ToString())];

    /// <summary>The shell saw the hotkey pressed. False when there is no such hotkey any more.</summary>
    public bool Press(int id)
    {
        PluginHotkey? hotkey;
        lock (_gate)
        {
            _hotkeys.TryGetValue(id, out hotkey);
        }

        hotkey?.Pressed();
        return hotkey is not null;
    }

    /// <summary>The shell could not register the hotkey with Windows; it is dropped, and its plugin's log says why.</summary>
    public void Fail(int id)
    {
        PluginHotkey? hotkey;
        lock (_gate)
        {
            if (!_hotkeys.Remove(id, out hotkey))
            {
                return;
            }
        }

        Refused?.Invoke(hotkey.PluginId, $"The hotkey {hotkey.Hotkey} could not be registered: Windows or another app uses it.");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
