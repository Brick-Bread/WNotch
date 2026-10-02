using Notch.Core.Agents;
using Notch.Core.Terminal;

namespace Notch.Core.Tests;

public class OpenCodeDetectionTests
{
    private const string Exe = @"C:\Users\me\AppData\Roaming\npm\node_modules\@opencode\cli\bin\opencode.exe";

    private static ProcessSnapshot Process(string name, string commandLine) =>
        new(10, 1, name, commandLine, name.EndsWith(".exe") ? Exe : null);

    [Theory]
    [InlineData("opencode.exe", "\"" + Exe + "\"")]
    [InlineData("opencode.exe", "\"" + Exe + "\" C:\\work\\project")]
    [InlineData("opencode.exe", "\"" + Exe + "\" --continue")]
    [InlineData("opencode.exe", "\"" + Exe + "\" -s ses_123")]
    [InlineData("opencode.exe", "\"" + Exe + "\" --prompt \"please run the tests\"")]
    [InlineData("opencode.exe", "\"" + Exe + "\" --prompt run")]
    [InlineData("opencode.exe", "\"" + Exe + "\" --standalone")]
    public void An_opencode_session_is_recognised(string name, string commandLine)
    {
        AgentDefinition? agent = AgentCatalog.Match(Process(name, commandLine));

        Assert.Equal(AgentKind.OpenCode, agent?.Kind);
    }

    [Theory]
    [InlineData("\"" + Exe + "\" serve --stdio --port 0")]
    [InlineData("\"" + Exe + "\" service run")]
    [InlineData("\"" + Exe + "\" run \"fix the bug\"")]
    [InlineData("\"" + Exe + "\" --standalone run hello")]
    [InlineData("\"" + Exe + "\" acp")]
    [InlineData("\"" + Exe + "\" models")]
    [InlineData("\"" + Exe + "\" plugin list")]
    [InlineData("\"" + Exe + "\" api GET /session")]
    public void The_background_server_and_one_off_commands_are_not_sessions(string commandLine)
    {
        Assert.Null(AgentCatalog.Match(Process("opencode.exe", commandLine)));
    }

    [Fact]
    public void An_opencode_script_run_by_node_is_recognised_unless_it_is_a_subcommand()
    {
        string script = @"node C:\Users\me\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode";

        Assert.Equal(AgentKind.OpenCode, AgentCatalog.Match(Process("node.exe", script))?.Kind);
        Assert.Null(AgentCatalog.Match(Process("node.exe", script + " serve")));
    }

    [Fact]
    public void Other_agents_are_not_affected_by_the_words_opencode_treats_as_subcommands()
    {
        var claude = new ProcessSnapshot(11, 1, "claude.exe", "claude \"please run the tests and serve the docs\"", @"C:\tools\claude.exe");

        Assert.Equal(AgentKind.Claude, AgentCatalog.Match(claude)?.Kind);
    }

    [Fact]
    public void A_launcher_button_for_opencode_is_an_opencode_agent()
    {
        Assert.True(TerminalPresets.TryParse("opencode = opencode", [], out TerminalProfile profile));
        Assert.Equal(AgentKind.OpenCode, profile.Agent);

        Assert.True(TerminalPresets.TryParse("Work = OPENCODE_CONFIG_DIR=C:\\work\\oc opencode --continue", [], out TerminalProfile work));
        Assert.Equal(AgentKind.OpenCode, work.Agent);
        Assert.Equal(AgentKind.OpenCode, AgentCatalog.ForCommand("opencode")?.Kind);
    }

    [Fact]
    public void New_installs_get_an_opencode_button_and_the_built_in_profile_matches_it()
    {
        Assert.Contains("opencode = opencode", TerminalProfile.DefaultPresets);
        Assert.Equal(AgentKind.OpenCode, TerminalProfile.OpenCode.Agent);
        Assert.Contains(TerminalProfile.OpenCode, TerminalProfile.All);
        Assert.Contains("opencode.ai", TerminalProfile.OpenCode.InstallHint);
        Assert.All(TerminalPresets.FromLines(TerminalProfile.DefaultPresets), p => Assert.NotNull(p));
    }
}

public class OpenCodeRoutingTests
{
    private static AgentDefinition OpenCode => AgentCatalog.ForKind(AgentKind.OpenCode)!;

    private static DetectedAgent Window(int pid) => new($"opencode:{pid}", pid, OpenCode, null, null, null);

    private static ProcessSnapshot Process(int pid, string? folder) =>
        new(pid, 1, "opencode.exe", "opencode", @"C:\tools\opencode.exe", null, folder);

