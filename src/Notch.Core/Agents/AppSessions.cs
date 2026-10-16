using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Core.Agents;

/// <summary>The desktop apps whose own sessions Notch can read.</summary>
public enum DesktopApp
{
    /// <summary>The Claude app, whose Code tab runs Claude Code sessions.</summary>
    Claude,

    /// <summary>The ChatGPT app (OpenAI's Codex package), whose coding threads Codex runs.</summary>
    ChatGpt,
}

/// <summary>Where a session's last turn stands, as its transcript tells it.</summary>
public enum TurnState
{
    /// <summary>Nothing in the transcript says.</summary>
    Unknown,
    Working,

    /// <summary>The agent asked the user something and waits for the answer.</summary>
    NeedsInput,

    /// <summary>The turn finished on its own.</summary>
    Ended,

    /// <summary>The user stopped the turn.</summary>
    Stopped,
}

/// <summary>What a transcript's last lines say: the turn's state and when the transcript last said anything.</summary>
public sealed record TurnReading(TurnState State, DateTime? At);

/// <summary>What the Claude app keeps about one Code tab session, in its own file.</summary>
/// <param name="TranscriptId">The Claude Code session id, which names the transcript.</param>
/// <param name="AwaitingPerson">The app holds the turn until the user answers it.</param>
public sealed record ClaudeAppSession(
    string Id,
    string TranscriptId,
    string? Title,
    string? Folder,
    bool Archived,
    DateTime? LastActivity,
    DateTime? LastFocused,
    bool AwaitingPerson);

/// <summary>The first line of a Codex thread's transcript, which says who started it and where.</summary>
/// <param name="FromApp">Started by the desktop app, not by the Codex command line.</param>
/// <param name="Subagent">A helper thread (a guardian review) under another thread, not one the user sees.</param>
public sealed record CodexThreadMeta(string Id, string? Folder, bool FromApp, bool Subagent);

/// <summary>A running desktop app as the process scan sees it.</summary>
/// <param name="Pid">The app's main process, which owns its windows.</param>
/// <param name="Workers">How many Claude Code programs the app runs, one for each session it has open; 0 for ChatGPT.</param>
public sealed record RunningApp(DesktopApp App, int Pid, int Workers);

/// <summary>
/// Reads the sessions the Claude and ChatGPT desktop apps keep on disk. Pure logic: the files are
/// read in the platform layer, and only their text comes here.
/// </summary>
public static class AppSessions
{
    /// <summary>Tools that stop a Claude turn until the user answers them.</summary>
    private static readonly HashSet<string> AskingTools = new(StringComparer.Ordinal) { "AskUserQuestion", "ExitPlanMode" };

    /// <summary>Program names, lowercase without .exe, whose path must be read to tell which app they are.</summary>
    private static readonly HashSet<string> AppPrograms = new(StringComparer.Ordinal) { "claude", "chatgpt", "codex" };

    public static string DisplayName(DesktopApp app) => app == DesktopApp.Claude ? "Claude" : "ChatGPT";

    /// <summary>The agent the app's sessions are, for its id and glyph.</summary>
    public static AgentDefinition Agent(DesktopApp app) => AgentCatalog.ForKind(app == DesktopApp.Claude ? AgentKind.Claude : AgentKind.Codex)!;

    /// <summary>Whether the process scan should read this program's path, since it may be one of the apps.</summary>
    public static bool IsAppProgram(string processName) => AppPrograms.Contains(AgentCatalog.NameWithoutExtension(processName));

    /// <summary>The desktop apps running right now. Helper processes of Chromium and Electron (--type=) are not the app.</summary>
    public static IReadOnlyList<RunningApp> Running(IReadOnlyList<ProcessSnapshot> processes)
    {
        var result = new List<RunningApp>();
        int claudeWorkers = processes.Count(p => IsClaudeWorker(p));

        ProcessSnapshot? claude = MainProcess(processes, IsClaudeApp);
        if (claude is not null)
        {
            result.Add(new RunningApp(DesktopApp.Claude, claude.Pid, claudeWorkers));
        }

        ProcessSnapshot? chatGpt = MainProcess(processes, IsChatGptApp);
        if (chatGpt is not null)
        {
            result.Add(new RunningApp(DesktopApp.ChatGpt, chatGpt.Pid, 0));
        }

        return result;
    }

