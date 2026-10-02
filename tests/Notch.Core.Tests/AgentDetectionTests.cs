using System.Text.Json.Nodes;
using Notch.Core.Activities;
using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class AgentDetectionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot P(int pid, int parent, string name, string? commandLine = null, string? path = null, int minute = 0) =>
        new(pid, parent, name, commandLine, path, T0.AddMinutes(minute));

    [Fact]
    public void A_native_agent_and_a_node_wrapped_one_are_both_found()
    {
        ProcessSnapshot[] processes =
        [
            P(1, 0, "explorer.exe"),
            P(10, 1, "WindowsTerminal.exe"),
            P(11, 10, "OpenConsole.exe"),
            P(12, 11, "pwsh.exe"),
            P(13, 12, "claude.exe", "claude"),
            P(20, 1, "Code.exe"),
            P(21, 20, "pwsh.exe"),
            P(22, 21, "node.exe", "node C:\\Users\\me\\AppData\\Roaming\\npm\\node_modules\\@openai\\codex\\bin\\codex.js"),
        ];

        IReadOnlyList<DetectedAgent> found = AgentDetector.Detect(processes, ownPid: 999);

        Assert.Equal(["claude", "codex"], found.Select(a => a.Definition.Id).OrderBy(x => x));
        DetectedAgent claude = found.Single(a => a.Definition.Id == "claude");
        Assert.Equal(13, claude.Pid);
        Assert.Equal("Windows Terminal", claude.HostName);
        Assert.Equal(10, claude.HostPid);
        Assert.Equal("VS Code", found.Single(a => a.Definition.Id == "codex").HostName);
    }

    [Fact]
    public void An_agent_with_helper_processes_counts_once()
    {
        ProcessSnapshot[] processes =
        [
            P(1, 0, "cmd.exe"),
            P(2, 1, "node.exe", "node C:\\x\\node_modules\\@anthropic-ai\\claude-code\\cli.js", minute: 1),
            P(3, 2, "node.exe", "node C:\\x\\node_modules\\@anthropic-ai\\claude-code\\cli.js --worker", minute: 2),
            P(4, 2, "claude.exe", "claude", minute: 2),
        ];

        DetectedAgent agent = Assert.Single(AgentDetector.Detect(processes, 999));
        Assert.Equal(2, agent.Pid);
    }

    [Fact]
    public void Agents_started_by_notchs_own_terminal_are_left_to_it()
    {
        ProcessSnapshot[] processes =
        [
            P(100, 1, "Notch.exe"),
            P(101, 100, "pwsh.exe"),
            P(102, 101, "claude.exe", "claude"),
            P(200, 1, "cmd.exe"),
            P(201, 200, "codex.exe", "codex"),
        ];

        DetectedAgent agent = Assert.Single(AgentDetector.Detect(processes, ownPid: 100));
        Assert.Equal("codex", agent.Definition.Id);
    }

    [Theory]
    [InlineData("claude.exe", "\"C:\\Users\\me\\AppData\\Local\\AnthropicClaude\\app-1.0\\claude.exe\"", @"C:\Users\me\AppData\Local\AnthropicClaude\app-1.0\claude.exe")]
    [InlineData("claude.exe", "claude.exe --type=renderer --lang=en", @"C:\Program Files\Claude\claude.exe")]
    [InlineData("claude.exe", "claude.exe", @"C:\Program Files\WindowsApps\Claude_1\claude.exe")]
    [InlineData("node.exe", "node server.js", null)]
    [InlineData("python.exe", "python manage.py runserver", null)]
    [InlineData("code.exe", "code .", null)]
    [InlineData("notepad.exe", "notepad claude.txt", null)]
    [InlineData("claude.exe", "claude.exe --output-format stream-json --verbose --input-format stream-json", null)]
    [InlineData("claude.exe", "claude -p fix the build", null)]
    [InlineData("claude.exe", "claude --print hello", null)]
    [InlineData("claude.exe", "claude.exe", @"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1.0\x\claude.exe")]
    [InlineData("codex.exe", "codex.exe -c features.x=true app-server --analytics", null)]
    [InlineData("codex.exe", "codex.exe exec-server --remote https://example", null)]
    [InlineData("codex.exe", "codex.exe", @"C:\Users\me\AppData\Local\OpenAI\Codex\bin\abc\codex.exe")]
    public void Lookalikes_are_not_agents(string name, string commandLine, string? path)
    {
        Assert.Empty(AgentDetector.Detect([P(5, 1, name, commandLine, path)], 999));
    }

    [Theory]
    [InlineData("python.exe", "python -m aider --model x", "aider")]
    [InlineData("node.exe", "node C:\\n\\@google\\gemini-cli\\dist\\index.js", "gemini")]
    [InlineData("gemini.exe", "gemini", "gemini")]
    [InlineData("aider.exe", "aider", "aider")]
    public void The_other_agents_are_recognised(string name, string commandLine, string id)
    {
        Assert.Equal(id, Assert.Single(AgentDetector.Detect([P(5, 1, name, commandLine)], 999)).Definition.Id);
    }

    [Fact]
    public void Hidden_agents_are_not_listed()
    {
        ProcessSnapshot[] processes = [P(5, 1, "claude.exe", "claude"), P(6, 1, "codex.exe", "codex")];
        Assert.Equal("codex", Assert.Single(AgentDetector.Detect(processes, 999, ["claude"])).Definition.Id);
    }

    [Fact]
    public void A_recycled_parent_id_is_not_followed()
    {
        // Pid 10 now belongs to a newer Notch; the agent's real parent has long gone.
        ProcessSnapshot[] processes =
        [
            P(10, 1, "Notch.exe", minute: 50),
            P(11, 10, "claude.exe", "claude", minute: 5),
        ];
        Assert.Single(AgentDetector.Detect(processes, ownPid: 10));
    }

    [Fact]
    public void Parent_loops_do_not_hang()
    {
        ProcessSnapshot[] processes = [P(1, 2, "a.exe"), P(2, 1, "claude.exe", "claude")];
        Assert.Single(AgentDetector.Detect(processes, 999));
    }

    [Fact]
    public void A_hook_program_is_traced_to_the_agent_that_started_it()
    {
        ProcessSnapshot[] processes =
        [
            P(1, 0, "cmd.exe"),
            P(2, 1, "claude.exe", "claude"),
            P(3, 2, "bash.exe"),
            P(4, 3, "Notch.Hook.exe"),
            P(9, 1, "Notch.Hook.exe"),
        ];
        IReadOnlyList<DetectedAgent> detected = AgentDetector.Detect(processes, 999);

        Assert.Equal(2, AgentDetector.AgentOf(4, processes, detected)?.Pid);
        Assert.Equal(2, AgentDetector.AgentOf(2, processes, detected)?.Pid);
        Assert.Null(AgentDetector.AgentOf(9, processes, detected));
        Assert.Null(AgentDetector.AgentOf(777, processes, detected));
    }

    [Fact]
    public void Window_candidates_put_the_known_host_first()
    {
        ProcessSnapshot[] processes =
        [
            P(10, 1, "WindowsTerminal.exe"),
            P(11, 10, "OpenConsole.exe"),
            P(12, 11, "pwsh.exe"),
            P(13, 12, "claude.exe", "claude"),
        ];
        IReadOnlyList<int> candidates = AgentDetector.WindowCandidates(13, processes);
        Assert.Equal(10, candidates[0]);
        Assert.Contains(12, candidates);
        Assert.Contains(13, candidates);
    }

    [Fact]
    public void Command_lines_name_the_agent_in_a_terminal_button()
    {
        Assert.Equal("claude", AgentCatalog.ForCommand(@"C:\bin\claude.cmd")?.Id);
        Assert.Equal("aider", AgentCatalog.ForCommand("aider")?.Id);
        Assert.Null(AgentCatalog.ForCommand("powershell"));
    }
}