    [Theory]
    [InlineData(@"C:\work\app", @"C:\work\app", true)]
    [InlineData(@"C:\work\app", @"c:\WORK\app\", true)]
    [InlineData(@"C:\work\app", "C:/work/app", true)]
    [InlineData(@"C:\work\app", @"C:\work\other", false)]
    [InlineData(@"C:\work\app", @"C:\work\app\sub", false)]
    [InlineData(null, @"C:\work\app", false)]
    [InlineData(@"C:\work\app", "", false)]
    public void Folders_are_compared_by_where_they_are_not_how_they_are_spelled(string? a, string? b, bool same)
    {
        Assert.Equal(same, AgentDetector.SameFolder(a, b));
    }

    [Fact]
    public void An_event_goes_to_the_window_in_its_folder()
    {
        ProcessSnapshot[] processes = [Process(1, @"C:\work\a"), Process(2, @"C:\work\b")];
        DetectedAgent[] detected = [Window(1), Window(2)];

        IReadOnlyList<DetectedAgent> targets = AgentDetector.OpenCodeIn(@"C:\work\b", processes, detected);

        Assert.Equal([2], targets.Select(t => t.Pid));
    }

    [Fact]
    public void Two_windows_in_the_same_folder_both_get_the_event()
    {
        ProcessSnapshot[] processes = [Process(1, @"C:\work\a"), Process(2, @"C:\work\a"), Process(3, @"C:\work\b")];
        DetectedAgent[] detected = [Window(1), Window(2), Window(3)];

        Assert.Equal([1, 2], AgentDetector.OpenCodeIn(@"C:\work\a", processes, detected).Select(t => t.Pid));
    }

    [Fact]
    public void An_event_for_a_folder_nobody_is_in_goes_nowhere()
    {
        ProcessSnapshot[] processes = [Process(1, @"C:\work\a"), Process(2, @"C:\work\b")];

        Assert.Empty(AgentDetector.OpenCodeIn(@"C:\elsewhere", processes, [Window(1), Window(2)]));
    }

    [Fact]
    public void A_window_whose_folder_is_unknown_is_chosen_only_when_it_is_the_only_one()
    {
        Assert.Single(AgentDetector.OpenCodeIn(@"C:\work\a", [Process(1, null)], [Window(1)]));

        // With two windows and no way to tell them apart, guessing would show the wrong one as done.
        Assert.Empty(AgentDetector.OpenCodeIn(@"C:\work\a", [Process(1, null), Process(2, null)], [Window(1), Window(2)]));
        Assert.Empty(AgentDetector.OpenCodeIn(@"C:\work\a", [Process(1, null), Process(2, @"C:\work\b")], [Window(1), Window(2)]));
    }

    [Fact]
    public void Other_agents_are_never_chosen()
    {
        AgentDefinition claude = AgentCatalog.ForKind(AgentKind.Claude)!;
        var claudeWindow = new DetectedAgent("claude:5", 5, claude, null, null, null);

        Assert.Empty(AgentDetector.OpenCodeIn(@"C:\work\a", [Process(5, @"C:\work\a")], [claudeWindow]));
    }

    [Fact]
    public void An_event_without_a_folder_is_chosen_only_for_a_single_window()
    {
        Assert.Single(AgentDetector.OpenCodeIn(null, [Process(1, null)], [Window(1)]));
        Assert.Empty(AgentDetector.OpenCodeIn(null, [Process(1, @"C:\a"), Process(2, @"C:\b")], [Window(1), Window(2)]));
    }
}

public class OpenCodeHookTests
{
    [Fact]
    public void The_plugins_message_says_which_agent_and_folder_it_came_from()
    {
        string payload = """{"hook_event_name":"Notification","cwd":"C:\\work\\app","agent":"opencode"}""";
        string line = AgentHooks.FormatMessage("", payload, hookPid: 321);

        Assert.True(AgentHooks.TryParseHookMessage(line, out AgentHooks.HookMessage? message));

        Assert.Equal("Notification", message!.EventName);
        Assert.Equal(@"C:\work\app", message.Folder);
        Assert.Equal("opencode", message.Agent);
        Assert.Equal(321, message.HookPid);
        Assert.Equal("", message.Session);
    }

    [Fact]
    public void Claude_and_codex_messages_have_no_agent_tag()
    {
        string line = AgentHooks.FormatMessage("s1", """{"hook_event_name":"Stop"}""");

        Assert.True(AgentHooks.TryParseHookMessage(line, out AgentHooks.HookMessage? message));
        Assert.Null(message!.Agent);
    }

    [Fact]
    public void Opencode_reports_through_the_plugin_so_it_gets_no_command_line_arguments()
    {
        Assert.Empty(AgentHooks.BuildArguments(AgentKind.OpenCode, @"C:\Notch\Notch.Hook.exe", @"C:\x\claude.json"));
    }

    [Fact]
    public void The_states_follow_working_waiting_working_done()
    {
        var tracker = new AgentStatusTracker(AgentKind.OpenCode);
        var seen = new List<AgentState>();
        tracker.Changed += () => seen.Add(tracker.State);

        tracker.OnHookEvent("UserPromptSubmit");
        tracker.OnHookEvent("Notification");
        tracker.OnHookEvent("PostToolUse");
        tracker.OnHookEvent("Stop");
        tracker.OnViewed();

        Assert.Equal([AgentState.Working, AgentState.NeedsInput, AgentState.Working, AgentState.Done, AgentState.Idle], seen);
    }

    [Fact]
    public void An_interrupted_turn_goes_back_to_idle_without_a_done_badge()
    {
        var tracker = new AgentStatusTracker(AgentKind.OpenCode);

        tracker.OnHookEvent("UserPromptSubmit");
        tracker.OnHookEvent("SessionEnd");

        Assert.Equal(AgentState.Idle, tracker.State);
    }
}

public class OpenCodePluginFileTests
{
    private const string Plugin = "// notch-opencode-plugin 1\nexport default {}\n";

    [Fact]
    public void The_plugin_goes_in_opencodes_plugins_folder()
    {
        Assert.Equal(@"C:\Users\me\.config\opencode\plugins\notch.js", OpenCodePlugin.PluginPath(null, @"C:\Users\me"));
        Assert.Equal(@"D:\oc\plugins\notch.js", OpenCodePlugin.PluginPath(@"D:\oc", @"C:\Users\me"));
        Assert.Equal(@"C:\Users\me\.config\opencode", OpenCodePlugin.ConfigDirectory("  ", @"C:\Users\me"));
    }

    [Fact]
    public void A_new_plugin_is_added()
    {
        HookEdit edit = OpenCodePlugin.Add(null, Plugin);

        Assert.True(edit.Changed);
        Assert.Equal(Plugin, edit.Text);
        Assert.Null(edit.Problem);
    }

    [Fact]
    public void The_same_plugin_is_not_written_again_but_an_older_one_is_replaced()
    {
        Assert.False(OpenCodePlugin.Add(Plugin.Replace("\n", "\r\n"), Plugin).Changed);

        HookEdit update = OpenCodePlugin.Add("// notch-opencode-plugin 0\nold", Plugin);
        Assert.True(update.Changed);
        Assert.Equal(Plugin, update.Text);
    }

    [Fact]
    public void A_plugin_that_is_not_notchs_is_never_overwritten()
    {
        const string Theirs = "export default { id: 'mine' }";

        HookEdit edit = OpenCodePlugin.Add(Theirs, Plugin);

        Assert.False(edit.Changed);
        Assert.Equal(Theirs, edit.Text);
        Assert.Contains("not Notch's", edit.Problem);
    }

    [Fact]
    public void Removing_deletes_only_notchs_own_file()
    {
        HookEdit ours = OpenCodePlugin.Remove(Plugin);
        Assert.True(ours.Changed);
        Assert.Equal("", ours.Text);

        Assert.False(OpenCodePlugin.Remove("export default { id: 'mine' }").Changed);
        Assert.False(OpenCodePlugin.Remove(null).Changed);
    }

    [Fact]
    public void The_plugin_that_ships_with_the_app_is_recognised_as_notchs_and_has_the_shape_opencode_2_requires()
    {
        string? path = null;
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null && path is null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Notch.App", "Assets", "opencode", OpenCodePlugin.FileName);
            path = File.Exists(candidate) ? candidate : null;
        }

        Assert.NotNull(path);
        string text = File.ReadAllText(path);

        Assert.True(OpenCodePlugin.IsNotchs(text));
        // opencode 2 refuses a plugin whose default export is not an object with an id and a setup function.
        Assert.Contains("export default {", text);
        Assert.Contains("id: \"notch\"", text);
        Assert.Contains("async setup(", text);
        foreach (string name in new[] { "session.execution.started", "session.execution.succeeded", "permission.asked", "permission.replied" })
        {
            Assert.Contains(name, text);
        }

        // It must talk only to Notch's pipes, never the network.
        Assert.DoesNotContain("fetch(", text);
        Assert.DoesNotContain("http://", text);
        Assert.DoesNotContain("https://", text);
        Assert.Contains("notch-agent-shared", text);
    }
}
