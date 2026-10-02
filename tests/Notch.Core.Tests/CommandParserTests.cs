using Notch.Core.Activities;
using Notch.Core.Automation;

namespace Notch.Core.Tests;

public class CommandParserTests
{
    private static NotchCommand Url(string url)
    {
        Assert.True(CommandParser.TryParseUrl(url, out NotchCommand? command, out string? error), error);
        return command!;
    }

    private static NotchCommand Args(params string[] arguments)
    {
        Assert.True(CommandParser.TryParseArguments(arguments, out NotchCommand? command, out string? error), error);
        return command!;
    }

    [Fact]
    public void An_install_link_carries_only_a_plugin_id()
    {
        Assert.Equal(new InstallPluginCommand("acme.build-status"), Url("notch://install?id=acme.build-status"));
    }

    [Theory]
    [InlineData("notch://install")]
    [InlineData("notch://install?id=")]
    [InlineData("notch://install?id=Bad%20Id")]
    [InlineData("notch://install?id=UPPER")]
    [InlineData("notch://install?id=../x")]
    [InlineData("notch://teleport?id=a")]
    [InlineData("http://install?id=a")]
    [InlineData("notch://timer?d=0")]
    [InlineData("notch://timer?d=48h")]
    [InlineData("notch://timer?d=soon")]
    [InlineData("notch://open?tab=secrets")]
    [InlineData("notch://plugin?id=a")]
    [InlineData("")]
    public void Anything_unknown_or_malformed_is_refused(string url)
    {
        Assert.False(CommandParser.TryParseUrl(url, out NotchCommand? command, out string? error));
        Assert.Null(command);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void An_oversized_link_is_refused()
    {
        Assert.False(CommandParser.TryParseUrl("notch://toast?title=" + new string('a', CommandParser.MaxInputLength), out _, out _));
    }

    [Fact]
    public void A_timer_takes_the_usual_durations_and_controls()
    {
        Assert.Equal(new StartTimerCommand(TimeSpan.FromMinutes(5)), Url("notch://timer?d=5m"));
        Assert.Equal(new StartTimerCommand(TimeSpan.FromSeconds(90)), Args("timer", "90s"));
        Assert.Equal(new StartTimerCommand(TimeSpan.FromMinutes(80)), Args("timer", "--d", "1h20m"));
        Assert.Equal(new TimerActionCommand(TimerAction.Stop), Args("timer", "stop"));
        Assert.Equal(new TimerActionCommand(TimerAction.Pause), Url("notch://timer?action=pause"));
        Assert.Equal(new TimerActionCommand(TimerAction.Resume), Args("timer", "resume"));
    }

    [Fact]
    public void A_toast_reads_its_options_from_the_command_line()
    {
        var toast = Assert.IsType<ToastCommand>(Args("toast", "Build done", "--detail", "all green", "--color", "green", "--lifetime", "5s"));
        Assert.Equal("Build done", toast.Title);
        Assert.Equal("all green", toast.Detail);
        Assert.Equal(GlowColor.Green, toast.Color);
        Assert.Equal(TimeSpan.FromSeconds(5), toast.Lifetime);
    }

    [Fact]
    public void A_toast_lifetime_is_capped_and_colours_may_be_hex()
    {
        var toast = Assert.IsType<ToastCommand>(Url("notch://toast?title=Hi&color=%23ff0000&lifetime=1h"));
        Assert.Equal(new GlowColor(255, 0, 0), toast.Color);
        Assert.Equal(CommandParser.MaxToastLifetime, toast.Lifetime);
        Assert.False(CommandParser.TryParseUrl("notch://toast?title=Hi&color=chartreuse-ish", out _, out _));
        Assert.False(CommandParser.TryParseUrl("notch://toast?title=" + new string('a', CommandParser.MaxTitleLength + 1), out _, out _));
    }

    [Fact]
    public void Tabs_are_limited_to_the_known_ones()
    {
        Assert.Equal(new OpenTabCommand("stats"), Url("notch://open?tab=Stats"));
        Assert.Equal(new OpenTabCommand("clipboard"), Args("open", "clipboard"));
    }

    [Fact]
    public void Plugins_can_be_switched_by_id()
    {
        Assert.Equal(new SetPluginEnabledCommand("acme.x", true), Url("notch://plugin?id=acme.x&enable=true"));
        Assert.Equal(new SetPluginEnabledCommand("acme.x", false), Args("plugin", "acme.x", "--enable", "off"));
    }

    [Fact]
    public void Verbs_without_parameters_work_and_case_does_not_matter()
    {
        Assert.IsType<OpenPaletteCommand>(Url("NOTCH://PALETTE"));
        Assert.IsType<OpenSettingsCommand>(Args("SETTINGS"));
    }

    [Fact]
    public void The_command_line_needs_a_verb()
    {
        Assert.False(CommandParser.TryParseArguments([], out _, out _));
        Assert.False(CommandParser.TryParseArguments(["fly"], out _, out _));
    }

    [Theory]
    [InlineData("acme.tool", true)]
    [InlineData("a", true)]
    [InlineData("acme-tool.v2", true)]
    [InlineData("Acme", false)]
    [InlineData("a..b", false)]
    [InlineData(".a", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void Plugin_ids_follow_the_manifest_rule(string id, bool valid)
    {
        Assert.Equal(valid, CommandParser.IsPluginId(id));
    }
}