public class AgentBoardTests
{
    private static AgentEntry Entry(string key, AgentState state = AgentState.Working, AgentSource source = AgentSource.Notch) =>
        new(key, "claude", "Claude", "g", state, "proj", source);

    [Fact]
    public void Entries_keep_their_place_when_updated()
    {
        var board = new AgentBoard();
        board.Set(Entry("a"));
        board.Set(Entry("b"));
        board.Set(Entry("a", AgentState.Done));

        Assert.Equal(["a", "b"], board.Snapshot().Select(e => e.Key));
        Assert.Equal(AgentState.Done, board.Find("a")!.State);
    }

    [Fact]
    public void Only_real_changes_are_announced()
    {
        var board = new AgentBoard();
        int changes = 0;
        board.Changed += () => changes++;

        board.Set(Entry("a"));
        board.Set(Entry("a"));
        board.Remove("nope");
        board.Remove("a");

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Keeping_only_some_removes_the_rest_of_that_source()
    {
        var board = new AgentBoard();
        board.Set(Entry("tab", source: AgentSource.Notch));
        board.Set(Entry("d1", source: AgentSource.Detected));
        board.Set(Entry("d2", source: AgentSource.Detected));

        board.KeepOnly(AgentSource.Detected, new HashSet<string> { "d2" });

        Assert.Equal(["tab", "d2"], board.Snapshot().Select(e => e.Key));
    }

    [Fact]
    public void A_detected_agent_that_reports_nothing_is_called_open()
    {
        Assert.Equal("Open", Entry("d", AgentState.Idle, AgentSource.Detected).StateText);
        Assert.Equal("Idle", Entry("t", AgentState.Idle, AgentSource.Notch).StateText);
        Assert.Equal("Needs input", Entry("t", AgentState.NeedsInput).StateText);
    }

    [Fact]
    public void The_pill_shows_each_entry_that_has_something_to_say_and_counts_the_others()
    {
        using var activities = new ActivityManager();
        var board = new AgentBoard();
        using var publisher = new AgentPublisher(activities, board);

        board.Set(Entry("a", AgentState.Working));
        board.Set(Entry("b", AgentState.NeedsInput, AgentSource.Detected));
        board.Set(Entry("c", AgentState.Idle, AgentSource.Detected));

        IReadOnlyList<Activity> shown = activities.Snapshot();
        Assert.Equal(2, shown.Count);
        Assert.All(shown, a => Assert.EndsWith("+1", a.Detail));

        board.Set(Entry("b", AgentState.Idle, AgentSource.Detected));
        Assert.Equal("Working", Assert.Single(activities.Snapshot()).Detail);

        board.Remove("a");
        Assert.Empty(activities.Snapshot());
    }
}

public class AgentHookMessageTests
{
    [Fact]
    public void A_message_from_an_agent_started_elsewhere_carries_the_hook_pid_and_folder()
    {
        string payload = """{"hook_event_name":"Stop","cwd":"C:\\work\\app","session_id":"abc"}""";
        string line = AgentHooks.FormatMessage("", payload, 4321);

        Assert.True(AgentHooks.TryParseHookMessage(line, out AgentHooks.HookMessage? message));
        Assert.Equal("", message!.Session);
        Assert.Equal("Stop", message.EventName);
        Assert.Equal(4321, message.HookPid);
        Assert.Equal("C:\\work\\app", message.Folder);
    }

