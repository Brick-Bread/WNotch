using Notch.Core.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Notch.Platform.Media;

/// <summary>
/// Reads and controls whatever the system considers the current media session (the same
/// one the Windows media flyout shows), regardless of which app owns it.
/// </summary>
public sealed class GsmtcMediaService : IMediaService, IDisposable
{
    private const ulong MaxThumbnailBytes = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private SessionManager? _manager;
    private Session? _session;
    private TrackInfo? _track;
    private volatile MediaSnapshot? _current;

    public MediaSnapshot? Current => _current;

    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        _manager = await SessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await AttachAsync(_manager.GetCurrentSession());
    }

    public Task TogglePlayPauseAsync() => SendAsync(async s => await s.TryTogglePlayPauseAsync());

    public Task NextAsync() => SendAsync(async s => await s.TrySkipNextAsync());

    public Task PreviousAsync() => SendAsync(async s => await s.TrySkipPreviousAsync());

    public Task SeekAsync(TimeSpan position) =>
        SendAsync(async s => await s.TryChangePlaybackPositionAsync(position.Ticks));

    public void Dispose()
    {
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        Detach();
        _refreshGate.Dispose();
    }

    private async Task SendAsync(Func<Session, Task> command)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            await command(session);
        }
        catch (Exception)
        {
            // The owning app can exit between the click and the call.
        }
    }

    private async void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            await AttachAsync(sender.GetCurrentSession());
        }
        catch (Exception)
        {
        }
    }

    private async Task AttachAsync(Session? session)
    {
        Detach();
        _session = session;

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        await RefreshAsync(reloadTrack: true);
    }

    private void Detach()
    {
        if (_session is { } old)
        {
            old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            old.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = null;
        _track = null;
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshAsync(reloadTrack: true);

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        _ = RefreshAsync(reloadTrack: false);

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args) =>
        _ = RefreshAsync(reloadTrack: false);

    private async Task RefreshAsync(bool reloadTrack)
    {
        try
        {
            await _refreshGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            _current = _session is { } session ? await ReadAsync(session, reloadTrack) : null;
        }
        catch (Exception)
        {
            // Sessions disappear mid-read when their app closes.
            _current = null;
        }
        finally
        {
            _refreshGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<MediaSnapshot> ReadAsync(Session session, bool reloadTrack)
    {
        // Track info is the slow part (it carries the artwork), so timeline and playback
        // events reuse the last copy.
        TrackInfo? track = reloadTrack ? null : _track;
        if (track is null)
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties properties = await session.TryGetMediaPropertiesAsync();
            _track = track = new TrackInfo(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                await ReadThumbnailAsync(properties.Thumbnail));
        }

        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
        GlobalSystemMediaTransportControlsSessionPlaybackControls controls = playback.Controls;
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

        // Sources without a timeline leave LastUpdatedTime at its zero value.
        DateTimeOffset updatedAt = timeline.LastUpdatedTime.Year < 2000 ? DateTimeOffset.UtcNow : timeline.LastUpdatedTime;

        return new MediaSnapshot
        {
            SourceAppId = session.SourceAppUserModelId ?? "",
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            Thumbnail = track.Thumbnail,
            IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Position = timeline.Position - timeline.StartTime,
            PositionUpdatedAt = updatedAt,
            Duration = timeline.EndTime - timeline.StartTime,
            PlaybackRate = playback.PlaybackRate ?? 1,
            CanTogglePlayPause = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
            CanGoNext = controls.IsNextEnabled,
            CanGoPrevious = controls.IsPreviousEnabled,
            CanSeek = controls.IsPlaybackPositionEnabled,
        };
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            if (stream.Size is 0 or > MaxThumbnailBytes)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            byte[] bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record TrackInfo(string Title, string Artist, string Album, byte[]? Thumbnail);
}
