using System.Text.Json;

namespace Notch.Core.Settings;

public sealed class AppSettings
{
    private const int MaxRecentFolders = 8;

    /// <summary>Folders terminal sessions were started in, most recent first.</summary>
    public List<string> RecentFolders { get; set; } = [];

    public void RememberFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > MaxRecentFolders)
        {
            RecentFolders.RemoveRange(MaxRecentFolders, RecentFolders.Count - MaxRecentFolders);
        }
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A missing or corrupt file yields defaults.</summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temporary file first so a crash cannot leave a half-written settings file.
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to persist them must not take the app down.
        }
    }
}
