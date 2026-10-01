using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Hud;

namespace Notch.Core.Tests;

public class GlowTests
{
    [Fact]
    public void Flash_starts_bright_and_settles_low()
    {
        var glow = new Glow(GlowColor.White, GlowPattern.Flash);

        Assert.Equal(1, glow.IntensityAt(0), precision: 6);
        Assert.Equal(0.35, glow.IntensityAt(5), precision: 6);
        Assert.True(glow.IntensityAt(0.3) > glow.IntensityAt(0.9));
    }

    [Theory]
    [InlineData(GlowPattern.Breathe, 3.2)]
    [InlineData(GlowPattern.Pulse, 1.1)]
    public void Waves_repeat_and_stay_in_range(GlowPattern pattern, double period)
    {
        var glow = new Glow(GlowColor.Cyan, pattern);

        for (double t = 0; t < period * 3; t += 0.05)
        {
            Assert.InRange(glow.IntensityAt(t), 0, 1);
        }

        Assert.Equal(glow.IntensityAt(0.4), glow.IntensityAt(0.4 + period), precision: 6);
        Assert.True(glow.IntensityAt(period / 2) > glow.IntensityAt(0));
    }

    [Fact]
    public void Audio_glow_follows_the_level()
    {
        var glow = new Glow(GlowColor.Violet, GlowPattern.Audio);

        Assert.True(glow.IntensityAt(1, audioLevel: 0.9) > glow.IntensityAt(1, audioLevel: 0.1));
        Assert.Equal(1, glow.IntensityAt(1, audioLevel: 5), precision: 6);
    }

    [Fact]
    public void Strength_scales_brightness() =>
        Assert.Equal(0.4, new Glow(GlowColor.White, GlowPattern.Steady, 0.5).IntensityAt(0), precision: 6);

    [Fact]
    public void Output_lifts_dim_glows_but_keeps_their_order()
    {
        Assert.Equal(0, GlowOutput.Level(0, 1), precision: 6);
        Assert.Equal(1, GlowOutput.Level(1, 1), precision: 6);
        Assert.True(GlowOutput.Level(0.25, 1) > 0.4);
        Assert.True(GlowOutput.Level(0.25, 1) < GlowOutput.Level(0.5, 1));
    }

    [Fact]
    public void Output_scales_with_the_brightness_setting()
    {
        Assert.Equal(2 * GlowOutput.Level(0.5, 1), GlowOutput.Level(0.5, GlowOutput.Gain(200)), precision: 6);
        Assert.Equal(1, GlowOutput.Gain(GlowOutput.DefaultPercent));
        Assert.Equal(0.25, GlowOutput.Gain(-5));
        Assert.Equal(2, GlowOutput.Gain(1000));
    }

    [Fact]
    public void Colours_blend() =>
        Assert.Equal(new GlowColor(100, 64, 0), new GlowColor(0, 0, 0).Lerp(new GlowColor(200, 128, 0), 0.5));

    [Fact]
    public void Agent_states_have_distinct_glows()
    {
        Glow working = AgentActivities.For("s", "Claude", "g", AgentState.Working)!.Glow!;
        Glow waiting = AgentActivities.For("s", "Claude", "g", AgentState.NeedsInput)!.Glow!;
        Glow done = AgentActivities.For("s", "Claude", "g", AgentState.Done)!.Glow!;

        Assert.Equal(GlowPattern.Breathe, working.Pattern);
        Assert.Equal(GlowPattern.Pulse, waiting.Pattern);
        Assert.Equal(GlowColor.Green, done.Color);
        Assert.NotEqual(working.Color, waiting.Color);
    }

    [Fact]
    public void Hud_glows_match_their_meaning()
    {
        Assert.Equal(GlowColor.Green, HudActivities.Power(pluggedIn: true, 50).Glow!.Color);
        Assert.Equal(GlowPattern.Pulse, HudActivities.LowBattery(8).Glow!.Pattern);
        Assert.True(HudActivities.Volume(1, muted: false).Glow!.Strength > HudActivities.Volume(0.1, muted: false).Glow!.Strength);
    }

    [Fact]
    public void Accent_picks_the_vivid_colour_over_grey()
    {
        // Mostly grey pixels with a few strong blue ones.
        var pixels = new List<byte>();
        for (int i = 0; i < 90; i++)
        {
            pixels.AddRange([128, 128, 128, 255]);
        }

        for (int i = 0; i < 10; i++)
        {
            pixels.AddRange([220, 60, 20, 255]);
        }

        GlowColor accent = AccentColor.FromPixels(pixels.ToArray())!.Value;

        Assert.Equal(255, accent.B);
        Assert.True(accent.R < 60 && accent.G < 100);
    }

    [Fact]
    public void Greyscale_images_have_no_accent() =>
        Assert.Null(AccentColor.FromPixels([50, 50, 50, 255, 200, 200, 200, 255]));

    [Theory]
    [InlineData("Blue", true)]
    [InlineData("violet", true)]
    [InlineData("None", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Presets_are_found_by_name(string? name, bool found) =>
        Assert.Equal(found, GlowColor.FromName(name) is not null);

    [Fact]
    public void Every_named_preset_resolves_to_itself()
    {
        foreach ((string name, GlowColor color) in GlowColor.Named)
        {
            Assert.Equal(color, GlowColor.FromName(name));
        }
    }

    [Fact]
    public void On_light_deepens_every_channel()
    {
        GlowColor deep = GlowColor.Yellow.OnLight();

        Assert.True(deep.R < GlowColor.Yellow.R && deep.G < GlowColor.Yellow.G && deep.B < GlowColor.Yellow.B);
    }
}
