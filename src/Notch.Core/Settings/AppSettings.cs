using System.Text.Json;
using System.Text.Json.Serialization;
using Notch.Core.Activities;
using Notch.Core.Hud;
using Notch.Core.Media;

namespace Notch.Core.Settings;

public sealed class AppSettings
{
    private const int MaxRecentFolders = 8;

    /// <summary>As many preset buttons as the timer card has room for.</summary>
    public const int MaxTimerPresets = 6;

    /// <summary>Which display hosts the notch, as an index into the system's display list. Null means the primary display.</summary>
    public int? DisplayIndex { get; set; }

    /// <summary>A notch attached to the screen edge, or a floating island.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotchStyle>))]
    public NotchStyle Style { get; set; } = NotchStyle.Notch;

    /// <summary>Top of the screen, or the far left of the taskbar.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotchPosition>))]
    public NotchPosition Position { get; set; } = NotchPosition.TopCenter;

    /// <summary>Open the notch by hovering over it. When off, it opens on click only.</summary>
    public bool ExpandOnHover { get; set; } = true;

    /// <summary>Open the notch on its Shelf tab when files are dragged onto the pill. When off, files can still be dropped on the open Shelf tab.</summary>
    public bool ShelfOpensOnDrag { get; set; } = true;

    /// <summary>
    /// Keys that open and close the notch from any app, as <see cref="Shell.Hotkey.TryParse"/> reads them.
    /// Empty, or anything it cannot read, means no hotkey. The default has no Win key, whose
    /// combinations Windows keeps taking for itself, and not Ctrl+Alt, which is how AltGr types letters.
    /// </summary>
    public string OpenHotkey { get; set; } = "Alt+Shift+N";

    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Dark or light colours, or whichever Windows is set to for apps.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotchTheme>))]
    public NotchTheme Theme { get; set; } = NotchTheme.Dark;

    /// <summary>
    /// A theme offered by a plugin, as "plugin-id/theme-id", used instead of <see cref="Theme"/>
    /// while that plugin is running. Null for none. The choice is kept when the plugin is switched
    /// off and takes effect again when it is back.
    /// </summary>
    public string? PluginTheme { get; set; }

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

    /// <summary>Show a notice in the pill when a newer release exists. Nothing is installed without the user asking.</summary>
    public bool NotifyOfUpdates { get; set; } = true;

    /// <summary>The release tag the user was last told about, so each release is announced once.</summary>
    public string? LastNotifiedUpdateTag { get; set; }

    public bool ShowMedia { get; set; } = true;

    public bool ShowVolume { get; set; } = true;

    public bool ShowBrightness { get; set; } = true;

    public bool ShowPower { get; set; } = true;

    public bool ShowBluetooth { get; set; } = true;

    public bool ShowCapsLock { get; set; } = true;

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

    /// <summary>The one-click timers on the timer card, in the order shown.</summary>
    public List<Widgets.TimerPreset> TimerPresets { get; set; } =
    [
        new("", 5 * 60),
        new("", 15 * 60),
        new("", 30 * 60),
        new("", 60 * 60),
    ];

    /// <summary>The presets from the settings that can be used: sensible lengths, tidy names, no more than fit.</summary>
    public IReadOnlyList<Widgets.TimerPreset> Timers() => [.. TimerPresets
        .Where(preset => preset is { IsValid: true })
        .Take(MaxTimerPresets)
        .Select(preset =>
        {
            string name = (preset.Name ?? "").Trim();
            return preset with { Name = name.Length > Widgets.TimerPreset.MaxNameLength ? name[..Widgets.TimerPreset.MaxNameLength].TrimEnd() : name };
        })];

    /// <summary>iCalendar (.ics) links shown on the Home tab's calendar card.</summary>
    public List<string> CalendarFeeds { get; set; } = [];

    /// <summary>Ids of the plugins the user has switched on. Installed plugins do not run until listed here.</summary>
    public List<string> EnabledPlugins { get; set; } = [];

    /// <summary>
    /// The buttons that start terminal sessions, one per line as <see cref="Terminal.TerminalPresets"/> reads them,
    /// for example <c>Work Claude = CLAUDE_CONFIG_DIR=C:\work claude</c>.
    /// </summary>
    public List<string> TerminalPresets { get; set; } = [.. Terminal.TerminalProfile.DefaultPresets];

    /// <summary>Folders terminal sessions were started in, most recent first.</summary>
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Accept requests on the local webhook. Off until the user turns it on.</summary>
    public bool WebhookEnabled { get; set; }

    /// <summary>The port the webhook listens on, on this computer only.</summary>
    public int WebhookPort { get; set; } = Automation.WebhookRequest.DefaultPort;

    /// <summary>The secret every webhook request must carry. Made when the webhook is first switched on.</summary>
    public string? WebhookToken { get; set; }

    /// <summary>Keys that open the command palette, read like <see cref="OpenHotkey"/>. Empty for none.</summary>
    public string PaletteHotkey { get; set; } = "Alt+Shift+P";

    /// <summary>Show agents that were not started from Notch's terminal too (found by looking at running programs).</summary>
    public bool DetectAgents { get; set; } = true;

    /// <summary>Ids of agents (see <see cref="Agents.AgentCatalog"/>) the user does not want shown.</summary>
    public List<string> HiddenAgents { get; set; } = [];

    /// <summary>
    /// Whether Notch has put its hooks into the agents' own settings files so that agents started
    /// anywhere report what they are doing. Switched on and off from the settings window, which
    /// keeps a backup of every file it changes.
    /// </summary>
    public bool GlobalAgentHooks { get; set; }

    /// <summary>Keep a history of what was copied, in the Clipboard tab. Off until the user turns it on.</summary>
    public bool ClipboardHistory { get; set; }

    /// <summary>Show Windows notifications in the pill. Off until the user turns it on.</summary>
    public bool MirrorNotifications { get; set; }

    /// <summary>Names of the apps whose notifications are shown. Empty means all of them.</summary>
    public List<string> MirroredApps { get; set; } = [];

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

        if (!ShowCapsLock)
        {
            yield return HudActivities.CapsLockId;
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
