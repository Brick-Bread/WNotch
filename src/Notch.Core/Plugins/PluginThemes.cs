namespace Notch.Core.Plugins;

/// <summary>Which of Notch's own themes a plugin theme is built on: whatever the theme does not define comes from it.</summary>
public enum PluginThemeBase
{
    /// <summary>Black island with light text.</summary>
    Dark,

    /// <summary>Light island with dark text.</summary>
    Light,
}

/// <summary>
/// The plugin's themes. A theme is a XAML resource dictionary shipped with the plugin that can
/// restyle everything Notch draws; the user picks it in Settings. API version 6.
/// </summary>
public interface IPluginThemes
{
    /// <summary>
    /// Adds <paramref name="theme"/>, or replaces the plugin's theme with the same <see cref="PluginTheme.Id"/>.
    /// Replacing the theme that is showing reloads it, which is handy while writing one.
    /// </summary>
    /// <exception cref="ArgumentException">The id or name is blank, or the file is not a relative path to a .xaml file inside the plugin's folder.</exception>
    void Set(PluginTheme theme);

    /// <summary>Removes a theme by id. False when there was no such theme. If it was showing, Notch goes back to its own theme.</summary>
    bool Remove(string id);

    /// <summary>Removes all of the plugin's themes.</summary>
    void Clear();
}

/// <summary>A look for the whole notch, described by a XAML file. See "Themes" in docs/plugins.md for what the file can define.</summary>
public sealed record PluginTheme
{
    /// <summary>Stable per theme and unique within the plugin, without a slash, e.g. "midnight". The user's choice is remembered by it.</summary>
    public required string Id { get; init; }

    /// <summary>The name shown in settings.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Path of the theme's <c>.xaml</c> file relative to the plugin's folder, e.g. "themes/midnight.xaml".
    /// Its root element is a <c>ResourceDictionary</c>.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// The built-in theme the file is applied on top of: every brush, font and style it leaves
    /// out comes from there, and the base decides whether Notch counts as dark or light for
    /// things drawn outside XAML (images, the terminal's defaults, plugins asking
    /// <see cref="IPluginShell.IsDark"/>). Defaults to dark.
    /// </summary>
    public PluginThemeBase Base { get; init; } = PluginThemeBase.Dark;

    /// <summary>A line of text shown next to the name in settings. Optional.</summary>
    public string? Description { get; init; }
}

/// <summary>A registered theme as the shell sees it.</summary>
/// <param name="FilePath">Full path of the XAML file.</param>
public sealed record PluginThemeEntry(
    string PluginId,
    string Id,
    string Name,
    string? Description,
    PluginThemeBase Base,
    string FilePath)
{
    /// <summary>What settings stores to remember the choice: "plugin-id/theme-id".</summary>
    public string Key => KeyFor(PluginId, Id);

    public static string KeyFor(string pluginId, string themeId) => $"{pluginId}/{themeId}";
}

/// <summary>
/// Holds the themes of all plugins. Plugins write through <see cref="IPluginThemes"/>; the shell
/// reads the board when <see cref="Changed"/> fires.
/// </summary>
public sealed class PluginThemeBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginThemeEntry> _entries = [];
    private readonly List<string> _order = [];

    /// <summary>Raised on whichever thread caused the change.</summary>
    public event EventHandler? Changed;

    public void Set(PluginThemeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (_entries.TryAdd(entry.Key, entry))
            {
                _order.Add(entry.Key);
            }
            else
            {
                _entries[entry.Key] = entry;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string pluginId, string themeId)
    {
        string key = PluginThemeEntry.KeyFor(pluginId, themeId);
        lock (_gate)
        {
            if (!_entries.Remove(key))
            {
                return false;
            }

            _order.Remove(key);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RemoveAll(string pluginId)
    {
        bool removed = false;
        lock (_gate)
        {
            foreach (string key in _order.Where(k => _entries[k].PluginId == pluginId).ToList())
            {
                _entries.Remove(key);
                _order.Remove(key);
                removed = true;
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>In the order the themes were first added.</summary>
    public IReadOnlyList<PluginThemeEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _order.Select(k => _entries[k])];
        }
    }

    /// <summary>The theme saved under <paramref name="key"/> (see <see cref="PluginThemeEntry.Key"/>), or null when no running plugin offers it.</summary>
    public PluginThemeEntry? Find(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.GetValueOrDefault(key);
        }
    }
}
