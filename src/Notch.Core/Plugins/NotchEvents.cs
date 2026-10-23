using System.Text.Json;
using Notch.Core.Media;
using Notch.Core.Widgets;

namespace Notch.Core.Plugins;

/// <summary>
/// What Notch itself tells plugins over the <see cref="IPluginBus"/>, from the sender "notch". Topics starting
/// with "notch." are Notch's own: plugins can subscribe to them but not publish on them. API version 9.
/// </summary>
public static class NotchEvents
{
    /// <summary>The sender of Notch's own messages.</summary>
    public const string Sender = "notch";

    /// <summary>Topics with this start are Notch's own.</summary>
    public const string Prefix = "notch.";

    /// <summary>
    /// The built-in timer started, was paused, resumed or cancelled, or finished. The payload is JSON such as
    /// <c>{"event":"finished","kind":"pomodoro-focus","seconds":1500,"name":"Focus 2 of 4"}</c>; <c>event</c> is
    /// <c>started</c>, <c>paused</c>, <c>resumed</c>, <c>cancelled</c> or <c>finished</c>, and <c>kind</c> is
    /// <c>timer</c>, <c>pomodoro-focus</c>, <c>pomodoro-short-break</c> or <c>pomodoro-long-break</c>.
    /// <c>seconds</c> is the length it was started with.
    /// </summary>
    public const string Timer = "notch.timer";

    /// <summary>
    /// What is playing changed: another track, or play and pause. The payload is JSON such as
    /// <c>{"event":"changed","playing":true,"title":"...","artist":"...","album":"...","app":"Spotify.exe"}</c>, or
    /// <c>{"event":"stopped"}</c> when nothing plays any more.
    /// </summary>
    public const string Media = "notch.media";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string TimerKind(PomodoroPhase? phase) => phase switch
    {
        PomodoroPhase.Focus => "pomodoro-focus",
        PomodoroPhase.ShortBreak => "pomodoro-short-break",
        PomodoroPhase.LongBreak => "pomodoro-long-break",
        _ => "timer",
    };

    public static string TimerPayload(string @event, string kind, TimeSpan duration, string? name) =>
        JsonSerializer.Serialize(new { @event, kind, seconds = (int)Math.Round(duration.TotalSeconds), name }, Json);

    public static string MediaPayload(MediaSnapshot? media) => media is null
        ? JsonSerializer.Serialize(new { @event = "stopped" }, Json)
        : JsonSerializer.Serialize(new { @event = "changed", playing = media.IsPlaying, title = media.Title, artist = media.Artist, album = media.Album, app = media.SourceAppId }, Json);
}
