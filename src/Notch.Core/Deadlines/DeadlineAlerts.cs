namespace Notch.Core.Deadlines;

/// <summary>A deadline that just passed one of its milestones.</summary>
public sealed record DeadlineAlert(Deadline Deadline, DeadlineMilestone Milestone);

/// <summary>
/// Notices when deadlines pass their milestones (a day, an hour, a quarter of an hour, due). Call <see cref="Check"/>
/// regularly; a deadline is only announced for milestones passed while it was being watched, so starting Notch, or
/// adding a deadline already inside an hour, does not set off alerts for moments that went by unseen.
/// </summary>
public sealed class DeadlineAlerts
{
    private Dictionary<string, TimeSpan> _lastLeft = [];

    public IReadOnlyList<DeadlineAlert> Check(IEnumerable<Deadline> deadlines, DateTimeOffset now, Func<Deadline, bool>? quiet = null)
    {
        var alerts = new List<DeadlineAlert>();
        var seen = new Dictionary<string, TimeSpan>();
        foreach (Deadline deadline in deadlines)
        {
            TimeSpan left = deadline.Due - now;
            seen[deadline.Key] = left;
            if (_lastLeft.TryGetValue(deadline.Key, out TimeSpan before)
                && DeadlineTiming.Crossed(before, left) is { } milestone
                && quiet?.Invoke(deadline) != true)
            {
                alerts.Add(new DeadlineAlert(deadline, milestone));
            }
        }

        _lastLeft = seen;
        return alerts;
    }
}
