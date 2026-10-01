namespace Notch.Core.Widgets;

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

/// <param name="FocusesPerLongBreak">Every this many focus sessions, the break is a long one.</param>
public sealed record PomodoroDurations(TimeSpan Focus, TimeSpan ShortBreak, TimeSpan LongBreak, int FocusesPerLongBreak = 4)
{
    public static PomodoroDurations Classic { get; } = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
}

/// <summary>Steps through focus sessions and breaks. It only tracks the sequence; a <see cref="CountdownTimer"/> does the counting.</summary>
public sealed class PomodoroCycle(PomodoroDurations durations)
{
    public PomodoroDurations Durations { get; } = durations;

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Focus;

    public int CompletedFocuses { get; private set; }

    /// <summary>Which focus session of the current set this is (1-based), e.g. 2 in "Focus 2/4".</summary>
    public int FocusNumber => (CompletedFocuses % Math.Max(1, Durations.FocusesPerLongBreak)) + 1;

    public TimeSpan CurrentDuration => Phase switch
    {
        PomodoroPhase.ShortBreak => Durations.ShortBreak,
        PomodoroPhase.LongBreak => Durations.LongBreak,
        _ => Durations.Focus,
    };

    /// <summary>"Focus 2/4", "Short break" or "Long break".</summary>
    public string Label => Phase switch
    {
        PomodoroPhase.ShortBreak => "Short break",
        PomodoroPhase.LongBreak => "Long break",
        _ => $"Focus {FocusNumber}/{Math.Max(1, Durations.FocusesPerLongBreak)}",
    };

    /// <summary>Moves on once the current phase has run out, and returns the new phase.</summary>
    public PomodoroPhase Advance()
    {
        if (Phase == PomodoroPhase.Focus)
        {
            CompletedFocuses++;
            Phase = CompletedFocuses % Math.Max(1, Durations.FocusesPerLongBreak) == 0
                ? PomodoroPhase.LongBreak
                : PomodoroPhase.ShortBreak;
        }
        else
        {
            Phase = PomodoroPhase.Focus;
        }

        return Phase;
    }
}
