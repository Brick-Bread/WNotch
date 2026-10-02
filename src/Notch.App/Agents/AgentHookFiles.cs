using System.IO;
using Notch.Core.Agents;

namespace Notch.App.Agents;

/// <summary>
/// Reads and writes the settings files of Claude Code and Codex, and puts a plugin in opencode's
/// plugins folder, so they report to Notch whichever terminal they run in. Only ever done because the user ticked the box in Settings;
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

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string OpenCodePluginPath => OpenCodePlugin.PluginPath(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR"), UserProfile);

    /// <summary>The plugin as it ships with this copy of Notch.</summary>
    private static string OpenCodePluginSource => Path.Combine(AppContext.BaseDirectory, "Assets", "opencode", OpenCodePlugin.FileName);

    /// <summary>
    /// Whether opencode is on this PC: its config folder exists, or the program is on the PATH.
    /// Notch does not create the config folder of a program that is not installed.
    /// </summary>
    private static bool OpenCodeIsInstalled() =>
        Directory.Exists(OpenCodePlugin.ConfigDirectory(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR"), UserProfile))
        || Notch.Core.Terminal.CommandResolver.FindOnPath("opencode") is not null;

    /// <summary>Whether Notch's plugin is in opencode's plugins folder right now.</summary>
    public static bool OpenCodePluginInstalled() => OpenCodePlugin.IsNotchs(Read(OpenCodePluginPath));

    /// <summary>What switching the hooks on or off would do, for showing to the user before it happens.</summary>
    /// <param name="Files">The files that would be written, with a line saying what happens to each.</param>
    /// <param name="Problems">Things the user should know: a file Notch will not touch, and why.</param>
    public sealed record Plan(IReadOnlyList<(string Path, string What)> Files, IReadOnlyList<string> Problems)
    {
        public bool Changes => Files.Count > 0;
    }

    /// <summary>Whether Notch's hooks are in the agents' settings files right now.</summary>
    public static bool IsInstalled() =>
        AgentHookEditor.ClaudeHasHooks(Read(ClaudeSettingsPath)) || AgentHookEditor.CodexHasHook(Read(CodexConfigPath)) || OpenCodePluginInstalled();

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

        if (!enable || OpenCodeIsInstalled())
        {
            HookEdit openCode = OpenCodeEdit(enable);
            if (openCode.Changed)
            {
                files.Add((OpenCodePluginPath, enable ? "adds Notch's opencode plugin (one small file)" : "deletes Notch's opencode plugin"));
            }
            else if (openCode.Problem is not null)
            {
                problems.Add(openCode.Problem);
            }
        }

        return new Plan(files, problems);
    }

    /// <summary>Makes the change <see cref="PlanFor"/> described. Throws <see cref="IOException"/> if a file cannot be written.</summary>
    public static void Apply(bool enable)
    {
        ApplyClaude(enable);
        ApplyCodex(enable);
        if (!enable || OpenCodeIsInstalled())
        {
            ApplyOpenCode(enable);
        }
    }

    private static void ApplyClaude(bool enable)
    {
        HookEdit claude = enable
            ? AgentHookEditor.AddClaudeHooks(Read(ClaudeSettingsPath), HookExecutable)
            : AgentHookEditor.RemoveClaudeHooks(Read(ClaudeSettingsPath));
        if (claude.Changed)
        {
            Write(ClaudeSettingsPath, claude.Text);
        }
    }

    private static void ApplyCodex(bool enable)
    {
        HookEdit codex = enable
            ? AgentHookEditor.AddCodexHook(Read(CodexConfigPath), HookExecutable)
            : AgentHookEditor.RemoveCodexHook(Read(CodexConfigPath));
        if (codex.Changed)
        {
            Write(CodexConfigPath, codex.Text);
        }
    }

    private static void ApplyOpenCode(bool enable)
    {
        HookEdit openCode = OpenCodeEdit(enable);
        if (openCode.Changed)
        {
            WriteOpenCodePlugin(openCode.Text);
        }
    }

    /// <summary>What adding or removing opencode's plugin comes to, given what is in the plugins folder now.</summary>
    private static HookEdit OpenCodeEdit(bool enable)
    {
        string? existing = Read(OpenCodePluginPath);
        if (!enable)
        {
            return OpenCodePlugin.Remove(existing);
        }

        string? shipped = Read(OpenCodePluginSource);
        return shipped is null
            ? new HookEdit(existing ?? "", Changed: false, "Notch's opencode plugin is missing from this copy of Notch, so opencode was left alone. Reinstall Notch.")
            : OpenCodePlugin.Add(existing, shipped);
    }

    /// <summary>Writes the plugin, or deletes it when <paramref name="text"/> is empty. A plugin Notch wrote is always replaced as a whole.</summary>
    private static void WriteOpenCodePlugin(string text)
    {
        string path = OpenCodePluginPath;
        if (text.Length == 0)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".notch-tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
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
                ApplyClaude(enable: false);
                ApplyClaude(enable: true);
            }

            string? codex = Read(CodexConfigPath);
            if (AgentHookEditor.CodexHasHook(codex) && codex?.Contains(HookExecutable, StringComparison.OrdinalIgnoreCase) != true)
            {
                ApplyCodex(enable: false);
                ApplyCodex(enable: true);
            }

            // A newer Notch ships a newer plugin; one Notch wrote earlier is brought up to date. Only an
            // existing one: a plugin the user never agreed to is not added here.
            if (OpenCodePluginInstalled())
            {
                ApplyOpenCode(enable: true);
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
