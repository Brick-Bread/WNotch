using System.Text.Json.Nodes;
using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class ApprovalTests
{
    private static AgentHooks.HookMessage Message(string payload, bool reply = true)
    {
        Assert.True(AgentHooks.TryParseHookMessage(AgentHooks.FormatMessage("", payload, 77, reply), out AgentHooks.HookMessage? message));
        return message!;
    }

    private static string Claude(string tool, string input, string suggestions = "[]") => $$"""
        {"session_id":"s1","cwd":"C:\\code\\app","hook_event_name":"PermissionRequest","tool_name":"{{tool}}","tool_input":{{input}},"permission_suggestions":{{suggestions}}}
        """;

    private static JsonNode Decision(string reply) => JsonNode.Parse(reply)!["hookSpecificOutput"]!["decision"]!;

    [Fact]
    public void A_hook_that_waits_says_so_and_carries_the_agents_session()
    {
        AgentHooks.HookMessage message = Message(Claude("Bash", """{"command":"npm test"}"""));

        Assert.True(message.WantsReply);
        Assert.Equal("PermissionRequest", message.EventName);
        Assert.Equal("s1", message.AgentSession);
        Assert.True(AgentHooks.WantsReply(Claude("Bash", "{}")));
        Assert.False(AgentHooks.WantsReply("""{"hook_event_name":"Stop"}"""));
        Assert.False(Message("""{"hook_event_name":"Stop","session_id":"s1"}""", reply: false).WantsReply);
    }

    [Fact]
    public void A_command_reads_as_the_command_and_allow_and_deny_answer_it()
    {
        AgentHooks.HookMessage message = Message(Claude("Bash", """{"command":"rm -rf node_modules","description":"Remove node_modules"}"""));
        ApprovalRequest request = AgentApprovals.Read(message)!;

        Assert.Equal(ApprovalKind.Tool, request.Kind);
        Assert.Equal("Run a command", request.Title);
        Assert.Equal("rm -rf node_modules", request.Subject);
        Assert.Equal("Remove node_modules", request.Detail);
        Assert.Equal(@"C:\code\app", request.Folder);
        Assert.False(request.CanAlwaysAllow);

        JsonNode allow = Decision(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.Allow)));
        Assert.Equal("allow", (string?)allow["behavior"]);
        Assert.Null(allow["updatedInput"]);
        Assert.Equal("PermissionRequest", (string?)JsonNode.Parse(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.Allow)))!["hookSpecificOutput"]!["hookEventName"]);

        JsonNode deny = Decision(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.Deny)));
        Assert.Equal("deny", (string?)deny["behavior"]);
        Assert.Equal(AgentApprovals.DeniedMessage, (string?)deny["message"]);
    }

    [Fact]
    public void Always_allow_hands_back_what_claude_suggested()
    {
        const string suggestions = """[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"localSettings"}]""";
        AgentHooks.HookMessage message = Message(Claude("Bash", """{"command":"npm test"}""", suggestions));

        Assert.True(AgentApprovals.Read(message)!.CanAlwaysAllow);
        JsonNode decision = Decision(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.AllowAlways)));
        Assert.Equal("allow", (string?)decision["behavior"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(suggestions), decision["updatedPermissions"]));
    }

    [Fact]
    public void An_edit_shows_the_file_and_the_change()
    {
        ApprovalRequest request = AgentApprovals.Read(Message(Claude("Edit", """{"file_path":"C:\\code\\app\\App.cs","old_string":"a\nb","new_string":"c"}""")))!;

        Assert.Equal("Edit App.cs", request.Title);
        Assert.Equal(@"C:\code\app\App.cs", request.Subject);
        Assert.Equal("- a\n- b\n+ c", request.Detail);
    }

    [Fact]
    public void Long_details_are_cut_short()
    {
        string content = string.Join("\\n", Enumerable.Range(1, 200).Select(i => $"line {i}"));
        ApprovalRequest request = AgentApprovals.Read(Message(Claude("Write", $$"""{"file_path":"/x/new.txt","content":"{{content}}"}""")))!;

        Assert.Equal("Write new.txt", request.Title);
        Assert.EndsWith("…", request.Detail);
        Assert.True(request.Detail!.Split('\n').Length <= 61);
    }

    [Fact]
    public void An_mcp_tool_names_its_server()
    {
        ApprovalRequest request = AgentApprovals.Read(Message(Claude("mcp__github__create_issue", """{"title":"Bug"}""")))!;

        Assert.Equal("Use create_issue from github", request.Title);
        Assert.Contains("\"title\": \"Bug\"", request.Detail);
    }

    [Fact]
    public void A_question_is_answered_with_the_picked_labels()
    {
        const string input = """{"questions":[{"question":"Which framework?","header":"Framework","options":[{"label":"React","description":"Components"},{"label":"Vue"}],"multiSelect":false},{"question":"Fuel?","header":"Fuel","options":[{"label":"Coffee"},{"label":"Tea"}],"multiSelect":true}]}""";
        AgentHooks.HookMessage message = Message(Claude("AskUserQuestion", input));
        ApprovalRequest request = AgentApprovals.Read(message)!;

        Assert.Equal(ApprovalKind.Question, request.Kind);
        Assert.Equal(2, request.Questions.Count);
        Assert.Equal("Framework", request.Questions[0].Header);
        Assert.Equal(["React", "Vue"], request.Questions[0].Options.Select(o => o.Label));
        Assert.Equal("Components", request.Questions[0].Options[0].Description);
        Assert.True(request.Questions[1].MultiSelect);

        var answers = new Dictionary<string, IReadOnlyList<string>> { ["Which framework?"] = ["React"], ["Fuel?"] = ["Coffee", "Tea"] };
        JsonNode decision = Decision(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.Allow, answers)));
        Assert.Equal("allow", (string?)decision["behavior"]);
        JsonNode updated = decision["updatedInput"]!;
        Assert.Equal(2, updated["questions"]!.AsArray().Count);
        Assert.Equal("React", (string?)updated["answers"]!["Which framework?"]);
        Assert.Equal("Coffee, Tea", (string?)updated["answers"]!["Fuel?"]);
    }

    [Fact]
    public void A_plan_is_shown_and_tool_input_sent_as_text_is_read_too()
    {
        ApprovalRequest request = AgentApprovals.Read(Message(Claude("ExitPlanMode", "\"{\\\"plan\\\":\\\"1. Do it\\\"}\"")))!;

        Assert.Equal(ApprovalKind.Plan, request.Kind);
        Assert.Equal("1. Do it", request.Detail);
    }

    [Fact]
    public void Opencode_prompts_read_and_answer_in_opencodes_words()
    {
        const string payload = """{"hook_event_name":"PermissionRequest","agent":"opencode","cwd":"C:\\code\\app","sessionID":"ses_1","requestID":"per_1","action":"bash","resources":["git push"],"save":["git push*"]}""";
        AgentHooks.HookMessage message = Message(payload);
        ApprovalRequest request = AgentApprovals.Read(message)!;

        Assert.Equal("opencode", request.AgentId);
        Assert.Equal("ses_1", request.AgentSession);
        Assert.Equal("Run a command", request.Title);
        Assert.Equal("git push", request.Subject);
        Assert.True(request.CanAlwaysAllow);

        Assert.Equal("once", (string?)JsonNode.Parse(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.Allow)))!["decision"]);
        Assert.Equal("always", (string?)JsonNode.Parse(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.AllowAlways)))!["decision"]);
        Assert.Equal("reject", (string?)JsonNode.Parse(AgentApprovals.Reply(message, new ApprovalAnswer(ApprovalDecision.Deny)))!["decision"]);
    }

    [Fact]
    public void Only_a_waiting_permission_request_is_a_prompt()
    {
        Assert.Null(AgentApprovals.Read(Message(Claude("Bash", "{}"), reply: false)));
        Assert.Null(AgentApprovals.Read(Message("""{"hook_event_name":"Stop","session_id":"s1"}""")));
    }

    [Fact]
    public void A_prompt_answered_in_the_terminal_is_settled_by_what_follows()
    {
        AgentHooks.HookMessage prompt = Message(Claude("Bash", """{"command":"npm test"}"""));

        Assert.True(AgentApprovals.IsSettledBy(prompt, Message("""{"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Bash","tool_input":{"command":"npm test"}}""", false)));
        Assert.True(AgentApprovals.IsSettledBy(prompt, Message("""{"session_id":"s1","hook_event_name":"Stop"}""", false)));
        Assert.True(AgentApprovals.IsSettledBy(prompt, Message("""{"session_id":"s1","hook_event_name":"UserPromptSubmit"}""", false)));

        // Another tool of the same turn, another session, or the prompt itself settle nothing.
        Assert.False(AgentApprovals.IsSettledBy(prompt, Message("""{"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Read","tool_input":{"file_path":"a"}}""", false)));
        Assert.False(AgentApprovals.IsSettledBy(prompt, Message("""{"session_id":"s2","hook_event_name":"Stop"}""", false)));
        Assert.False(AgentApprovals.IsSettledBy(prompt, Message("""{"session_id":"s1","hook_event_name":"Notification"}""", false)));
        Assert.False(AgentApprovals.IsSettledBy(prompt, prompt));
    }

    [Fact]
    public void An_opencode_prompt_is_settled_when_opencode_says_it_was_answered()
    {
        AgentHooks.HookMessage prompt = Message("""{"hook_event_name":"PermissionRequest","agent":"opencode","sessionID":"ses_1","requestID":"per_1","action":"edit"}""");

        Assert.True(AgentApprovals.IsSettledBy(prompt, Message("""{"hook_event_name":"PermissionReplied","agent":"opencode","sessionID":"ses_1","requestID":"per_1"}""", false)));
        Assert.False(AgentApprovals.IsSettledBy(prompt, Message("""{"hook_event_name":"PermissionReplied","agent":"opencode","sessionID":"ses_1","requestID":"per_2"}""", false)));
    }

    [Fact]
    public void Asking_puts_the_agent_in_needs_input_and_an_opencode_answer_back_to_work()
    {
        var status = new AgentStatusTracker(AgentKind.OpenCode);
        status.OnHookEvent("PermissionRequest");
        Assert.Equal(AgentState.NeedsInput, status.State);
        status.OnHookEvent("PermissionReplied");
        Assert.Equal(AgentState.Working, status.State);
    }

    [Fact]
    public void A_settings_file_from_an_older_notch_gets_only_the_hook_it_lacks()
    {
        const string hook = @"C:\Notch\Notch.Hook.exe";
        JsonObject old = JsonNode.Parse(AgentHookEditor.AddClaudeHooks(null, hook).Text)!.AsObject();
        old["hooks"]!.AsObject().Remove("PermissionRequest");

        HookEdit edit = AgentHookEditor.AddClaudeHooks(old.ToJsonString(), hook);

        Assert.True(edit.Changed);
        JsonNode hooks = JsonNode.Parse(edit.Text)!["hooks"]!;
        Assert.Single(hooks["PermissionRequest"]!.AsArray());
        Assert.Single(hooks["Stop"]!.AsArray());
    }
}
