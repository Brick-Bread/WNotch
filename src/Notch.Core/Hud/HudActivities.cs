using Notch.Core.Activities;

namespace Notch.Core.Hud;

/// <summary>Builds the transient activities shown for system events.</summary>
public static class HudActivities
{
    public const string VolumeId = "hud.volume";
    public const string BrightnessId = "hud.brightness";
    public const string PowerId = "hud.power";
    public const string BluetoothId = "hud.bluetooth";
    public const string AudioOutputId = "hud.audio-output";
    public const string CapsLockId = "hud.caps-lock";

    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(3.5);

    public static Activity Volume(double level, bool muted)
    {
        level = Math.Clamp(level, 0, 1);
        bool silent = muted || level <= 0;

        return new Activity
        {
            Id = VolumeId,
            Tier = ActivityTier.Transient,
            Title = muted ? "Muted" : "Volume",
            Progress = silent ? 0 : level,
            Glow = new Glow(GlowColor.White, GlowPattern.Flash, silent ? 0.35 : 0.4 + (0.6 * level)),
            Glyph = silent ? "" : level switch
            {
                < 0.34 => "",
                < 0.67 => "",
                _ => "",
            },
        };
    }

    public static Activity Brightness(double level) => new()
    {
        Id = BrightnessId,
        Tier = ActivityTier.Transient,
        Title = "Brightness",
        Progress = Math.Clamp(level, 0, 1),
        Glow = new Glow(GlowColor.Yellow, GlowPattern.Flash, 0.4 + (0.6 * Math.Clamp(level, 0, 1))),
        Glyph = "",
    };

    public static Activity Power(bool pluggedIn, int percent) => new()
    {
        Id = PowerId,
        Tier = ActivityTier.Transient,
        Title = pluggedIn ? "Charging" : "On battery",
        Glow = pluggedIn ? new Glow(GlowColor.Green, GlowPattern.Flash) : new Glow(GlowColor.White, GlowPattern.Flash, 0.6),
        Detail = $"{percent}%",
        Glyph = pluggedIn ? "" : "",
        Lifetime = NoticeLifetime,
    };

    public static Activity LowBattery(int percent) => new()
    {
        Id = PowerId,
        Tier = ActivityTier.Transient,
        Title = "Low battery",
        Glow = new Glow(GlowColor.Red, GlowPattern.Pulse),
        Detail = $"{percent}%",
        Glyph = "",
        Lifetime = TimeSpan.FromSeconds(6),
    };

    public static Activity Bluetooth(string deviceName, bool connected) => new()
    {
        Id = BluetoothId,
        Tier = ActivityTier.Transient,
        Title = deviceName,
        Detail = connected ? "Connected" : "Disconnected",
        Glow = new Glow(GlowColor.Blue, GlowPattern.Flash, connected ? 1 : 0.5),
        Glyph = "",
        Lifetime = NoticeLifetime,
    };

    public static Activity CapsLock(bool on) => new()
    {
        Id = CapsLockId,
        Tier = ActivityTier.Transient,
        Title = "Caps Lock",
        Detail = on ? "On" : "Off",
        Glow = new Glow(GlowColor.White, GlowPattern.Flash, on ? 0.8 : 0.4),
        Glyph = "",
        Lifetime = TimeSpan.FromSeconds(2),
    };

    public static Activity AudioOutput(string deviceName) => new()
    {
        Id = AudioOutputId,
        Tier = ActivityTier.Transient,
        Title = deviceName,
        Detail = "Output",
        Glow = new Glow(GlowColor.White, GlowPattern.Flash, 0.7),
        Glyph = "",
        Lifetime = NoticeLifetime,
    };
}
