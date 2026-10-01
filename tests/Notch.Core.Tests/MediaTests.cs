using Notch.Core.Activities;
using Notch.Core.Media;

namespace Notch.Core.Tests;

public class MediaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MediaSnapshot Track(bool playing) => new()
    {
        SourceAppId = "player",
        Title = "Song",
        Artist = "Artist",
        IsPlaying = playing,
        Position = TimeSpan.FromSeconds(30),
        PositionUpdatedAt = T0,
        Duration = TimeSpan.FromSeconds(100),
    };

    [Fact]
    public void Position_advances_while_playing() =>
        Assert.Equal(TimeSpan.FromSeconds(40), Track(playing: true).PositionAt(T0.AddSeconds(10)));

    [Fact]
    public void Position_holds_while_paused() =>
        Assert.Equal(TimeSpan.FromSeconds(30), Track(playing: false).PositionAt(T0.AddSeconds(10)));

    [Fact]
    public void Position_stops_at_the_duration() =>
        Assert.Equal(TimeSpan.FromSeconds(100), Track(playing: true).PositionAt(T0.AddMinutes(10)));

    [Fact]
    public void Position_keeps_running_without_a_timeline()
    {
        MediaSnapshot live = Track(playing: true) with { Duration = TimeSpan.Zero };

        Assert.Equal(TimeSpan.FromSeconds(630), live.PositionAt(T0.AddMinutes(10)));
    }

    [Fact]
    public void Publisher_shows_media_only_while_playing()
    {
        using var activities = new ActivityManager();
        var media = new FakeMediaService { Current = Track(playing: true) };
        using var publisher = new MediaActivityPublisher(media, activities);

        Activity activity = Assert.Single(activities.Snapshot());
        Assert.Equal("Song", activity.Title);
        Assert.Equal("Artist", activity.Detail);

        media.Set(Track(playing: false));
        Assert.Empty(activities.Snapshot());

        media.Set(Track(playing: true));
        Assert.Single(activities.Snapshot());

        media.Set(null);
        Assert.Empty(activities.Snapshot());
    }

    private sealed class FakeMediaService : IMediaService
    {
        public MediaSnapshot? Current { get; set; }

        public event EventHandler? Changed;

        public void Set(MediaSnapshot? snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task TogglePlayPauseAsync() => Task.CompletedTask;

        public Task NextAsync() => Task.CompletedTask;

        public Task PreviousAsync() => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
    }
}
