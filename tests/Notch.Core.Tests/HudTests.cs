using Notch.Core.Activities;
using Notch.Core.Hud;

namespace Notch.Core.Tests;

public class HudTests
{
    [Fact]
    public void Muted_volume_shows_an_empty_bar()
    {
        Activity hud = HudActivities.Volume(0.8, muted: true);

        Assert.Equal("Muted", hud.Title);
        Assert.Equal(0, hud.Progress);
        Assert.Equal(ActivityTier.Transient, hud.Tier);
    }

    [Fact]
    public void Volume_is_clamped() =>
        Assert.Equal(1, HudActivities.Volume(1.4, muted: false).Progress);

    [Fact]
    public void First_power_reading_is_silent() =>
        Assert.Null(new PowerHudTracker().Update(hasBattery: true, pluggedIn: true, percent: 80));

    [Fact]
    public void Plugging_and_unplugging_show_a_hud()
    {
        var tracker = new PowerHudTracker();
        tracker.Update(hasBattery: true, pluggedIn: false, percent: 80);

        Assert.Equal("Charging", tracker.Update(true, pluggedIn: true, 80)?.Title);
        Assert.Null(tracker.Update(true, pluggedIn: true, 81));
        Assert.Equal("On battery", tracker.Update(true, pluggedIn: false, 81)?.Title);
    }

    [Fact]
    public void Low_battery_fires_once_per_mark()
    {
        var tracker = new PowerHudTracker();
        tracker.Update(hasBattery: true, pluggedIn: false, percent: 22);

        Assert.Null(tracker.Update(true, false, 21));
        Assert.Equal("Low battery", tracker.Update(true, false, 20)?.Title);
        Assert.Null(tracker.Update(true, false, 19));
        Assert.Equal("Low battery", tracker.Update(true, false, 10)?.Title);
    }

    [Fact]
    public void Low_battery_is_not_reported_while_charging()
    {
        var tracker = new PowerHudTracker();
        tracker.Update(hasBattery: true, pluggedIn: true, percent: 21);

        Assert.Null(tracker.Update(true, true, 19));
    }

    [Fact]
    public void Machines_without_a_battery_never_report() =>
        Assert.Null(new PowerHudTracker().Update(hasBattery: false, pluggedIn: true, percent: 100));
}
