namespace Notch.Core.Shell;

/// <summary>The keys held with a hotkey's main key. The values are the ones Windows uses when registering one.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A key combination that works from any app, written the way it is typed in settings: <c>Alt+Shift+N</c>.</summary>
/// <param name="VirtualKey">The Windows virtual-key code of the main key.</param>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const int Space = 0x20;
    private const int F1 = 0x70;
    private const int LastFunctionKey = 24;

    /// <summary>
    /// Reads modifiers and one key joined by "+": a letter, a digit, F1 to F24 or Space, with at
    /// least one of Ctrl, Alt and Win, since Shift alone would take a key away from typing.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            HotkeyModifiers? modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => null,
            };
            if (modifier is null)
            {
                return false;
            }

            modifiers |= modifier.Value;
        }

        if ((modifiers & ~HotkeyModifiers.Shift) == HotkeyModifiers.None || KeyCode(parts[^1]) is not { } key)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>The way <see cref="TryParse"/> reads it, tidied: <c>Ctrl+Alt+Shift+Win</c> order, the key last.</summary>
    public override string ToString()
    {
        List<string> parts = [];
        foreach (HotkeyModifiers modifier in (HotkeyModifiers[])[HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win])
        {
            if (Modifiers.HasFlag(modifier))
            {
                parts.Add(modifier.ToString());
            }
        }

        parts.Add(VirtualKey switch
        {
            Space => "Space",
            >= F1 and < F1 + LastFunctionKey => "F" + (VirtualKey - F1 + 1),
            _ => ((char)VirtualKey).ToString(),
        });
        return string.Join('+', parts);
    }

    private static int? KeyCode(string name)
    {
        string key = name.ToUpperInvariant();
        if (key.Length == 1 && key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            // Letters and digits have their character's code.
            return key[0];
        }

        if (key == "SPACE")
        {
            return Space;
        }

        return key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int number) && number is >= 1 and <= LastFunctionKey
            ? F1 + number - 1
            : null;
    }
}
