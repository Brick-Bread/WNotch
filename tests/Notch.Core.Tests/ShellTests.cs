using Notch.Core.Activities;
using Notch.Core.Animation;
using Notch.Core.Settings;
using Notch.Core.Shell;

namespace Notch.Core.Tests;

public class ShellTests
{
    private static Activity Make(ActivityTier tier) => new() { Id = tier.ToString(), Tier = tier, Title = "x" };

    [Fact]
    public void No_activities_is_idle() =>
        Assert.Equal(NotchMode.Idle, NotchModeResolver.Resolve(expanded: false, []));

    [Theory]
    [InlineData(ActivityTier.Ongoing, NotchMode.Compact)]
    [InlineData(ActivityTier.Attention, NotchMode.Compact)]
    [InlineData(ActivityTier.Transient, NotchMode.Peek)]
    public void Top_activity_picks_the_mode(ActivityTier tier, NotchMode expected) =>
        Assert.Equal(expected, NotchModeResolver.Resolve(expanded: false, [Make(tier)]));

    [Fact]
    public void Expanded_wins_over_activities() =>
        Assert.Equal(NotchMode.Expanded, NotchModeResolver.Resolve(expanded: true, [Make(ActivityTier.Transient)]));

    [Theory]
    [InlineData("Win+Shift+N", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x4E, "Shift+Win+N")]
    [InlineData(" ctrl + alt + space ", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x20, "Ctrl+Alt+Space")]
    [InlineData("Control+7", HotkeyModifiers.Ctrl, 0x37, "Ctrl+7")]
    [InlineData("windows+f12", HotkeyModifiers.Win, 0x7B, "Win+F12")]
    public void Hotkey_is_read_as_typed_and_written_tidily(string text, HotkeyModifiers modifiers, int key, string tidy)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));

        Assert.Equal(new Hotkey(modifiers, key), hotkey);
        Assert.Equal(tidy, hotkey.ToString());
    }

    // Nothing, no key, a key Notch does not offer, an unknown modifier, and keys that would be taken away from typing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+F25")]
    [InlineData("Hyper+N")]
    [InlineData("N")]
    [InlineData("Shift+N")]
    public void Hotkey_that_cannot_be_used_is_refused(string? text) =>
        Assert.False(Hotkey.TryParse(text, out _));

    // A 2560-wide display at 150%, to the right of another one; the idle pill is 180 x 32 DIPs.
    [Theory]
    [InlineData(3200, 0, true)]
    [InlineData(3066, 47, true)]
    [InlineData(3334, 47, true)]
    [InlineData(3064, 10, false)]
    [InlineData(3335, 10, false)]
    [InlineData(3200, 48, false)]
    [InlineData(3200, -1, false)]
    public void Island_hit_test_follows_the_display(int x, int y, bool expected) =>
        Assert.Equal(expected, new IslandPlacement(NotchPosition.TopCenter, NotchStyle.Notch).Contains(1920, 0, 4480, 1440, 1.5, 180, 32, x, y));

    [Fact]
    public void Island_hit_test_margin_reaches_past_the_edge()
    {
        var notch = new IslandPlacement(NotchPosition.TopCenter, NotchStyle.Notch);

        Assert.False(notch.Contains(0, 0, 1920, 1080, 1, 180, 32, 871, 33));
        Assert.True(notch.Contains(0, 0, 1920, 1080, 1, 180, 32, 869, 33, margin: 2));
    }

    [Fact]
    public void A_floating_island_keeps_clear_of_the_edge_but_is_reached_through_the_gap()
    {
        var island = new IslandPlacement(NotchPosition.TopCenter, NotchStyle.Island);

        Assert.Equal(IslandPlacement.TopGap, island.EdgeGap);
        Assert.Equal(0, new IslandPlacement(NotchPosition.TopCenter, NotchStyle.Notch).EdgeGap);

        // The very top row of pixels, the island itself, and just below it.
        Assert.True(island.Contains(0, 0, 1920, 1080, 1, 180, 32, 960, 0));
        Assert.True(island.Contains(0, 0, 1920, 1080, 1, 180, 32, 960, 39));
        Assert.False(island.Contains(0, 0, 1920, 1080, 1, 180, 32, 960, 40));
    }

    // A 1920 x 1080 display at 100% with the usual 48-high taskbar; the idle pill is 180 x 32.
    [Theory]
    [InlineData(NotchStyle.Island, 12, 1079, true)]
    [InlineData(NotchStyle.Island, 191, 1040, true)]
    [InlineData(NotchStyle.Island, 192, 1050, false)]
    [InlineData(NotchStyle.Island, 11, 1050, false)]
    [InlineData(NotchStyle.Island, 100, 1039, false)]
    [InlineData(NotchStyle.Notch, 100, 1048, true)]
    [InlineData(NotchStyle.Notch, 100, 1047, false)]
    [InlineData(NotchStyle.Notch, 960, 0, false)]
    public void In_the_taskbar_the_island_sits_bottom_left(NotchStyle style, int x, int y, bool expected) =>
        Assert.Equal(expected, new IslandPlacement(NotchPosition.TaskbarLeft, style, 48).Contains(0, 0, 1920, 1080, 1, 180, 32, x, y));

    [Theory]
    [InlineData(48, 8)]
    [InlineData(72, 20)]
    [InlineData(30, 4)]
    public void A_floating_island_is_centred_in_the_taskbar(double taskbarHeight, double gap) =>
        Assert.Equal(gap, new IslandPlacement(NotchPosition.TaskbarLeft, NotchStyle.Island, taskbarHeight).EdgeGap);

    private static readonly StackedWindow Fullscreen = new(CoversDisplay: true, IsTopmost: false);
    private static readonly StackedWindow Ordinary = new(CoversDisplay: false, IsTopmost: false);
    private static readonly StackedWindow Pinned = new(CoversDisplay: false, IsTopmost: true);

    [Fact]
    public void A_fullscreen_app_on_top_covers_the_display()
    {
        Assert.True(FullscreenRule.IsCovered([Fullscreen, Ordinary]));
        Assert.True(FullscreenRule.IsCovered([new StackedWindow(CoversDisplay: true, IsTopmost: true)]));
    }

    [Fact]
    public void Always_on_top_windows_above_a_game_are_looked_past() =>
        Assert.True(FullscreenRule.IsCovered([Pinned, Pinned, Fullscreen]));

    [Fact]
    public void An_ordinary_window_over_a_fullscreen_app_uncovers_the_display()
    {
        Assert.False(FullscreenRule.IsCovered([Ordinary, Fullscreen]));
        Assert.False(FullscreenRule.IsCovered([Pinned, Ordinary, Fullscreen]));
    }

    [Fact]
    public void A_display_without_a_fullscreen_app_is_not_covered()
    {
        Assert.False(FullscreenRule.IsCovered([]));
        Assert.False(FullscreenRule.IsCovered([Pinned]));
        Assert.False(FullscreenRule.IsCovered([Ordinary, Ordinary]));
    }

    [Fact]
    public void Spring_settles_on_its_target()
    {
        var spring = new Spring(180) { Target = 640 };

        for (int frame = 0; frame < 240 && !spring.IsSettled; frame++)
        {
            spring.Step(1.0 / 60.0);
        }

        Assert.True(spring.IsSettled);
        Assert.Equal(640, spring.Value);
    }

    [Fact]
    public void Spring_survives_a_long_frame()
    {
        var spring = new Spring(0) { Target = 100 };

        spring.Step(5);

        Assert.InRange(spring.Value, 0, 200);
    }
}
