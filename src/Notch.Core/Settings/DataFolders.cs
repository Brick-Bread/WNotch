namespace Notch.Core.Settings;

/// <summary>
/// Where Notch keeps its files: settings, plugins and their data, logs. A debug build keeps its own, under
/// "Notch-Debug", so running one beside an installed copy never has the two overwrite each other's settings;
/// <c>--shared-data</c> makes a debug run use the installed copy's folders instead.
/// </summary>
public static class DataFolders
{
    /// <summary>Makes a debug build use the installed copy's folders for this run.</summary>
    public const string SharedFlag = "--shared-data";

    private const string ReleaseName = "Notch";

#if DEBUG
    private const string DebugName = "Notch-Debug";

    /// <summary>The folders' name: "Notch", or "Notch-Debug" for a debug build.</summary>
    public static string Name { get; } =
        Environment.GetCommandLineArgs().Contains(SharedFlag, StringComparer.OrdinalIgnoreCase) ? ReleaseName : DebugName;
#else
    /// <summary>The folders' name: "Notch", or "Notch-Debug" for a debug build.</summary>
    public static string Name => ReleaseName;
#endif

    /// <summary>Settings, plugins and what they keep: <c>%AppData%\Notch</c>.</summary>
    public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>Logs, caches and downloads: <c>%LocalAppData%\Notch</c>.</summary>
    public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>
    /// A debug build's first run starts from a copy of the installed copy's settings, so it looks the same. What reaches
    /// outside Notch's own folders is left to the installed copy: the copy has the webhook off (it would want the same
    /// port) and leaves the coding agents' hook files alone (they would be pointed at the debug build).
    /// </summary>
    /// <returns>True when a copy was made.</returns>
    public static bool SeedSettings(string from, string to)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase) || File.Exists(to) || !File.Exists(from))
        {
            return false;
        }

        AppSettings settings = new SettingsStore(from).Load();
        settings.WebhookEnabled = false;
        settings.GlobalAgentHooks = false;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        new SettingsStore(to).Save(settings);
        return true;
    }

    /// <summary>Copies the installed copy's settings into this build's folder when it has none yet; see <see cref="SeedSettings(string, string)"/>.</summary>
    public static bool SeedSettings() => SeedSettings(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ReleaseName, "settings.json"),
        Path.Combine(Roaming, "settings.json"));
}
