using System.IO;
using Notch.Core.Agents;

namespace Notch.App.Agents;

/// <summary>
/// Reads and writes the settings files of Claude Code and Codex so their hooks report to Notch
/// whichever terminal they run in. Only ever done because the user ticked the box in Settings;
/// each file gets a backup the first time it is changed, and unticking takes Notch's lines out again.
/// </summary>
internal static class AgentHookFiles
{
    public const string BackupSuffix = ".notch-backup";

    public static string HookExecutable => Path.Combine(AppContext.BaseDirectory, "Notch.Hook.exe");

    public static string ClaudeSettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "settings.json");

    public static string CodexConfigPath => Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "config.toml");

    /// <summary>What switching the hooks on or off would do, for showing to the user before it happens.</summary>
    /// <param name="Files">The files that would be written, with a line saying what happens to each.</param>
    /// <param name="Problems">Things the user should know: a file Notch will not touch, and why.</param>
    public sealed record Plan(IReadOnlyList<(string Path, string What)> Files, IReadOnlyList<string> Problems)
    {
        public bool Changes => Files.Count > 0;
    }

    /// <summary>Whether Notch's hooks are in the agents' settings files right now.</summary>
    public static bool IsInstalled() =>
        AgentHookEditor.ClaudeHasHooks(Read(ClaudeSettingsPath)) || AgentHookEditor.CodexHasHook(Read(CodexConfigPath));

    public static Plan PlanFor(bool enable)
    {
        var files = new List<(string, string)>();
        var problems = new List<string>();

        HookEdit claude = enable
            ? AgentHookEditor.AddClaudeHooks(Read(ClaudeSettingsPath), HookExecutable)
            : AgentHookEditor.RemoveClaudeHooks(Read(ClaudeSettingsPath));
        if (claude.Changed)
        {
            files.Add((ClaudeSettingsPath, enable ? "adds Notch's six hooks" : "removes Notch's hooks"));
        }
        else if (claude.Problem is not null)
        {
            problems.Add(claude.Problem);
        }

        HookEdit codex = enable
            ? AgentHookEditor.AddCodexHook(Read(CodexConfigPath), HookExecutable)
            : AgentHookEditor.RemoveCodexHook(Read(CodexConfigPath));
        if (codex.Changed)
        {
            files.Add((CodexConfigPath, enable ? "adds a notify line" : "removes Notch's notify line"));
        }
        else if (codex.Problem is not null)
        {
            problems.Add(codex.Problem);
        }

        return new Plan(files, problems);
    }

    /// <summary>Makes the change <see cref="PlanFor"/> described. Throws <see cref="IOException"/> if a file cannot be written.</summary>
    public static void Apply(bool enable)
    {
        HookEdit claude = enable
            ? AgentHookEditor.AddClaudeHooks(Read(ClaudeSettingsPath), HookExecutable)
            : AgentHookEditor.RemoveClaudeHooks(Read(ClaudeSettingsPath));
        if (claude.Changed)
        {
            Write(ClaudeSettingsPath, claude.Text);
        }

        HookEdit codex = enable
            ? AgentHookEditor.AddCodexHook(Read(CodexConfigPath), HookExecutable)
            : AgentHookEditor.RemoveCodexHook(Read(CodexConfigPath));
        if (codex.Changed)
        {
            Write(CodexConfigPath, codex.Text);
        }
    }

    /// <summary>
    /// When the hooks are on, points them at this copy of the hook program. The folder Notch is
    /// installed in can change (a reinstall elsewhere), and a hook that names a program that is
    /// gone is a hook that does nothing. Only Notch's own entries are touched.
    /// </summary>
    public static void RefreshPaths()
    {
        try
        {
            // The hook is written with forward slashes (the agents run it through a shell).
            string forward = HookExecutable.Replace('\\', '/');
            string? claude = Read(ClaudeSettingsPath);
            if (AgentHookEditor.ClaudeHasHooks(claude) && claude?.Contains(forward, StringComparison.OrdinalIgnoreCase) != true)
            {
                Apply(enable: false);
                Apply(enable: true);
                return;
            }

            string? codex = Read(CodexConfigPath);
            if (AgentHookEditor.CodexHasHook(codex) && codex?.Contains(HookExecutable, StringComparison.OrdinalIgnoreCase) != true)
            {
                Apply(enable: false);
                Apply(enable: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left as it was; the hooks keep working if the old path is still there.
        }
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A copy of what was there before Notch first touched it; never overwritten by later changes.
        string backup = path + BackupSuffix;
        if (File.Exists(path) && !File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        string temporary = path + ".notch-tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
