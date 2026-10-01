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
