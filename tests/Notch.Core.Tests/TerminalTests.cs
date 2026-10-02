using System.Text.Json.Nodes;
using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Settings;
using Notch.Core.Terminal;

namespace Notch.Core.Tests;

public class TerminalTests
{
    private const string Path = @"C:\Windows;C:\Tools;C:\Users\me\AppData\Roaming\npm";
    private const string PathExt = ".COM;.EXE;.BAT;.CMD";

    private static Func<string, bool> Files(params string[] existing) =>
        candidate => existing.Contains(candidate, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Finds_an_exe_on_the_path() =>
        Assert.Equal(@"C:\Tools\codex.exe", CommandResolver.FindOnPath("codex", Path, PathExt, Files(@"C:\Tools\codex.exe")), ignoreCase: true);

    [Fact]
    public void Prefers_the_cmd_shim_over_an_extensionless_script()
    {
        Func<string, bool> files = Files(@"C:\Users\me\AppData\Roaming\npm\codex", @"C:\Users\me\AppData\Roaming\npm\codex.cmd");

        Assert.Equal(@"C:\Users\me\AppData\Roaming\npm\codex.cmd", CommandResolver.FindOnPath("codex", Path, PathExt, files), ignoreCase: true);
    }

    [Fact]
    public void Returns_null_when_not_installed() =>
        Assert.Null(CommandResolver.FindOnPath("claude", Path, PathExt, Files()));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void Quotes_arguments_like_the_c_runtime(string argument, string expected) =>
        Assert.Equal(expected, CommandResolver.QuoteArgument(argument));

    [Fact]
    public void Batch_files_run_through_cmd() =>
        Assert.Equal(
            "cmd.exe /d /s /c \"C:\\npm\\codex.cmd -c \"a b\"\"",
            CommandResolver.BuildCommandLine(@"C:\npm\codex.cmd", ["-c", "a b"]));

    [Fact]
    public void Executables_run_directly() =>
        Assert.Equal(
            "\"C:\\Program Files\\claude.exe\" --settings C:\\s.json",
            CommandResolver.BuildCommandLine(@"C:\Program Files\claude.exe", ["--settings", @"C:\s.json"]));

    [Fact]
    public void Claude_reports_a_full_turn()
    {
        var tracker = new AgentStatusTracker(AgentKind.Claude);
        var states = new List<AgentState>();
        tracker.Changed += () => states.Add(tracker.State);

        tracker.OnHookEvent("UserPromptSubmit");
        tracker.OnHookEvent("PreToolUse");
        tracker.OnHookEvent("Notification");
        tracker.OnUserInput(submitted: false);
        tracker.OnHookEvent("PostToolUse");
        tracker.OnHookEvent("Stop");
        tracker.OnViewed();

        Assert.Equal(
            [AgentState.Working, AgentState.NeedsInput, AgentState.Working, AgentState.Done, AgentState.Idle],
            states);
    }

    [Fact]
    public void Codex_work_is_inferred_from_the_terminal()
    {
        var tracker = new AgentStatusTracker(AgentKind.Codex);

        tracker.OnUserInput(submitted: false);
        Assert.Equal(AgentState.Idle, tracker.State);

        tracker.OnUserInput(submitted: true);
        Assert.Equal(AgentState.Working, tracker.State);

        tracker.OnHookEvent("agent-turn-complete");
        Assert.Equal(AgentState.Done, tracker.State);
    }

    [Fact]
    public void Codex_false_start_clears_when_the_terminal_goes_quiet()
    {
        var tracker = new AgentStatusTracker(AgentKind.Codex);

        tracker.OnUserInput(submitted: true);
        tracker.OnOutputQuiet();

        Assert.Equal(AgentState.Idle, tracker.State);
    }

    [Fact]
    public void Typing_in_claude_does_not_count_as_work()
    {
        var tracker = new AgentStatusTracker(AgentKind.Claude);

        tracker.OnUserInput(submitted: true);

        Assert.Equal(AgentState.Idle, tracker.State);
    }

    [Fact]
    public void Shells_are_never_tracked()
    {
        var tracker = new AgentStatusTracker(AgentKind.None);

        tracker.OnUserInput(submitted: true);

        Assert.Equal(AgentState.Idle, tracker.State);
    }

    [Fact]
    public void Agent_states_map_to_pill_tiers()
    {
        Assert.Null(AgentActivities.For("1", "Claude", "g", AgentState.Idle));
        Assert.Equal(ActivityTier.Ongoing, AgentActivities.For("1", "Claude", "g", AgentState.Working)!.Tier);
        Assert.Equal(ActivityTier.Attention, AgentActivities.For("1", "Claude", "g", AgentState.NeedsInput)!.Tier);
        Assert.Equal("agent.1", AgentActivities.For("1", "Claude", "g", AgentState.Done)!.Id);
    }

    [Fact]
    public void Agent_activity_names_its_folder_and_counts_the_other_sessions()
    {
        Activity alone = AgentActivities.For("1", "Claude", "g", AgentState.Working, "notch")!;
        Assert.Equal(("Claude · notch", "Working"), (alone.Title, alone.Detail));

        Activity oneOfThree = AgentActivities.For("1", "Codex", "g", AgentState.NeedsInput, "website", others: 2)!;
        Assert.Equal(("Codex · website", "Needs input · +2"), (oneOfThree.Title, oneOfThree.Detail));

        Assert.Equal("Claude", AgentActivities.For("1", "Claude", "g", AgentState.Done, folderName: "")!.Title);
    }

    [Fact]
    public void Claude_settings_hook_every_tracked_event()
    {
        JsonNode settings = JsonNode.Parse(AgentHooks.BuildClaudeSettings(@"C:\Apps\Notch\Notch.Hook.exe"))!;

        foreach (string name in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd" })
        {
            JsonNode hook = settings["hooks"]![name]![0]!["hooks"]![0]!;
            Assert.Equal("command", (string?)hook["type"]);
            Assert.Equal("C:/Apps/Notch/Notch.Hook.exe", (string?)hook["command"]);
        }
    }

    [Fact]
    public void Hook_paths_with_spaces_are_quoted()
    {
        JsonNode settings = JsonNode.Parse(AgentHooks.BuildClaudeSettings(@"C:\Program Files\Notch\Notch.Hook.exe"))!;

        Assert.Equal("\"C:/Program Files/Notch/Notch.Hook.exe\"", (string?)settings["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void Agent_arguments_point_at_the_hook()
    {
        Assert.Equal(["--settings", @"C:\s.json"], AgentHooks.BuildArguments(AgentKind.Claude, @"C:\hook.exe", @"C:\s.json"));
        Assert.Equal(["-c", @"notify=['C:\hook.exe']"], AgentHooks.BuildArguments(AgentKind.Codex, @"C:\hook.exe", @"C:\s.json"));
        Assert.Empty(AgentHooks.BuildArguments(AgentKind.None, @"C:\hook.exe", @"C:\s.json"));
    }

    [Theory]
    [InlineData("{\"hook_event_name\":\"Stop\",\"session_id\":\"abc\"}", "Stop")]
    [InlineData("{\"type\":\"agent-turn-complete\",\"turn-id\":\"1\"}", "agent-turn-complete")]
    public void Hook_messages_round_trip(string payload, string expectedEvent)
    {
        string line = AgentHooks.FormatMessage("session-7", payload);

        Assert.True(AgentHooks.TryParseMessage(line, out string session, out string eventName));
        Assert.Equal("session-7", session);
        Assert.Equal(expectedEvent, eventName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"session\":\"s\",\"payload\":\"not json\"}")]
    [InlineData("{\"session\":\"s\",\"payload\":\"{}\"}")]
    [InlineData("{\"payload\":\"{\\\"type\\\":\\\"x\\\"}\"}")]
    public void Malformed_hook_messages_are_rejected(string line) =>
        Assert.False(AgentHooks.TryParseMessage(line, out _, out _));

    [Fact]
    public void Presets_read_environment_arguments_and_agent()
    {
        Assert.True(TerminalPresets.TryParse(
            @"Work Claude = CLAUDE_CONFIG_DIR=""C:\my work\.claude"" claude --model opus", [], out TerminalProfile profile));

        Assert.Equal(("Work Claude", "work-claude", "claude", AgentKind.Claude), (profile.DisplayName, profile.Id, profile.Command, profile.Agent));
        Assert.Equal(["--model", "opus"], profile.Arguments);
        Assert.Equal(@"C:\my work\.claude", profile.Environment!["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(AgentKind.Codex, TerminalPresets.FromLines(["Fork = C:\\bin\\my-codex.exe"])[0].Agent);
    }

    [Fact]
    public void Presets_round_trip_and_reject_bad_lines()
    {
        const string line = @"Work Claude = ""A=b c"" claude ""two words""";
        Assert.True(TerminalPresets.TryParse(line, [], out TerminalProfile profile));
        Assert.Equal(line, TerminalPresets.ToLine(profile));

        Assert.False(TerminalPresets.TryParse("no equals sign", [], out _));
        Assert.False(TerminalPresets.TryParse("Empty =", [], out _));
        Assert.False(TerminalPresets.TryParse("Open = claude \"quote", [], out _));
    }

    [Fact]
    public void Presets_keep_ids_apart_and_fall_back_to_the_defaults()
    {
        Assert.Equal(["a", "a-2"], TerminalPresets.FromLines(["A = x", "A = y"]).Select(p => p.Id));
        Assert.Equal(["claude", "codex", "opencode", "shell"], TerminalPresets.FromLines(["garbage"]).Select(p => p.Id));
        Assert.Equal(TerminalPresets.MaxPresets, TerminalPresets.FromLines(Enumerable.Range(0, 20).Select(i => $"P{i} = x")).Count);
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_corrupt_file()
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}", "settings.json");
        var store = new SettingsStore(file);
        try
        {
            Assert.Empty(store.Load().RecentFolders);

            var settings = new AppSettings();
            settings.RememberFolder(@"C:\a");
            settings.RememberFolder(@"C:\b");
            settings.RememberFolder(@"c:\A");
            store.Save(settings);

            Assert.Equal([@"c:\A", @"C:\b"], store.Load().RecentFolders);

            File.WriteAllText(file, "{ broken");
            Assert.Empty(store.Load().RecentFolders);
        }
        finally
        {
            Directory.Delete(System.IO.Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
