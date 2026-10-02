using Notch.Core.Automation;

namespace Notch.Core.Tests;

public class ForwardedLaunchTests
{
    [Fact]
    public void Starting_notch_by_hand_brings_it_up()
    {
        Assert.True(ForwardedLaunch.ShouldReveal([]));
        Assert.True(ForwardedLaunch.ShouldReveal(["--settings"]));
        Assert.True(ForwardedLaunch.ShouldReveal(["--tab=stats"]));
    }

    [Theory]
    [InlineData("--updated")]
    [InlineData("--UPDATED")]
    [InlineData("--wait-for=1234")]
    public void A_copy_the_installer_or_a_restart_started_stays_quiet(string flag)
    {
        Assert.False(ForwardedLaunch.ShouldReveal([flag]));
        Assert.False(ForwardedLaunch.ShouldReveal(["--tab=home", flag]));
    }
}