    /// <summary>The app process whose parent is not the same app: the one the others were started from.</summary>
    private static ProcessSnapshot? MainProcess(IReadOnlyList<ProcessSnapshot> processes, Func<ProcessSnapshot, bool> isApp)
    {
        ProcessSnapshot[] app = [.. processes.Where(isApp)];
        var pids = app.Select(p => p.Pid).ToHashSet();
        return app.FirstOrDefault(p => !pids.Contains(p.ParentPid) && !IsHelper(p)) ?? app.FirstOrDefault(p => !IsHelper(p));
    }

    private static bool IsHelper(ProcessSnapshot process) =>
        (process.CommandLine ?? "").Contains(" --type=", StringComparison.OrdinalIgnoreCase);

    internal static bool IsClaudeApp(ProcessSnapshot process) =>
        AgentCatalog.NameWithoutExtension(process.Name) == "claude"
        && process.ExecutablePath is { } path
        && (path.Contains("\\windowsapps\\claude_", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\anthropicclaude\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>A Claude Code program the Claude app runs for one of its sessions.</summary>
    internal static bool IsClaudeWorker(ProcessSnapshot process) =>
        AgentCatalog.NameWithoutExtension(process.Name) == "claude"
        && process.ExecutablePath is { } path
        && path.Contains("\\claude\\claude-code\\", StringComparison.OrdinalIgnoreCase)
        && (process.CommandLine ?? "").Contains("--input-format stream-json", StringComparison.OrdinalIgnoreCase);

    internal static bool IsChatGptApp(ProcessSnapshot process) =>
        AgentCatalog.NameWithoutExtension(process.Name) is "chatgpt" or "codex"
        && process.ExecutablePath is { } path
        && (path.Contains("\\windowsapps\\openai.codex_", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\windowsapps\\openai.chatgpt", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads one session file of the Claude app (claude-code-sessions\…\local_*.json).</summary>
    public static ClaudeAppSession? ReadClaudeSession(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            string? id = Text(node?["sessionId"]);
            string? transcript = Text(node?["cliSessionId"]);
            if (id is null || transcript is null)
            {
                return null;
            }

            return new ClaudeAppSession(
                id,
                transcript,
                Text(node?["title"]),
                Text(node?["cwd"]),
                node?["isArchived"] is JsonValue archived && archived.TryGetValue(out bool a) && a,
                Millis(node?["lastActivityAt"]),
                Millis(node?["lastFocusedAt"]),
                node?["interruptedAwaitingPerson"] is JsonObject);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the last lines of a Claude Code transcript say about its turn. Lines the turn does not
    /// turn on (attachments, summaries, hook reports, a subagent's own lines) are passed over.
    /// </summary>
    public static TurnReading ReadClaudeTurn(IReadOnlyList<string> lines)
    {
        DateTime? at = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            JsonNode? node = Parse(lines[i]);
            if (node is null)
            {
                continue;
            }

            at ??= Time(node["timestamp"]);
            string? type = Text(node["type"]);
            if (type is not ("user" or "assistant") || node["isSidechain"] is JsonValue side && side.TryGetValue(out bool s) && s)
            {
                continue;
            }

            JsonNode? message = node["message"];
            JsonNode? content = message?["content"];
            if (type == "assistant")
            {
                if (content is JsonArray blocks && blocks.OfType<JsonObject>().LastOrDefault(b => Text(b["type"]) == "tool_use") is { } tool)
                {
                    return new TurnReading(AskingTools.Contains(Text(tool["name"]) ?? "") ? TurnState.NeedsInput : TurnState.Working, at);
                }

                return new TurnReading(Text(message?["stop_reason"]) is "end_turn" or "stop_sequence" ? TurnState.Ended : TurnState.Working, at);
            }

            // A user line: a prompt or a tool's result, both of which the agent is now working on.
            if (node["isMeta"] is JsonValue meta && meta.TryGetValue(out bool m) && m)
            {
                continue;
            }

            string said = content is JsonArray parts
                ? string.Concat(parts.OfType<JsonObject>().Select(p => Text(p["text"]) ?? ""))
                : Text(content) ?? "";
            return new TurnReading(said.StartsWith("[Request interrupted by user", StringComparison.Ordinal) ? TurnState.Stopped : TurnState.Working, at);
        }

        return new TurnReading(TurnState.Unknown, at);
    }

    /// <summary>Reads the first line of a Codex transcript (sessions\YYYY\MM\DD\rollout-*.jsonl).</summary>
    public static CodexThreadMeta? ReadCodexMeta(string firstLine)
    {
        JsonNode? node = Parse(firstLine);
        if (node is null || Text(node["type"]) != "session_meta" || node["payload"] is not JsonObject payload || Text(payload["id"]) is not { } id)
        {
            return null;
        }

        bool subagent = Text(payload["parent_thread_id"]) is not null
            || payload["source"] is JsonObject
            || (Text(payload["thread_source"]) is { } source && source != "user");
        bool fromApp = (Text(payload["originator"]) ?? "").Contains("desktop", StringComparison.OrdinalIgnoreCase);
        return new CodexThreadMeta(id, Text(payload["cwd"]), fromApp, subagent);
    }

    /// <summary>What the last lines of a Codex transcript say about its turn: its last task event.</summary>
    public static TurnReading ReadCodexTurn(IReadOnlyList<string> lines)
    {
        DateTime? at = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            JsonNode? node = Parse(lines[i]);
            if (node is null)
            {
                continue;
            }

            at ??= Time(node["timestamp"]);
            if (Text(node["type"]) != "event_msg")
            {
                continue;
            }

            switch (Text(node["payload"]?["type"]))
            {
                case "task_started":
                    return new TurnReading(TurnState.Working, at);
                case "task_complete":
                    return new TurnReading(TurnState.Ended, at);
                case "turn_aborted":
                    return new TurnReading(TurnState.Stopped, at);
            }
        }

        return new TurnReading(TurnState.Unknown, at);
    }

    /// <summary>The names the ChatGPT app gave its threads (session_index.jsonl), the latest name winning.</summary>
    public static Dictionary<string, string> ReadCodexTitles(IEnumerable<string> lines)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            JsonNode? node = Parse(line);
            if (Text(node?["id"]) is { } id && Text(node?["thread_name"]) is { } name)
            {
                titles[id] = name;
            }
        }

        return titles;
    }

    /// <summary>
    /// What the board shows for a session. A finished turn is "Done" until the user has looked at
    /// the session since it finished; a turn said to be running that has been quiet for longer
    /// than <paramref name="stale"/> is taken to have died with its program.
    /// </summary>
    /// <param name="viewed">When the user last looked at the session, if known.</param>
    public static AgentState StateFor(TurnReading turn, DateTime? viewed, DateTime now, TimeSpan stale, bool awaitingPerson = false)
    {
        if (awaitingPerson)
        {
            return AgentState.NeedsInput;
        }

        bool quiet = turn.At is not { } at || now - at > stale;
        return turn.State switch
        {
            TurnState.Working when !quiet => AgentState.Working,
            TurnState.NeedsInput when !quiet => AgentState.NeedsInput,
            TurnState.Ended when turn.At is { } ended && (viewed is not { } seen || seen < ended) => AgentState.Done,
            _ => AgentState.Idle,
        };
    }

    /// <summary>The folder's own name, for telling sessions apart when they have no title.</summary>
    public static string? FolderName(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : Path.GetFileName(folder.Trim().TrimEnd('\\', '/')) is { Length: > 0 } name ? name : folder;

    private static JsonNode? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            // The first line of a tail read from the middle of the file, or one still being written.
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static DateTime? Millis(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out long ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;

    private static DateTime? Time(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at.UtcDateTime
            : null;
}
