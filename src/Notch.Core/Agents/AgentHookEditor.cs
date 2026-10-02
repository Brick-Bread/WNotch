using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Notch.Core.Agents;

/// <summary>What an edit of an agent's own settings file came to.</summary>
/// <param name="Text">The file's new contents (the old ones when nothing changed).</param>
/// <param name="Problem">Why nothing was changed, when that is because of something the user should know.</param>
public sealed record HookEdit(string Text, bool Changed, string? Problem = null);

/// <summary>
/// Adds Notch's hooks to the settings files the agents read for every session, so agents started
/// anywhere report what they are doing, and takes them out again. Everything that is not Notch's
/// is left exactly as it was; a file that cannot be read is never touched.
/// </summary>
public static partial class AgentHookEditor
{
    /// <summary>Text that marks a hook command as Notch's.</summary>
    private const string Marker = "Notch.Hook";

    private const string CodexComment = "# Notch: reports when Codex finishes a turn. Remove it in Notch's settings.";

    private static readonly string[] ClaudeEvents = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>The hook program as the agents' shells want it written (forward slashes, quoted when it has a space).</summary>
    public static string ShellCommand(string hookExecutable)
    {
        string path = hookExecutable.Replace('\\', '/');
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }

    public static bool ClaudeHasHooks(string? json) => FindClaudeEntries(json).Any();

    /// <summary>Adds the six hooks to Claude's <c>settings.json</c> (null or empty text starts a new file).</summary>
    public static HookEdit AddClaudeHooks(string? json, string hookExecutable)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return new HookEdit(json ?? "", false, "Claude's settings file could not be read, so it was left alone.");
        }

        if (root["hooks"] is not null and not JsonObject)
        {
            return new HookEdit(json ?? "", false, "The \"hooks\" setting in Claude's settings file is not what Notch expects, so it was left alone.");
        }

        var hooks = root["hooks"] as JsonObject ?? [];
        bool changed = false;
        foreach (string name in ClaudeEvents)
        {
            if (hooks[name] is not null and not JsonArray)
            {
                return new HookEdit(json ?? "", false, $"The \"{name}\" hooks in Claude's settings file are not what Notch expects, so it was left alone.");
            }

            var list = hooks[name] as JsonArray ?? [];
            if (list.Any(IsNotchGroup))
            {
                hooks[name] = list;
                continue;
            }

            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = ShellCommand(hookExecutable) }),
            });
            hooks[name] = list;
            changed = true;
        }

        root["hooks"] = hooks;
        return changed ? new HookEdit(root.ToJsonString(Pretty), true) : new HookEdit(json ?? "", false);
    }

    /// <summary>Takes Notch's hooks out of Claude's settings, leaving every other hook and setting.</summary>
    public static HookEdit RemoveClaudeHooks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HookEdit("", false);
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return new HookEdit(json, false, "Claude's settings file could not be read, so it was left alone.");
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            return new HookEdit(json, false);
        }

        bool changed = false;
        foreach (string name in hooks.Select(h => h.Key).ToArray())
        {
            if (hooks[name] is not JsonArray list)
            {
                continue;
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (IsNotchGroup(list[i]))
                {
                    list.RemoveAt(i);
                    changed = true;
                }
            }

            if (list.Count == 0)
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        return changed ? new HookEdit(root.ToJsonString(Pretty), true) : new HookEdit(json, false);
    }

    private static IEnumerable<JsonNode?> FindClaudeEntries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root?["hooks"] is not JsonObject hooks)
        {
            yield break;
        }

        foreach ((string _, JsonNode? list) in hooks)
        {
            if (list is JsonArray array)
            {
                foreach (JsonNode? group in array.Where(IsNotchGroup))
                {
                    yield return group;
                }
            }
        }
    }

    /// <summary>A hook group (<c>{ "hooks": [ { "command": ... } ] }</c>) that runs Notch's hook program.</summary>
    private static bool IsNotchGroup(JsonNode? group) =>
        group?["hooks"] is JsonArray commands
        && commands.Any(c => ((string?)c?["command"])?.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true);

    public static bool CodexHasHook(string? toml) =>
        !string.IsNullOrEmpty(toml) && TopLevelNotify(toml) is { } line && line.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a top-level <c>notify</c> line to Codex's <c>config.toml</c>. Codex takes only one, so an
    /// existing one that is not Notch's is left alone and reported.
    /// </summary>
    public static HookEdit AddCodexHook(string? toml, string hookExecutable)
    {
        toml ??= "";
        if (hookExecutable.Contains('\''))
        {
            return new HookEdit(toml, false, "The path of Notch's hook program contains a quote, which Codex's settings cannot hold.");
        }

        if (TopLevelNotify(toml) is { } existing)
        {
            return existing.Contains(Marker, StringComparison.OrdinalIgnoreCase)
                ? new HookEdit(toml, false)
                : new HookEdit(toml, false, "Codex already has a notify command of your own. Codex runs only one, so Notch did not change it.");
        }

        string newline = toml.Contains("\r\n") ? "\r\n" : "\n";
        string block = $"{CodexComment}{newline}notify = ['{hookExecutable}']{newline}";
        return new HookEdit(toml.Length == 0 ? block : block + newline + toml, true);
    }

    /// <summary>Takes the <c>notify</c> line Notch added out of Codex's config.</summary>
    public static HookEdit RemoveCodexHook(string? toml)
    {
        if (string.IsNullOrEmpty(toml))
        {
            return new HookEdit("", false);
        }

        string[] lines = toml.Split('\n');
        var kept = new List<string>();
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            bool isOurs = IsNotifyLine(trimmed) && trimmed.Contains(Marker, StringComparison.OrdinalIgnoreCase);
            if (isOurs)
            {
                changed = true;
                if (kept.Count > 0 && kept[^1].Trim() == CodexComment)
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                // The blank line that separated the block from the rest goes with it.
                if (i + 1 < lines.Length && lines[i + 1].Trim().Length == 0)
                {
                    i++;
                }

                continue;
            }

            kept.Add(lines[i]);
        }

        return changed ? new HookEdit(string.Join('\n', kept), true) : new HookEdit(toml, false);
    }

    /// <summary>The top-level <c>notify</c> line, if there is one before the first table.</summary>
    private static string? TopLevelNotify(string toml)
    {
        foreach (string raw in toml.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                return null;
            }

            if (IsNotifyLine(line))
            {
                return line;
            }
        }

        return null;
    }

    private static bool IsNotifyLine(string line) => NotifyPattern().IsMatch(line);

    [GeneratedRegex(@"^notify\s*=")]
    private static partial Regex NotifyPattern();
}
