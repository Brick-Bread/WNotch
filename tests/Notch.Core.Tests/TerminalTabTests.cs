using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class TerminalTabTests
{
    [Fact]
    public void The_tab_named_after_the_agents_console_is_picked()
    {
        string[] tabs = ["PowerShell", "✳ Fix the login bug", "opencode"];

        Assert.Equal(1, TerminalTabs.Pick(tabs, "✳ Fix the login bug", "app"));
        Assert.Equal(2, TerminalTabs.Pick(tabs, "opencode", null));
    }

    [Fact]
    public void A_tab_that_adds_to_the_title_or_cuts_it_short_still_matches()
    {
        Assert.Equal(0, TerminalTabs.Pick(["🔔 Fix the login bug", "PowerShell"], "Fix the login bug", null));
        Assert.Equal(1, TerminalTabs.Pick(["PowerShell", "Refactor the whole settings…"], "Refactor the whole settings window", null));
    }

    [Fact]
    public void Without_a_title_the_folder_decides()
    {
        Assert.Equal(1, TerminalTabs.Pick(["PowerShell", "C:\\code\\notch"], null, "notch"));
        Assert.Equal(-1, TerminalTabs.Pick(["PowerShell", "C:\\code\\notch"], null, "ab"));
    }

    [Fact]
    public void Several_tabs_that_fit_equally_pick_none()
    {
        Assert.Equal(-1, TerminalTabs.Pick(["claude", "claude"], "claude", null));
        Assert.Equal(-1, TerminalTabs.Pick(["notch: claude", "notch: codex"], "something else", "notch"));
        Assert.Equal(-1, TerminalTabs.Pick([], "claude", "notch"));
    }

    [Fact]
    public void A_title_no_tab_shows_falls_back_to_the_folder()
    {
        Assert.Equal(0, TerminalTabs.Pick(["my-app (main)", "PowerShell"], "node", "my-app"));
    }
}
