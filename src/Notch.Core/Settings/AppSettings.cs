using System.Text.Json;
using System.Text.Json.Serialization;
using Notch.Core.Activities;
using Notch.Core.Hud;
using Notch.Core.Media;

namespace Notch.Core.Settings;

public sealed class AppSettings
{
    private const int MaxRecentFolders = 8;

    /// <summary>Which display hosts the notch, as an index into the system's display list. Null means the primary display.</summary>
    public int? DisplayIndex { get; set; }

    /// <summary>Open the notch by hovering over it. When off, it opens on click only.</summary>
    public bool ExpandOnHover { get; set; } = true;

    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Dark or light colours, or whichever Windows is set to for apps.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotchTheme>))]
    public NotchTheme Theme { get; set; } = NotchTheme.Dark;

    /// <summary>
    /// Name of the <see cref="GlowColor"/> preset that tints buttons, bars and the selected tab.
    /// Anything that is not a preset, such as <see cref="NoAccent"/>, leaves them uncoloured.
    /// </summary>
    public string AccentColor { get; set; } = nameof(GlowColor.Blue);

    public const string NoAccent = "None";

    /// <summary>Light up the pill in a colour and rhythm that matches what is happening.</summary>
    public bool GlowEffects { get; set; } = true;

    /// <summary>Glow brightness in percent of the standard, <see cref="GlowOutput.MinPercent"/> to <see cref="GlowOutput.MaxPercent"/>.</summary>
    public int GlowIntensity { get; set; } = GlowOutput.DefaultPercent;

    /// <summary>Download and install new releases without asking.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>The release tag the updater last tried to install, so a release that fails to install is not retried in a loop.</summary>
    public string? LastUpdateAttemptTag { get; set; }

    public DateTimeOffset? LastUpdateAttemptAt { get; set; }

    public bool ShowMedia { get; set; } = true;

    public bool ShowVolume { get; set; } = true;

    public bool ShowBrightness { get; set; } = true;

    public bool ShowPower { get; set; } = true;

    public bool ShowBluetooth { get; set; } = true;

    public int PomodoroFocusMinutes { get; set; } = 25;

    public int PomodoroShortBreakMinutes { get; set; } = 5;

    public int PomodoroLongBreakMinutes { get; set; } = 15;

    /// <summary>Pomodoro lengths from the settings, with anything out of range replaced by the classic value.</summary>
    public Widgets.PomodoroDurations PomodoroDurations()
    {
        Widgets.PomodoroDurations classic = Widgets.PomodoroDurations.Classic;
        return new Widgets.PomodoroDurations(
            Minutes(PomodoroFocusMinutes, classic.Focus),
            Minutes(PomodoroShortBreakMinutes, classic.ShortBreak),
            Minutes(PomodoroLongBreakMinutes, classic.LongBreak));

        static TimeSpan Minutes(int value, TimeSpan fallback) =>
            value is >= 1 and <= 600 ? TimeSpan.FromMinutes(value) : fallback;
    }

    /// <summary>iCalendar (.ics) links shown on the Home tab's calendar card.</summary>
    public List<string> CalendarFeeds { get; set; } = [];

    /// <summary>Ids of the plugins the user has switched on. Installed plugins do not run until listed here.</summary>
    public List<string> EnabledPlugins { get; set; } = [];

    /// <summary>Folders terminal sessions were started in, most recent first.</summary>
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>The activity ids the user has switched off.</summary>
    public IEnumerable<string> SuppressedActivityIds()
    {
        if (!ShowMedia)
        {
            yield return MediaActivityPublisher.ActivityId;
        }

        if (!ShowVolume)
        {
            yield return HudActivities.VolumeId;
            yield return HudActivities.AudioOutputId;
        }

        if (!ShowBrightness)
        {
            yield return HudActivities.BrightnessId;
        }

        if (!ShowPower)
        {
            yield return HudActivities.PowerId;
        }

        if (!ShowBluetooth)
        {
            yield return HudActivities.BluetoothId;
        }
    }

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
