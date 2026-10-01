using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Notch.Core.Media;

namespace Notch.App.Shell;

// The media card on the expanded Home tab.
public partial class NotchWindow
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private byte[]? _mediaArtBytes;

    private void InitializeMedia()
    {
        _media.Changed += OnMediaChanged;
        _mediaTimer.Tick += (_, _) => UpdateMediaTimeline();

        MediaPrevious.Click += (_, _) => _ = _media.PreviousAsync();
        MediaPlayPause.Click += (_, _) => _ = _media.TogglePlayPauseAsync();
        MediaNext.Click += (_, _) => _ = _media.NextAsync();
        MediaSeekArea.MouseLeftButtonDown += OnMediaSeek;
    }

    private void OnMediaChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateMediaCard);

    private void UpdateMediaCard()
    {
        MediaSnapshot? media = _media.Current;
        Visibility visibility = media is null ? Visibility.Collapsed : Visibility.Visible;
        if (MediaCard.Visibility != visibility)
        {
            // The Home tab is taller with the card than without it.
            MediaCard.Visibility = visibility;
            Refresh();
        }

        UpdateMediaTimer();

        if (media is null)
        {
            return;
        }

        MediaTitle.Text = string.IsNullOrWhiteSpace(media.Title) ? "Unknown title" : media.Title;
        MediaArtist.Text = media.Artist;
        MediaPlayPause.Content = media.IsPlaying ? PauseGlyph : PlayGlyph;
        MediaPlayPause.IsEnabled = media.CanTogglePlayPause;
        MediaPrevious.IsEnabled = media.CanGoPrevious;
        MediaNext.IsEnabled = media.CanGoNext;
        MediaTimeline.Visibility = media.HasTimeline ? Visibility.Visible : Visibility.Hidden;
        MediaSeekArea.Cursor = media.CanSeek ? Cursors.Hand : null;

        if (!ReferenceEquals(media.Thumbnail, _mediaArtBytes))
        {
            _mediaArtBytes = media.Thumbnail;
            MediaArt.Background = ImageLoader.ToCoverBrush(ImageLoader.Decode(media.Thumbnail, 256));
        }

        UpdateMediaTimeline();
    }

    private void UpdateMediaTimeline()
    {
        if (_media.Current is not { HasTimeline: true } media)
        {
            return;
        }

        TimeSpan position = media.PositionAt(DateTimeOffset.UtcNow);
        MediaProgress.Value = position / media.Duration;
        MediaElapsed.Text = FormatTime(position);
        MediaRemaining.Text = "-" + FormatTime(media.Duration - position);
    }

    /// <summary>The timeline only needs to tick while it is on screen and moving.</summary>
    private void UpdateMediaTimer()
    {
        bool tick = _expanded && _media.Current is { IsPlaying: true, HasTimeline: true };
        if (tick && !_mediaTimer.IsEnabled)
        {
            UpdateMediaTimeline();
            _mediaTimer.Start();
        }
        else if (!tick)
        {
            _mediaTimer.Stop();
        }
    }

    private void OnMediaSeek(object sender, MouseButtonEventArgs e)
    {
        if (_media.Current is not { CanSeek: true, HasTimeline: true } media || MediaSeekArea.ActualWidth <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(e.GetPosition(MediaSeekArea).X / MediaSeekArea.ActualWidth, 0, 1);
        _ = _media.SeekAsync(media.Duration * ratio);
        e.Handled = true;
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
