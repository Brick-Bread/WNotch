using System.Text.Json;

namespace Notch.Core.Plugins;

/// <summary>A newer release of an installed plugin.</summary>
/// <param name="InstalledVersion">What is installed now, for display.</param>
/// <param name="LatestTag">The release tag that would be installed, e.g. "v1.1.0".</param>
public sealed record PluginUpdate(string PluginId, string Name, string? InstalledVersion, string LatestTag, PluginSource Source);

/// <summary>
/// Remembers where a plugin was installed from and which release, in a hidden file inside its
/// folder, so Notch can later ask the same repository whether there is something newer.
/// </summary>
public static class PluginOrigin
{
    public const string FileName = ".notch-source";

    public sealed record Record(string Repository, string Tag);

    public static void Write(string pluginDirectory, PluginSource source, string tag) =>
        File.WriteAllText(
            Path.Combine(pluginDirectory, FileName),
            JsonSerializer.Serialize(new Record(source.ToString(), tag)));

    /// <summary>What was recorded at install time, or null for a plugin copied in by hand or an unreadable record.</summary>
    public static Record? Read(string pluginDirectory)
    {
        try
        {
            string path = Path.Combine(pluginDirectory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The repository to ask about updates: the one it was installed from, else the one its manifest names.</summary>
    public static PluginSource? SourceOf(string pluginDirectory, PluginManifest? manifest) =>
        (Read(pluginDirectory)?.Repository is { } recorded ? PluginSource.Parse(recorded) : null)
        ?? (manifest?.Repository is { } declared ? PluginSource.Parse(declared) : null);

    /// <summary>Reads "v1.2.3" or "1.2" as a version; null when it is neither.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim().TrimStart('v', 'V');

        // "1.2.0-beta" and "1.2.0+build" compare by their numeric part.
        int end = trimmed.IndexOfAny(['-', '+']);
        return Version.TryParse(end < 0 ? trimmed : trimmed[..end], out Version? version) ? version : null;
    }

    /// <summary>
    /// Whether a release is newer than what is installed. The version recorded at install time
    /// (the tag) wins over the manifest's, which authors may forget to bump.
    /// </summary>
    public static bool IsNewer(string latestTag, string? installedTag, string? manifestVersion)
    {
        Version? latest = ParseVersion(latestTag);
        Version? installed = ParseVersion(installedTag) ?? ParseVersion(manifestVersion);
        return latest is not null && installed is not null && latest > installed;
    }
}
