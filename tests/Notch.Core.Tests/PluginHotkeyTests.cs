using Notch.Core.Plugins;
using Notch.Core.Shell;

namespace Notch.Core.Tests;

public class PluginHotkeyBoardTests
{
    private static Hotkey Chord(string text) => Hotkey.TryParse(text, out Hotkey hotkey) ? hotkey : throw new ArgumentException(text);

    [Fact]
    public void A_chord_held_by_Notch_or_another_plugin_is_refused()
    {
        var board = new PluginHotkeyBoard { UsedByNotch = h => h == Chord("Alt+Shift+N") };

        Assert.NotNull(board.Add("a", Chord("Alt+Shift+C"), () => { }, out _));
        Assert.Null(board.Add("b", Chord("Alt+Shift+C"), () => { }, out string? taken));
        Assert.Contains("plugin a", taken);
        Assert.Null(board.Add("b", Chord("Alt+Shift+N"), () => { }, out string? notch));
        Assert.Contains("Notch's own", notch);
        Assert.Equal(["Alt+Shift+C"], board.ChordsOf("a"));
    }

    [Fact]
    public void Pressing_calls_the_plugin_and_one_Windows_refused_is_dropped_and_reported()
    {
        var board = new PluginHotkeyBoard();
        var refused = new List<string>();
        board.Refused = (plugin, message) => refused.Add($"{plugin}: {message}");
        int pressed = 0;
        PluginHotkey hotkey = board.Add("a", Chord("Ctrl+Win+F9"), () => pressed++, out _)!;

        Assert.True(board.Press(hotkey.Id));
        Assert.Equal(1, pressed);

        board.Fail(hotkey.Id);
        Assert.False(board.Press(hotkey.Id));
        Assert.Empty(board.Snapshot());
        Assert.StartsWith("a: The hotkey", Assert.Single(refused));
    }
}
