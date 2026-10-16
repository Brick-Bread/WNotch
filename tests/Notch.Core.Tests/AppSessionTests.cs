using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class AppSessionTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private static string Line(string json) => json.Replace('\'', '"');

    [Fact]
    public void The_claude_app_and_its_sessions_are_found_among_the_programs()
    {
        const string app = "C:\\Program Files\\WindowsApps\\Claude_2.42304.0.0_x64__pzs8sxrjxfjjc\\app\\Claude.exe";
        const string worker = "C:\\Users\\me\\AppData\\Roaming\\Claude\\claude-code\\2.1.299\\f0\\claude.exe";
        ProcessSnapshot[] processes =
        [
            new(1, 0, "explorer.exe"),
            new(10, 1, "claude.exe", $"\"{app}\"", app),
            new(11, 10, "claude.exe", $"\"{app}\" --type=renderer", app),
            new(12, 10, "claude.exe", $"{worker} --output-format stream-json --verbose --input-format stream-json", worker),
            new(13, 10, "claude.exe", $"{worker} --output-format stream-json --verbose --input-format stream-json --resume=abc", worker),
        ];

        RunningApp found = Assert.Single(AppSessions.Running(processes));
        Assert.Equal(DesktopApp.Claude, found.App);
        Assert.Equal(10, found.Pid);
        Assert.Equal(2, found.Workers);

        // The app's own programs are not command line agents.
        Assert.Empty(AgentDetector.Detect(processes, 999));
    }

    [Fact]
    public void The_chatgpt_app_is_found_by_its_package_folder()
    {
        const string app = "C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0\\app\\ChatGPT.exe";
        ProcessSnapshot[] processes =
        [
            new(20, 1, "ChatGPT.exe", $"\"{app}\"", app),
            new(21, 20, "ChatGPT.exe", $"\"{app}\" --type=gpu-process", app),
            new(30, 1, "codex.exe", "codex", "C:\\Users\\me\\AppData\\Roaming\\npm\\codex.exe"),
        ];

        RunningApp found = Assert.Single(AppSessions.Running(processes));
        Assert.Equal(DesktopApp.ChatGpt, found.App);
        Assert.Equal(20, found.Pid);
    }

    [Fact]
    public void A_claude_session_file_is_read()
    {
        ClaudeAppSession? session = AppSessions.ReadClaudeSession(Line(
            "{'sessionId':'local_1','cliSessionId':'abc','cwd':'C:\\\\code\\\\notch','title':'Bug fix','isArchived':false," +
            "'lastActivityAt':1792142078801,'lastFocusedAt':1792142144516,'interruptedAwaitingPerson':null}"));

        Assert.NotNull(session);
        Assert.Equal("abc", session.TranscriptId);
        Assert.Equal("Bug fix", session.Title);
        Assert.Equal("C:\\code\\notch", session.Folder);
        Assert.False(session.Archived);
        Assert.False(session.AwaitingPerson);
        Assert.True(session.LastFocused > session.LastActivity);
    }

    [Fact]
    public void A_claude_turn_that_ended_is_told_from_one_still_running()
    {
        string[] ended =
        [
            Line("{'type':'user','message':{'role':'user','content':'fix it'},'timestamp':'2026-10-16T09:00:00Z'}"),
            Line("{'type':'assistant','message':{'stop_reason':'end_turn','content':[{'type':'text','text':'Fixed.'}]},'timestamp':'2026-10-16T09:01:00Z'}"),
            Line("{'type':'system','subtype':'stop_hook_summary','timestamp':'2026-10-16T09:01:02Z'}"),
        ];
        TurnReading reading = AppSessions.ReadClaudeTurn(ended);
        Assert.Equal(TurnState.Ended, reading.State);
        Assert.Equal(T0.AddMinutes(1).AddSeconds(2), reading.At);

        string[] tool =
        [
            Line("{'type':'assistant','message':{'stop_reason':'tool_use','content':[{'type':'tool_use','name':'Bash'}]},'timestamp':'2026-10-16T09:00:00Z'}"),
            Line("{'type':'attachment','timestamp':'2026-10-16T09:00:01Z'}"),
        ];
        Assert.Equal(TurnState.Working, AppSessions.ReadClaudeTurn(tool).State);

        string[] result = [.. tool, Line("{'type':'user','message':{'content':[{'type':'tool_result','content':'ok'}]},'timestamp':'2026-10-16T09:00:02Z'}")];
        Assert.Equal(TurnState.Working, AppSessions.ReadClaudeTurn(result).State);
    }

    [Fact]
    public void A_claude_question_needs_input_and_an_interruption_stops_the_turn()
    {
        string[] asking =
        [
            Line("{'type':'assistant','message':{'content':[{'type':'tool_use','name':'AskUserQuestion'}]},'timestamp':'2026-10-16T09:00:00Z'}"),
        ];
        Assert.Equal(TurnState.NeedsInput, AppSessions.ReadClaudeTurn(asking).State);

        string[] stopped =
        [
            Line("{'type':'assistant','message':{'content':[{'type':'tool_use','name':'Bash'}]},'timestamp':'2026-10-16T09:00:00Z'}"),
            Line("{'type':'user','message':{'content':[{'type':'text','text':'[Request interrupted by user for tool use]'}]},'timestamp':'2026-10-16T09:00:05Z'}"),
        ];
        Assert.Equal(TurnState.Stopped, AppSessions.ReadClaudeTurn(stopped).State);
    }

    [Fact]
    public void A_subagents_lines_and_a_half_read_line_are_passed_over()
    {
        string[] lines =
        [
            "ext\":\"cut off in the middle\"}]}}",
            Line("{'type':'assistant','message':{'stop_reason':'end_turn','content':[{'type':'text','text':'Done.'}]},'timestamp':'2026-10-16T09:00:00Z'}"),
            Line("{'type':'user','isSidechain':true,'message':{'content':'subagent prompt'},'timestamp':'2026-10-16T09:00:01Z'}"),
        ];

        Assert.Equal(TurnState.Ended, AppSessions.ReadClaudeTurn(lines).State);
    }

    [Fact]
    public void Codex_threads_of_the_app_are_told_from_the_command_line_and_from_helpers()
    {
        CodexThreadMeta? app = AppSessions.ReadCodexMeta(Line(
            "{'type':'session_meta','payload':{'id':'t1','cwd':'C:\\\\code','originator':'Codex Desktop','source':'vscode','parent_thread_id':null,'thread_source':'user'}}"));
        Assert.NotNull(app);
        Assert.True(app.FromApp);
        Assert.False(app.Subagent);

        CodexThreadMeta? cli = AppSessions.ReadCodexMeta(Line(
            "{'type':'session_meta','payload':{'id':'t2','originator':'codex-tui','source':'cli','thread_source':'user'}}"));
        Assert.False(cli!.FromApp);

        CodexThreadMeta? guardian = AppSessions.ReadCodexMeta(Line(
            "{'type':'session_meta','payload':{'id':'t3','originator':'Codex Desktop','source':{'subagent':{'other':'guardian'}},'parent_thread_id':'t1','thread_source':'guardian_review'}}"));
        Assert.True(guardian!.Subagent);
    }

    [Fact]
    public void A_codex_turn_is_read_from_its_last_task_event()
    {
        string started = Line("{'type':'event_msg','payload':{'type':'task_started'},'timestamp':'2026-10-16T09:00:00Z'}");
        string item = Line("{'type':'response_item','payload':{'type':'message'},'timestamp':'2026-10-16T09:00:30Z'}");
        string complete = Line("{'type':'event_msg','payload':{'type':'task_complete'},'timestamp':'2026-10-16T09:01:00Z'}");
        string aborted = Line("{'type':'event_msg','payload':{'type':'turn_aborted'},'timestamp':'2026-10-16T09:01:00Z'}");

        TurnReading working = AppSessions.ReadCodexTurn([started, item]);
        Assert.Equal(TurnState.Working, working.State);
        Assert.Equal(T0.AddSeconds(30), working.At);
        Assert.Equal(TurnState.Ended, AppSessions.ReadCodexTurn([started, item, complete]).State);
        Assert.Equal(TurnState.Stopped, AppSessions.ReadCodexTurn([started, aborted]).State);
    }

    [Fact]
    public void A_thread_keeps_the_latest_name_it_was_given()
    {
        Dictionary<string, string> titles = AppSessions.ReadCodexTitles(
        [
            Line("{'id':'t1','thread_name':'First name'}"),
            Line("{'id':'t1','thread_name':'Renamed'}"),
            "",
        ]);

        Assert.Equal("Renamed", titles["t1"]);
    }

    [Fact]
    public void Done_lasts_until_the_session_is_looked_at_and_a_quiet_turn_is_not_working()
    {
        TimeSpan stale = TimeSpan.FromMinutes(10);
        var ended = new TurnReading(TurnState.Ended, T0);

        Assert.Equal(AgentState.Done, AppSessions.StateFor(ended, viewed: T0.AddMinutes(-1), T0.AddMinutes(5), stale));
        Assert.Equal(AgentState.Idle, AppSessions.StateFor(ended, viewed: T0.AddMinutes(1), T0.AddMinutes(5), stale));

        var working = new TurnReading(TurnState.Working, T0);
        Assert.Equal(AgentState.Working, AppSessions.StateFor(working, null, T0.AddMinutes(1), stale));
        Assert.Equal(AgentState.Idle, AppSessions.StateFor(working, null, T0.AddMinutes(30), stale));

        Assert.Equal(AgentState.NeedsInput, AppSessions.StateFor(ended, T0.AddMinutes(1), T0.AddMinutes(5), stale, awaitingPerson: true));
    }

    [Fact]
    public void An_app_session_with_nothing_to_report_reads_as_open()
    {
        var entry = new AgentEntry("k", "claude", "Claude", "", AgentState.Idle, "Bug fix", AgentSource.App, 10, "Claude app");
        Assert.Equal("Open", entry.StateText);
    }
}
