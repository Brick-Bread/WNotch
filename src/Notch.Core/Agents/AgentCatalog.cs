namespace Notch.Core.Agents;

/// <summary>A coding agent Notch knows how to recognise among the running programs.</summary>
/// <param name="Id">Stable name used in settings, e.g. "claude".</param>
/// <param name="ExecutableNames">Program names (no .exe, lowercase) that are the agent itself.</param>
/// <param name="CommandLineMarkers">
/// Text found in the command line when the agent runs inside a generic runtime such as node or
/// python, e.g. <c>@anthropic-ai/claude-code</c>. Lowercase.
/// </param>
public sealed record AgentDefinition(
    string Id,
    string DisplayName,
    AgentKind Kind,
    string Glyph,
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> CommandLineMarkers);

/// <summary>The agents Notch recognises, and the rules that tell them apart from lookalikes.</summary>
public static class AgentCatalog
{
    /// <summary>Programs that run an agent written in a scripting language; their command line says which.</summary>
    private static readonly string[] Runtimes = ["node", "bun", "deno", "python", "python3", "pythonw", "py", "uv", "uvx", "npx"];

    /// <summary>
    /// Folders whose programs are not command line agents: the Claude desktop app is also called
    /// claude.exe and carries its own copy of Claude Code for its Code tab, and the Codex desktop
    /// app keeps its servers in its own folder.
    /// </summary>
    private static readonly string[] ExcludedPaths =
    [
        "\\anthropicclaude\\", "\\windowsapps\\", "\\claude desktop\\", "/applications/claude.app/",
        "\\roaming\\claude\\claude-code\\", "\\openai\\codex\\",
    ];

    /// <summary>
    /// Command line text that means a program runs as a server, or for one answer, and not as a
    /// session the user sits in: Codex's app server, an agent another program drives over
    /// stdin and stdout, and print mode.
    /// </summary>
    private static readonly string[] BackgroundMarkers =
    [
        " app-server", " exec-server", " mcp-server", " mcp serve", " --input-format stream-json", " --input-format=stream-json",
        " --output-format stream-json", " --output-format=stream-json", " --sdk-url", " --print", " -p ",
    ];

    public static IReadOnlyList<AgentDefinition> All { get; } =
    [
        new("claude", "Claude", AgentKind.Claude, "", ["claude"],
            ["@anthropic-ai/claude-code", "@anthropic-ai\\claude-code", "claude-code/cli", "claude-code\\cli"]),
        new("codex", "Codex", AgentKind.Codex, "", ["codex"],
            ["@openai/codex", "@openai\\codex"]),
        new("gemini", "Gemini", AgentKind.Gemini, "", ["gemini"],
            ["@google/gemini-cli", "@google\\gemini-cli", "gemini-cli"]),
        new("aider", "Aider", AgentKind.Aider, "", ["aider"],
            ["aider-chat", "-m aider", "scripts\\aider", "bin/aider", "aider\\main", "aider/main"]),
    ];

    public static AgentDefinition? ById(string id) =>
        All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static AgentDefinition? ForKind(AgentKind kind) => All.FirstOrDefault(a => a.Kind == kind);

    /// <summary>The agent a command in a terminal button starts, going by the program's file name.</summary>
    public static AgentDefinition? ForCommand(string command)
    {
        string file = Path.GetFileNameWithoutExtension(command);
        return All.FirstOrDefault(a => a.ExecutableNames.Any(n => file.Contains(n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The agent this running program is, or null when it is not one.</summary>
    public static AgentDefinition? Match(ProcessSnapshot process)
    {
        string name = NameWithoutExtension(process.Name);
        string commandLine = (process.CommandLine ?? "").ToLowerInvariant();

        // Chromium and Electron start helper processes of themselves with --type=; those are never an agent.
        if (commandLine.Contains(" --type=", StringComparison.Ordinal))
        {
            return null;
        }

        // Padded so a marker at the very end of the command line matches too.
        string padded = commandLine + " ";
        if (BackgroundMarkers.Any(marker => padded.Contains(marker, StringComparison.Ordinal)))
        {
            return null;
        }

        if (process.ExecutablePath is { Length: > 0 } path
            && ExcludedPaths.Any(excluded => path.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (AgentDefinition agent in All)
        {
            if (agent.ExecutableNames.Contains(name))
            {
                return agent;
            }
        }

        if (Runtimes.Contains(name))
        {
            foreach (AgentDefinition agent in All)
            {
                if (agent.CommandLineMarkers.Any(marker => commandLine.Contains(marker, StringComparison.Ordinal)))
                {
                    return agent;
                }
            }
        }

        return null;
    }

    /// <summary>Whether the name could be a runtime whose command line must be read to tell what it runs.</summary>
    public static bool IsRuntime(string processName) => Runtimes.Contains(NameWithoutExtension(processName));

    /// <summary>Whether the name is one of the agents' own programs.</summary>
    public static bool IsAgentProgram(string processName) =>
        All.Any(a => a.ExecutableNames.Contains(NameWithoutExtension(processName)));

    internal static string NameWithoutExtension(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }
}
