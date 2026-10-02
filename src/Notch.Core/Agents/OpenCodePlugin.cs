namespace Notch.Core.Agents;

/// <summary>
/// opencode has no hook settings like Claude Code's. It loads plugins from a folder instead, and
/// a plugin sees what the session is doing. Notch's plugin is one small file that tells Notch when
/// opencode starts working, asks for permission, or finishes. This is the pure part of putting it
/// in place and taking it out again; the app does the file work.
/// </summary>
public static class OpenCodePlugin
{
    /// <summary>The file's name inside opencode's <c>plugins</c> folder.</summary>
    public const string FileName = "notch.js";

    /// <summary>A line of the plugin that says it is Notch's, and which version of the plugin it is.</summary>
    public const string Marker = "notch-opencode-plugin";

    /// <summary>opencode's config folder: <c>OPENCODE_CONFIG_DIR</c> when set, otherwise <c>.config/opencode</c> in the home folder.</summary>
    public static string ConfigDirectory(string? configDirVariable, string homeDirectory) =>
        !string.IsNullOrWhiteSpace(configDirVariable) ? configDirVariable : Path.Combine(homeDirectory, ".config", "opencode");

    public static string PluginPath(string? configDirVariable, string homeDirectory) =>
        Path.Combine(ConfigDirectory(configDirVariable, homeDirectory), "plugins", FileName);

    /// <summary>Whether a file is Notch's plugin (as opposed to some other plugin the user named notch.js).</summary>
    public static bool IsNotchs(string? text) => text is not null && text.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Writes <paramref name="plugin"/> over Notch's own older copy, or into a place where there is none. Never over a file that is not Notch's.</summary>
    public static HookEdit Add(string? existing, string plugin)
    {
        if (existing is null)
        {
            return new HookEdit(plugin, Changed: true);
        }

        if (!IsNotchs(existing))
        {
            return new HookEdit(
                existing,
                Changed: false,
                $"opencode already has a plugin called {FileName} that is not Notch's, so Notch left opencode alone. Rename it to let Notch add its own.");
        }

        return new HookEdit(plugin, Changed: !string.Equals(Normalize(existing), Normalize(plugin), StringComparison.Ordinal));
    }

    /// <summary>
    /// Takes Notch's plugin out. The result has <see cref="HookEdit.Changed"/> set and empty
    /// <see cref="HookEdit.Text"/> when the file is Notch's and should be deleted.
    /// </summary>
    public static HookEdit Remove(string? existing) =>
        IsNotchs(existing) ? new HookEdit("", Changed: true) : new HookEdit(existing ?? "", Changed: false);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