    [Fact]
    public void Codex_notifications_are_read_too()
    {
        string line = AgentHooks.FormatMessage("s1", """{"type":"agent-turn-complete","cwd":"/x"}""");
        Assert.True(AgentHooks.TryParseHookMessage(line, out AgentHooks.HookMessage? message));
        Assert.Equal("s1", message!.Session);
        Assert.Equal("agent-turn-complete", message.EventName);
        Assert.Equal(0, message.HookPid);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("""{"session":"s","payload":"{}"}""")]
    [InlineData("""{"session":"s","payload":"nope"}""")]
    public void Bad_messages_are_refused(string line)
    {
        Assert.False(AgentHooks.TryParseHookMessage(line, out _));
    }
}

public class AgentHookEditorTests
{
    private const string Hook = @"C:\Users\me\AppData\Local\Programs\Notch\Notch.Hook.exe";

    [Fact]
    public void Claude_gets_the_six_hooks_and_keeps_everything_else()
    {
        const string existing = """
            {
              "model": "opus",
              "hooks": {
                "Stop": [ { "hooks": [ { "type": "command", "command": "my-script" } ] } ],
                "PreCompact": [ { "hooks": [ { "type": "command", "command": "other" } ] } ]
              }
            }
            """;

        HookEdit edit = AgentHookEditor.AddClaudeHooks(existing, Hook);

        Assert.True(edit.Changed);
        JsonNode root = JsonNode.Parse(edit.Text)!;
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal(2, root["hooks"]!["Stop"]!.AsArray().Count);
        Assert.Single(root["hooks"]!["PreCompact"]!.AsArray());
        foreach (string name in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd" })
        {
            Assert.Contains(root["hooks"]![name]!.AsArray(), g => ((string?)g!["hooks"]![0]!["command"])!.Contains("Notch.Hook"));
        }

        Assert.True(AgentHookEditor.ClaudeHasHooks(edit.Text));
    }

    [Fact]
    public void Adding_twice_changes_nothing_the_second_time()
    {
        HookEdit first = AgentHookEditor.AddClaudeHooks(null, Hook);
        HookEdit second = AgentHookEditor.AddClaudeHooks(first.Text, Hook);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void Removing_puts_the_file_back_as_far_as_content_goes()
    {
        const string original = """{ "model": "opus", "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "mine" } ] } ] } }""";

        HookEdit added = AgentHookEditor.AddClaudeHooks(original, Hook);
        HookEdit removed = AgentHookEditor.RemoveClaudeHooks(added.Text);

        Assert.True(removed.Changed);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(removed.Text)));
        Assert.False(AgentHookEditor.ClaudeHasHooks(removed.Text));
    }

