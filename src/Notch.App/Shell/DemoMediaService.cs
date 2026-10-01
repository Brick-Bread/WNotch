using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notch.Core.Media;

namespace Notch.App.Shell;

/// <summary>A fake player for <c>--demo</c>, so the media views can be seen without anything playing.</summary>
internal sealed class DemoMediaService : IMediaService
{
    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(245);

    private MediaSnapshot _current = new()
    {
        SourceAppId = "demo",
        Title = "Weightless",
        Artist = "Marconi Union",
        Album = "Demo",
        IsPlaying = true,
        Position = TimeSpan.FromSeconds(72),
        PositionUpdatedAt = DateTimeOffset.UtcNow,
        Duration = TrackLength,
        Thumbnail = DrawArtwork(),
        CanTogglePlayPause = true,
        CanGoNext = true,
        CanSeek = true,
    };

    public MediaSnapshot? Current => _current;

    public event EventHandler? Changed;

    public Task TogglePlayPauseAsync() =>
        Update(_current with { IsPlaying = !_current.IsPlaying, Position = Now(), PositionUpdatedAt = DateTimeOffset.UtcNow });

    public Task NextAsync() => SeekAsync(TimeSpan.Zero);

    public Task PreviousAsync() => SeekAsync(TimeSpan.Zero);

    public Task SeekAsync(TimeSpan position) =>
        Update(_current with { Position = position, PositionUpdatedAt = DateTimeOffset.UtcNow });

    private TimeSpan Now() => _current.PositionAt(DateTimeOffset.UtcNow);

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
}
