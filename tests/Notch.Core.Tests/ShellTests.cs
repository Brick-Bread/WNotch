using Notch.Core.Activities;
using Notch.Core.Animation;
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
        Assert.Equal(expected, IslandHitTest.Contains(1920, 0, 2560, 1.5, 180, 32, x, y));

    [Fact]
    public void Island_hit_test_margin_reaches_past_the_edge()
    {
        Assert.False(IslandHitTest.Contains(0, 0, 1920, 1, 180, 32, 871, 33));
        Assert.True(IslandHitTest.Contains(0, 0, 1920, 1, 180, 32, 869, 33, margin: 2));
    }

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
