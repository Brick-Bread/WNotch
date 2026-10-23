namespace Notch.Core.Activities;

/// <summary>How a clock that counts up from a moment reads: "4:05", or "1:23:45" from an hour on.</summary>
public static class ElapsedClock
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }

    public static string Since(DateTimeOffset from, DateTimeOffset? now = null) => Format((now ?? DateTimeOffset.Now) - from);
}
