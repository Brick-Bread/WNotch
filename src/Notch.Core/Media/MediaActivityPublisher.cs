using Notch.Core.Activities;

namespace Notch.Core.Media;

/// <summary>Keeps a "media" activity in the pill for as long as something is playing.</summary>
public sealed class MediaActivityPublisher : IDisposable
{
    public const string ActivityId = "media";

    private const string MusicGlyph = "";

    private readonly IMediaService _media;
    private readonly ActivityManager _activities;

    public MediaActivityPublisher(IMediaService media, ActivityManager activities)
    {
        _media = media;
        _activities = activities;
        _media.Changed += OnMediaChanged;
        Publish();
    }

    public void Dispose() => _media.Changed -= OnMediaChanged;

    private void OnMediaChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        if (_media.Current is not { IsPlaying: true } media)
        {
            _activities.Remove(ActivityId);
            return;
        }

        _activities.Publish(new Activity
        {
            Id = ActivityId,
            Tier = ActivityTier.Ongoing,
            Glyph = MusicGlyph,
            Image = media.Thumbnail,
            Title = string.IsNullOrWhiteSpace(media.Title) ? "Playing" : media.Title,
            Detail = media.Artist,

            // The shell swaps in a colour taken from the artwork when it has one.
            Glow = new Glow(GlowColor.Violet, GlowPattern.Audio, 0.85),
        });
    }
}
