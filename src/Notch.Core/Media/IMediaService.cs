namespace Notch.Core.Media;

public interface IMediaService
{
    /// <summary>Null when no app has an active media session.</summary>
    MediaSnapshot? Current { get; }

    /// <summary>May be raised on any thread.</summary>
    event EventHandler? Changed;

    Task TogglePlayPauseAsync();

    Task NextAsync();

    Task PreviousAsync();

    Task SeekAsync(TimeSpan position);
}