    [Fact]
    public void A_file_that_is_all_ours_empties_out_without_leaving_an_empty_hooks_section()
    {
        HookEdit removed = AgentHookEditor.RemoveClaudeHooks(AgentHookEditor.AddClaudeHooks("{}", Hook).Text);
        Assert.Null(JsonNode.Parse(removed.Text)!["hooks"]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("""{ "hooks": 5 }""")]
    [InlineData("""{ "hooks": { "Stop": "x" } }""")]
    public void A_file_that_is_not_what_was_expected_is_never_changed(string text)
    {
        HookEdit edit = AgentHookEditor.AddClaudeHooks(text, Hook);
        Assert.False(edit.Changed);
        Assert.Equal(text, edit.Text);
        Assert.NotNull(edit.Problem);
    }

    [Fact]
    public void Codex_gets_a_notify_line_before_any_table()
    {
        const string existing = "model = \"o3\"\n\n[projects.x]\ntrust = true\n";
        HookEdit edit = AgentHookEditor.AddCodexHook(existing, Hook);

        Assert.True(edit.Changed);
        Assert.StartsWith("# Notch:", edit.Text);
        Assert.True(edit.Text.IndexOf("notify =", StringComparison.Ordinal) < edit.Text.IndexOf("[projects.x]", StringComparison.Ordinal));
        Assert.True(AgentHookEditor.CodexHasHook(edit.Text));
        Assert.EndsWith(existing, edit.Text);
    }

    [Fact]
    public void Codex_keeps_a_notify_command_of_the_users_own()
    {
        const string existing = "notify = [\"my-notifier\"]\n";
        HookEdit edit = AgentHookEditor.AddCodexHook(existing, Hook);

        Assert.False(edit.Changed);
        Assert.Equal(existing, edit.Text);
        Assert.NotNull(edit.Problem);
        Assert.False(AgentHookEditor.CodexHasHook(existing));
    }

    [Fact]
    public void A_notify_line_inside_a_table_is_not_the_top_level_one()
    {
        HookEdit edit = AgentHookEditor.AddCodexHook("[tui]\nnotify = true\n", Hook);
        Assert.True(edit.Changed);
    }

    [Fact]
    public void Codex_is_restored_exactly()
    {
        foreach (string original in new[] { "", "model = \"o3\"\n", "model = \"o3\"\r\n\r\n[x]\r\ny = 1\r\n" })
        {
            HookEdit added = AgentHookEditor.AddCodexHook(original, Hook);
            HookEdit removed = AgentHookEditor.RemoveCodexHook(added.Text);
            Assert.Equal(original.Replace("\r\n", "\n"), removed.Text.Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public void Removing_from_a_file_without_our_hook_does_nothing()
    {
        Assert.False(AgentHookEditor.RemoveCodexHook("notify = [\"mine\"]\n").Changed);
        Assert.False(AgentHookEditor.RemoveClaudeHooks("""{ "hooks": { "Stop": [ { "hooks": [ { "command": "mine" } ] } ] } }""").Changed);
    }

    [Fact]
    public void Paths_with_spaces_are_quoted_and_use_forward_slashes()
    {
        Assert.Equal("\"C:/Program Files/Notch/Notch.Hook.exe\"", AgentHookEditor.ShellCommand(@"C:\Program Files\Notch\Notch.Hook.exe"));
        Assert.Equal("C:/n/Notch.Hook.exe", AgentHookEditor.ShellCommand(@"C:\n\Notch.Hook.exe"));
    }
}
