using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Core.Agents;

/// <summary>What an agent asks the user for.</summary>
public enum ApprovalKind
{
    /// <summary>To use a tool: run a command, edit a file, fetch a page.</summary>
    Tool,

    /// <summary>To answer one or more questions by picking from options (Claude's AskUserQuestion).</summary>
    Question,

    /// <summary>To go ahead with a plan it wrote (Claude's ExitPlanMode).</summary>
    Plan,
}

public enum ApprovalDecision
{
    Allow,

    /// <summary>Allow, and do not ask again for the same thing, as the agent's own prompt offers it.</summary>
    AllowAlways,
    Deny,
}

public sealed record ApprovalOption(string Label, string? Description);

/// <param name="Text">The question as the agent asked it, which is also the key its answer goes under.</param>
public sealed record ApprovalQuestion(string Text, string? Header, IReadOnlyList<ApprovalOption> Options, bool MultiSelect);

/// <summary>A prompt an agent shows the user, as the notch shows it.</summary>
/// <param name="AgentId">The agent's id in <see cref="AgentCatalog"/>.</param>
/// <param name="AgentSession">The agent's own id for its session.</param>
/// <param name="Title">What it wants, in a few words: "Run a command", "Edit App.cs".</param>
/// <param name="Subject">The one line that says exactly what: the command, the file, the address.</param>
/// <param name="Detail">More to read before answering, such as the change to a file or the plan; null for none.</param>
/// <param name="CanAlwaysAllow">The agent can remember an "always allow" for this.</param>
public sealed record ApprovalRequest(
    string AgentId,
    string? AgentSession,
    string? Folder,
    ApprovalKind Kind,
    string Title,
    string? Subject,
    string? Detail,
    IReadOnlyList<ApprovalQuestion> Questions,
    bool CanAlwaysAllow);

/// <summary>The user's answer to an <see cref="ApprovalRequest"/>.</summary>
/// <param name="Answers">For a question: the labels picked for each question, by its text.</param>
public sealed record ApprovalAnswer(ApprovalDecision Decision, IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers = null);

/// <summary>
/// Reads the permission prompts Claude Code and opencode send through their hooks, and writes the answers
/// in the form each takes back. Pure logic: the waiting and the asking are the app's.
/// </summary>
public static class AgentApprovals
{
    /// <summary>The event opencode's plugin sends when a prompt it showed Notch was answered somewhere else.</summary>
    public const string OpenCodeRepliedEvent = "PermissionReplied";

    /// <summary>What Claude is told when the user says no in the notch.</summary>
    public const string DeniedMessage = "The user denied this from Notch.";

    private const int MaxDetailLines = 60;
    private const int MaxDetailLength = 4000;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>The prompt a waiting hook carries, or null when it is not one Notch can show.</summary>
    public static ApprovalRequest? Read(AgentHooks.HookMessage message)
    {
        if (!message.WantsReply || message.EventName != AgentHooks.PermissionRequestEvent || Parse(message.Payload) is not JsonObject payload)
        {
            return null;
        }

        return message.Agent == "opencode" ? ReadOpenCode(payload, message) : ReadClaude(payload, message);
    }

