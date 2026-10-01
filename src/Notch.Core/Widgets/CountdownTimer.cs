namespace Notch.Core.Widgets;

public enum CountdownState
{
    Idle,
    Running,
    Paused,

    /// <summary>Reached zero and is waiting to be dismissed.</summary>
    Finished,
}

/// <summary>A pausable countdown. It keeps no timer of its own: read <see cref="Remaining"/> whenever the display refreshes.</summary>
public sealed class CountdownTimer(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _endsAt;
    private TimeSpan _remainingWhenPaused;
    private bool _running;
    private bool _paused;

    /// <summary>The length the countdown was last started with.</summary>
    public TimeSpan Duration { get; private set; }

    public CountdownState State
    {
        get
        {
            if (_paused)
            {
                return CountdownState.Paused;
            }

            if (!_running)
            {
                return CountdownState.Idle;
            }

            return _time.GetUtcNow() >= _endsAt ? CountdownState.Finished : CountdownState.Running;
        }
    }

    public TimeSpan Remaining => State switch
    {
        CountdownState.Running => _endsAt - _time.GetUtcNow(),
        CountdownState.Paused => _remainingWhenPaused,
        _ => TimeSpan.Zero,
    };

    /// <summary>0 at the start, 1 when finished.</summary>
    public double Progress => Duration > TimeSpan.Zero && State != CountdownState.Idle
        ? Math.Clamp(1 - (Remaining / Duration), 0, 1)
        : 0;

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            Reset();
            return;
        }

        Duration = duration;
        _endsAt = _time.GetUtcNow() + duration;
        _running = true;
        _paused = false;
    }

    public void Pause()
    {
        if (State == CountdownState.Running)
        {
            _remainingWhenPaused = Remaining;
            _paused = true;
        }
    }

    public void Resume()
    {
        if (State == CountdownState.Paused)
        {
            _endsAt = _time.GetUtcNow() + _remainingWhenPaused;
            _paused = false;
        }
    }

    /// <summary>Stops the countdown, or dismisses it once finished.</summary>
    public void Reset()
    {
        _running = false;
        _paused = false;
    }

    /// <summary>"4:05", or "1:02:03" from an hour up. Rounds up so a fresh five minutes reads 5:00, not 4:59.</summary>
    public static string Format(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds)));
        return rounded.TotalHours >= 1 ? rounded.ToString(@"h\:mm\:ss") : rounded.ToString(@"m\:ss");
    }
}
