namespace Notch.Core.Media;

/// <summary>What the system's current media session is playing, at one point in time.</summary>
public sealed record MediaSnapshot
{
    public required string SourceAppId { get; init; }

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    public bool IsPlaying { get; init; }

    /// <summary>Position as of <see cref="PositionUpdatedAt"/>; use <see cref="PositionAt"/> for the live value.</summary>
    public TimeSpan Position { get; init; }

    public DateTimeOffset PositionUpdatedAt { get; init; }

    /// <summary>Zero when the source does not report a timeline (live streams, some browsers).</summary>
    public TimeSpan Duration { get; init; }

    public double PlaybackRate { get; init; } = 1;

    /// <summary>Encoded image bytes (PNG or JPEG) as supplied by the source app.</summary>
    public byte[]? Thumbnail { get; init; }

    public bool CanTogglePlayPause { get; init; }

    public bool CanGoNext { get; init; }

    public bool CanGoPrevious { get; init; }

    public bool CanSeek { get; init; }

    public bool HasTimeline => Duration > TimeSpan.Zero;

    /// <summary>Sources only report position occasionally, so the live value is extrapolated.</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        TimeSpan position = Position;
        if (IsPlaying && now > PositionUpdatedAt)
        {
            position += (now - PositionUpdatedAt) * PlaybackRate;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return HasTimeline && position > Duration ? Duration : position;
    }
}
