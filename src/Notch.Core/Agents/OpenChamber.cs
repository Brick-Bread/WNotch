using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Core.Agents;

/// <summary>
/// OpenChamber, the desktop app built on opencode. It runs an opencode server of its own, which loads
/// Notch's opencode plugin like any other; the plugin says when it runs there, so its sessions are
/// listed as OpenChamber's, one by one under their titles. Pure logic: the app does the files, the
/// processes and the network.
/// </summary>
public static class OpenChamber
{
    /// <summary>What opencode's plugin calls the program it runs in, inside OpenChamber.</summary>
    public const string HostName = "openchamber";

    public const string DisplayName = "OpenChamber";

    /// <summary>Starts the key of every board entry for OpenChamber, so they are told from the others.</summary>
    public const string KeyPrefix = "openchamber:";

    /// <summary>The entry for the app itself while none of its sessions has reported.</summary>
    public const string AppKey = KeyPrefix + "app";

    /// <summary>The link that opens OpenChamber at its front, starting it if need be.</summary>
    public const string FocusLink = "openchamber://focus";

    private const string ProgramName = "openchamber";

    /// <summary>Whether a hook event came from opencode inside OpenChamber.</summary>
    public static bool IsFrom(AgentHooks.HookMessage message) => message.Agent == "opencode" && message.Host == HostName;

    public static string KeyFor(string sessionId) => KeyPrefix + sessionId;

    /// <summary>The OpenChamber session a board key is for, or null for the app's own entry and others.</summary>
    public static string? SessionOf(string key) =>
        key.StartsWith(KeyPrefix, StringComparison.Ordinal) && key != AppKey ? key[KeyPrefix.Length..] : null;

    /// <summary>The link that shows one session in OpenChamber.</summary>
    public static string SessionLink(string sessionId) => "openchamber://session/" + Uri.EscapeDataString(sessionId);

    /// <summary>Whether the process scan should read this program's command line, since it may be OpenChamber.</summary>
    public static bool IsProgram(string processName) => AgentCatalog.NameWithoutExtension(processName) == ProgramName;

    /// <summary>
    /// OpenChamber's main process, which owns its windows: the one the others were started from. It is
    /// Electron, whose helper processes have the same name and <c>--type=</c> in their command line.
    /// </summary>
    public static int? MainPid(IReadOnlyList<ProcessSnapshot> processes)
    {
        ProcessSnapshot[] app = [.. processes.Where(p => IsProgram(p.Name) && !(p.CommandLine ?? "").Contains(" --type=", StringComparison.OrdinalIgnoreCase))];
        var pids = app.Select(p => p.Pid).ToHashSet();
        return (app.FirstOrDefault(p => !pids.Contains(p.ParentPid)) ?? app.FirstOrDefault())?.Pid;
    }

    /// <summary>OpenChamber's settings file: in <c>OPENCHAMBER_DATA_DIR</c> when set, otherwise <c>.config\openchamber</c> in the home folder.</summary>
    public static string SettingsPath(string? dataDirVariable, string homeDirectory) =>
        Path.Combine(!string.IsNullOrWhiteSpace(dataDirVariable) ? dataDirVariable : Path.Combine(homeDirectory, ".config", "openchamber"), "settings.json");

    /// <summary>Where the desktop app's local server listens, from its settings file, and the token its own tools use.</summary>
    public static (int Port, string? Token)? ReadServer(string? settingsJson)
    {
        try
        {
            JsonNode? node = string.IsNullOrWhiteSpace(settingsJson) ? null : JsonNode.Parse(settingsJson);
            if (node?["desktopLocalPort"] is JsonValue value && value.TryGetValue(out int port) && port is > 0 and < 65536)
            {
                return (port, node["desktopLocalClientToken"] is JsonValue token && token.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // An unreadable file is the same as none.
        }

        return null;
    }

    /// <summary>What to ask OpenChamber's local server for a new session in <paramref name="folder"/>.</summary>
    public static string NewSessionRequest(string folder) =>
        new JsonObject { ["action"] = "session.create", ["input"] = new JsonObject { ["directory"] = folder } }.ToJsonString();

    /// <summary>The id of the session OpenChamber made, from its answer: the first opencode session id in it.</summary>
    public static string? ReadSessionId(string? responseJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(responseJson) ? null : FindSessionId(JsonNode.Parse(responseJson), 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindSessionId(JsonNode? node, int depth)
    {
        if (depth > 6)
        {
            return null;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (string key in new[] { "sessionID", "sessionId", "id" })
                {
                    if (obj[key] is JsonValue value && value.TryGetValue(out string? text) && text.StartsWith("ses", StringComparison.Ordinal))
                    {
                        return text;
                    }
                }

                foreach ((string _, JsonNode? child) in obj)
                {
                    if (FindSessionId(child, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonArray array:
                foreach (JsonNode? child in array)
                {
                    if (FindSessionId(child, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }
}