    /// <summary>The line to send back to the hook for <paramref name="answer"/>.</summary>
    public static string Reply(AgentHooks.HookMessage message, ApprovalAnswer answer)
    {
        JsonObject payload = Parse(message.Payload) as JsonObject ?? [];
        if (message.Agent == "opencode")
        {
            string decision = answer.Decision switch
            {
                ApprovalDecision.AllowAlways => "always",
                ApprovalDecision.Allow => "once",
                _ => "reject",
            };

            return new JsonObject { ["decision"] = decision }.ToJsonString(Compact);
        }

        var result = new JsonObject();
        if (answer.Decision == ApprovalDecision.Deny)
        {
            result["behavior"] = "deny";
            result["message"] = DeniedMessage;
        }
        else
        {
            result["behavior"] = "allow";
            if (answer.Answers is { Count: > 0 } answers && payload["tool_input"] is JsonObject input)
            {
                // The tool's input as Claude sent it, with the answers it asks hooks to fill in.
                var updated = (JsonObject)input.DeepClone();
                var picked = new JsonObject();
                foreach ((string question, IReadOnlyList<string> labels) in answers)
                {
                    picked[question] = string.Join(", ", labels);
                }

                updated["answers"] = picked;
                result["updatedInput"] = updated;
            }

            if (answer.Decision == ApprovalDecision.AllowAlways && payload["permission_suggestions"] is JsonArray { Count: > 0 } suggestions)
            {
                result["updatedPermissions"] = suggestions.DeepClone();
            }
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = AgentHooks.PermissionRequestEvent,
                ["decision"] = result,
            },
        }.ToJsonString(Compact);
    }

    /// <summary>
    /// Whether <paramref name="later"/>, an event from after the prompt, means it was answered somewhere else
    /// (in the agent's own terminal, or another window), so the notch no longer needs to ask.
    /// </summary>
    public static bool IsSettledBy(AgentHooks.HookMessage prompt, AgentHooks.HookMessage later)
    {
        if (ReferenceEquals(prompt, later) || later.Agent != prompt.Agent || prompt.AgentSession is null || later.AgentSession != prompt.AgentSession)
        {
            return false;
        }

        if (prompt.Agent == "opencode")
        {
            return later.EventName == OpenCodeRepliedEvent
                ? Text(Parse(later.Payload)?["requestID"]) == Text(Parse(prompt.Payload)?["requestID"])
                : later.EventName is "Stop" or "SessionEnd";
        }

        switch (later.EventName)
        {
            // The turn moved on, or ended: whatever it asked is settled.
            case "UserPromptSubmit" or "Stop" or "SessionEnd":
                return true;

            // The tool it asked about has run, so it was allowed. Other tools of the same turn may run meanwhile.
            case "PostToolUse":
                JsonNode? asked = Parse(prompt.Payload);
                JsonNode? ran = Parse(later.Payload);
                return Text(asked?["tool_name"]) == Text(ran?["tool_name"])
                    && JsonNode.DeepEquals(asked?["tool_input"], ran?["tool_input"]);

            default:
                return false;
        }
    }

    private static ApprovalRequest ReadClaude(JsonObject payload, AgentHooks.HookMessage message)
    {
        string tool = Text(payload["tool_name"]) ?? "a tool";
        JsonObject input = InputOf(payload["tool_input"]);
        bool canRemember = payload["permission_suggestions"] is JsonArray { Count: > 0 };
        ApprovalRequest Make(ApprovalKind kind, string title, string? subject, string? detail, IReadOnlyList<ApprovalQuestion>? questions = null) =>
            new("claude", message.AgentSession, message.Folder, kind, title, subject, Trim(detail), questions ?? [], canRemember && kind == ApprovalKind.Tool);

        switch (tool)
        {
            case "AskUserQuestion":
                return Make(ApprovalKind.Question, "Claude asks", null, null, Questions(input["questions"]));

            case "ExitPlanMode":
                return Make(ApprovalKind.Plan, "Ready to start on the plan?", Text(input["planFilePath"]), Text(input["plan"]));

            case "Bash" or "PowerShell":
                return Make(ApprovalKind.Tool, "Run a command", Text(input["command"]), Text(input["description"]));

            case "Edit":
                return Make(ApprovalKind.Tool, $"Edit {FileName(input["file_path"])}", Text(input["file_path"]),
                    Diff(Text(input["old_string"]), Text(input["new_string"])));

            case "MultiEdit":
                var edits = new StringBuilder();
                foreach (JsonObject edit in (input["edits"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    edits.Append(Diff(Text(edit["old_string"]), Text(edit["new_string"]))).Append('\n');
                }

                return Make(ApprovalKind.Tool, $"Edit {FileName(input["file_path"])}", Text(input["file_path"]), edits.ToString().TrimEnd());

            case "Write":
                return Make(ApprovalKind.Tool, $"Write {FileName(input["file_path"])}", Text(input["file_path"]), Text(input["content"]));

            case "NotebookEdit":
                return Make(ApprovalKind.Tool, $"Edit {FileName(input["notebook_path"])}", Text(input["notebook_path"]), Text(input["new_source"]));

            case "Read":
                return Make(ApprovalKind.Tool, $"Read {FileName(input["file_path"])}", Text(input["file_path"]), null);

            case "Glob" or "Grep":
                return Make(ApprovalKind.Tool, "Search files", Text(input["pattern"]), Text(input["path"]));

            case "WebFetch":
                return Make(ApprovalKind.Tool, "Fetch a web page", Text(input["url"]), Text(input["prompt"]));

            case "WebSearch":
                return Make(ApprovalKind.Tool, "Search the web", Text(input["query"]), null);

            case "Task" or "Agent":
                return Make(ApprovalKind.Tool, "Start a subagent", Text(input["description"]), Text(input["prompt"]));
        }

        // An MCP tool is named mcp__<server>__<tool>.
        string[] parts = tool.Split("__");
        string title = parts is ["mcp", var server, .. var rest] && rest.Length > 0
            ? $"Use {string.Join("__", rest)} from {server}"
            : $"Use {tool}";
        return Make(ApprovalKind.Tool, title, null, input.Count == 0 ? null : input.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ApprovalRequest ReadOpenCode(JsonObject payload, AgentHooks.HookMessage message)
    {
        string action = Text(payload["action"]) ?? "";
        string[] resources = [.. (payload["resources"] as JsonArray ?? []).Select(Text).OfType<string>()];
        string title = action switch
        {
            "bash" => "Run a command",
            "edit" or "write" or "patch" => resources.Length == 1 ? $"Edit {FileName(resources[0])}" : "Edit files",
            "read" => resources.Length == 1 ? $"Read {FileName(resources[0])}" : "Read files",
            "webfetch" => "Fetch a web page",
            "websearch" => "Search the web",
            "external_directory" => "Work outside the project folder",
            "task" => "Start a subagent",
            "doom_loop" => "Keep going after repeating itself",
            "" => "Allow this",
            _ => $"Allow {action}",
        };

        JsonNode? metadata = payload["metadata"];
        string? detail = Text(metadata?["diff"]) ?? Text(payload["message"]) ?? Text(metadata?["description"]);
        bool canRemember = payload["save"] is JsonArray { Count: > 0 };
        return new ApprovalRequest(
            "opencode",
            message.AgentSession,
            message.Folder,
            ApprovalKind.Tool,
            title,
            resources.Length == 0 ? null : string.Join("  ", resources),
            Trim(detail),
            [],
            canRemember);
    }

    private static List<ApprovalQuestion> Questions(JsonNode? node)
    {
        var questions = new List<ApprovalQuestion>();
        foreach (JsonObject question in (node as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(question["question"]) is not { } text)
            {
                continue;
            }

            ApprovalOption[] options = [.. (question["options"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(o => Text(o["label"]) is not null)
                .Select(o => new ApprovalOption(Text(o["label"])!, Text(o["description"])))];
            bool multiple = question["multiSelect"] is JsonValue flag && flag.TryGetValue(out bool m) && m;
            questions.Add(new ApprovalQuestion(text, Text(question["header"]), options, multiple));
        }

        return questions;
    }

    /// <summary>The tool's input. Some versions send it as a JSON string rather than an object.</summary>
    private static JsonObject InputOf(JsonNode? node) => node switch
    {
        JsonObject input => input,
        JsonValue value when value.TryGetValue(out string? text) && Parse(text) is JsonObject parsed => parsed,
        _ => [],
    };

    /// <summary>A change to a file as lines taken out and lines put in.</summary>
    private static string? Diff(string? before, string? after)
    {
        if (before is null && after is null)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (string line in (before ?? "").Split('\n'))
        {
            text.Append("- ").Append(line.TrimEnd('\r')).Append('\n');
        }

        foreach (string line in (after ?? "").Split('\n'))
        {
            text.Append("+ ").Append(line.TrimEnd('\r')).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>At most a screenful: the notch is not where long files are read.</summary>
    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] lines = text.Split('\n');
        string kept = lines.Length > MaxDetailLines ? string.Join('\n', lines.Take(MaxDetailLines)) : text;
        if (kept.Length > MaxDetailLength)
        {
            kept = kept[..MaxDetailLength];
        }

        return kept.Length < text.Length ? kept.TrimEnd() + "\n…" : kept.TrimEnd();
    }

    private static string FileName(JsonNode? path) => FileName(Text(path));

    private static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "a file";
        }

        string name = path.TrimEnd('\\', '/');
        int slash = name.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
