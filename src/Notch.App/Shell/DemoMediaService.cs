using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notch.Core.Media;

namespace Notch.App.Shell;

/// <summary>
/// A fake player for <c>--demo</c>, so the media views can be seen without anything playing.
/// It is also what the website's pictures show. The artwork is drawn here, not a real cover.
/// </summary>
internal sealed class DemoMediaService : IMediaService
{
    private const string Artist = "Linkin Park";

    /// <summary>Shorter than every track, so the progress bar never starts full.</summary>
    private static readonly TimeSpan StartPosition = TimeSpan.FromSeconds(72);

    private static readonly Track[] Tracks =
    [
        new("In the End", "Hybrid Theory", 216),
        new("Papercut", "Hybrid Theory", 184),
        new("One Step Closer", "Hybrid Theory", 155),
        new("Crawling", "Hybrid Theory", 209),
        new("Numb", "Meteora", 187),
        new("Faint", "Meteora", 162),
        new("Somewhere I Belong", "Meteora", 213),
        new("Breaking the Habit", "Meteora", 196),
        new("What I've Done", "Minutes to Midnight", 205),
        new("New Divide", "New Divide", 268),
        new("Burn It Down", "Living Things", 230),
        new("The Emptiness Machine", "From Zero", 190),
    ];

    private static readonly byte[] Artwork = DrawArtwork();

    // One track per run: every picture of a screenshot run shows the same song, and the next run may show another.
    private int _track = Random.Shared.Next(Tracks.Length);
    private MediaSnapshot _current;

    public DemoMediaService() => _current = Playing(Tracks[_track], StartPosition, isPlaying: true);

    public MediaSnapshot? Current => _current;

    public event EventHandler? Changed;

    public Task TogglePlayPauseAsync() =>
        Update(_current with { IsPlaying = !_current.IsPlaying, Position = Now(), PositionUpdatedAt = DateTimeOffset.UtcNow });

    public Task NextAsync() => Skip(1);

    public Task PreviousAsync() => Skip(-1);

    public Task SeekAsync(TimeSpan position) =>
        Update(_current with { Position = position, PositionUpdatedAt = DateTimeOffset.UtcNow });

    private TimeSpan Now() => _current.PositionAt(DateTimeOffset.UtcNow);

    private Task Skip(int step)
    {
        _track = (_track + step + Tracks.Length) % Tracks.Length;
        return Update(Playing(Tracks[_track], TimeSpan.Zero, _current.IsPlaying));
    }

    private static MediaSnapshot Playing(Track track, TimeSpan position, bool isPlaying) => new()
    {
        SourceAppId = "demo",
        Title = track.Title,
        Artist = Artist,
        Album = track.Album,
        IsPlaying = isPlaying,
        Position = position,
        PositionUpdatedAt = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromSeconds(track.Seconds),
        Thumbnail = Artwork,
        CanTogglePlayPause = true,
        CanGoNext = true,
        CanGoPrevious = true,
        CanSeek = true,
    };

    private Task Update(MediaSnapshot snapshot)
    {
        _current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static byte[] DrawArtwork()
    {
        const int size = 128;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = ((y * size) + x) * 4;
                pixels[i] = (byte)(255 - y);          // blue
                pixels[i + 1] = (byte)(60 + (x / 2)); // green
                pixels[i + 2] = (byte)(120 + x);      // red
                pixels[i + 3] = 255;
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record Track(string Title, string Album, int Seconds);
}
