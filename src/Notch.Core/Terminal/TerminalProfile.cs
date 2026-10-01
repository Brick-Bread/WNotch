using Notch.Core.Agents;

namespace Notch.Core.Terminal;

/// <summary>Something the built-in terminal can launch.</summary>
/// <param name="Command">Bare command name, resolved through PATH when the session starts.</param>
/// <param name="InstallHint">Shown when <paramref name="Command"/> is not installed.</param>
public sealed record TerminalProfile(string Id, string DisplayName, string Command, string Glyph, AgentKind Agent, string InstallHint)
{
    public static TerminalProfile Claude { get; } = new(
        "claude", "Claude", "claude", "", AgentKind.Claude,
        "The Claude Code CLI is not on your PATH. Install it from claude.com/claude-code, then open a new session.");

    public static TerminalProfile Codex { get; } = new(
        "codex", "Codex", "codex", "", AgentKind.Codex,
        "The Codex CLI is not on your PATH. Install it with: npm install -g @openai/codex");

    public static TerminalProfile Shell { get; } = new(
        "shell", "PowerShell", "powershell", "", AgentKind.None,
        "PowerShell was not found on your PATH.");

    public static IReadOnlyList<TerminalProfile> All { get; } = [Claude, Codex, Shell];
}
