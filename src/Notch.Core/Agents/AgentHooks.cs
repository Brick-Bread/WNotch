using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Core.Agents;

/// <summary>
/// The contract between the app, the agent CLIs and Notch.Hook.exe. The app starts each CLI
/// with hooks pointing at the hook executable; the hook forwards what the CLI tells it to the
/// app's named pipe, tagged with the session it came from.
/// </summary>
public static class AgentHooks
{
    /// <summary>Environment variable naming the pipe the hook should write to.</summary>
    public const string PipeVariable = "NOTCH_PIPE";

    /// <summary>Environment variable identifying the terminal session the CLI runs in.</summary>
    public const string SessionVariable = "NOTCH_SESSION";

    private static readonly string[] ClaudeEvents = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    /// <summary>The settings JSON passed to <c>claude --settings</c>: every relevant hook runs the hook executable.</summary>
    public static string BuildClaudeSettings(string hookExecutablePath)
    {
        var hooks = new JsonObject();
        foreach (string name in ClaudeEvents)
        {
            hooks[name] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = ToShellCommand(hookExecutablePath),
                }),
            });
        }

        return new JsonObject { ["hooks"] = hooks }.ToJsonString();
    }

    /// <summary>Extra arguments that make an agent CLI report to the hook executable.</summary>
    /// <param name="claudeSettingsPath">Where the output of <see cref="BuildClaudeSettings"/> was saved.</param>
    public static IReadOnlyList<string> BuildArguments(AgentKind kind, string hookExecutablePath, string claudeSettingsPath) => kind switch
    {
        AgentKind.Claude => ["--settings", claudeSettingsPath],

        // A TOML literal string keeps backslashes as-is, but cannot contain a single quote.
        AgentKind.Codex when !hookExecutablePath.Contains('\'') => ["-c", $"notify=['{hookExecutablePath}']"],
        _ => [],
    };

    /// <summary>Formats the line the hook executable writes to the pipe.</summary>
    public static string FormatMessage(string sessionId, string payload) =>
        JsonSerializer.Serialize(new JsonObject { ["session"] = sessionId, ["payload"] = payload });

    /// <summary>Reads a line written by the hook executable.</summary>
    /// <param name="eventName">Claude's <c>hook_event_name</c> or Codex's notification <c>type</c>.</param>
    public static bool TryParseMessage(string line, out string sessionId, out string eventName)
    {
        sessionId = "";
        eventName = "";

        try
        {
            JsonNode? message = JsonNode.Parse(line);
            string? session = (string?)message?["session"];
            string? payloadText = (string?)message?["payload"];
            if (string.IsNullOrEmpty(session) || string.IsNullOrWhiteSpace(payloadText))
            {
                return false;
            }

            JsonNode? payload = JsonNode.Parse(payloadText);
            string? name = (string?)payload?["hook_event_name"] ?? (string?)payload?["type"];
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            sessionId = session;
            eventName = name;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Claude runs hook commands through a shell (Git Bash by default on Windows), where
    /// backslashes are escapes, so the path is written with forward slashes.
    /// </summary>
    private static string ToShellCommand(string executablePath)
    {
        string path = executablePath.Replace('\\', '/');
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
