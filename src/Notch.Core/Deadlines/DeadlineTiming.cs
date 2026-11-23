using Notch.Core.Activities;

namespace Notch.Core.Deadlines;

/// <summary>How pressing a deadline is, which sets its colour.</summary>
public enum DeadlineUrgency
{
    /// <summary>Further away than the user's "near" threshold.</summary>
    Later,

    /// <summary>Within the threshold.</summary>
    Near,

    /// <summary>Within the last hour.</summary>
    Soon,

    /// <summary>Past due.</summary>
    Overdue,
}

/// <summary>The moments before a deadline when the pill speaks up.</summary>
public enum DeadlineMilestone
{
    Day,
    Hour,
    Quarter,
    Due,
}

/// <summary>How deadlines read and when they matter: the pill's chip, the list's wording, the alerts.</summary>
public static class DeadlineTiming
{
    public static readonly TimeSpan DefaultNear = TimeSpan.FromHours(24);
    public const int MinNearHours = 1;
    public const int MaxNearHours = 24 * 7;

    /// <summary>The milestones from the furthest to the last, with how long before the due time each falls.</summary>
    public static readonly IReadOnlyList<(DeadlineMilestone Milestone, TimeSpan Before)> Milestones =
    [
        (DeadlineMilestone.Day, TimeSpan.FromDays(1)),
        (DeadlineMilestone.Hour, TimeSpan.FromHours(1)),
        (DeadlineMilestone.Quarter, TimeSpan.FromMinutes(15)),
        (DeadlineMilestone.Due, TimeSpan.Zero),
    ];

    /// <summary>The threshold from the settings, in hours, kept to what makes sense.</summary>
    public static TimeSpan NearFromHours(int hours) =>
        hours is >= MinNearHours and <= MaxNearHours ? TimeSpan.FromHours(hours) : DefaultNear;

    public static DeadlineUrgency Urgency(TimeSpan left, TimeSpan near) => left switch
    {
        _ when left <= TimeSpan.Zero => DeadlineUrgency.Overdue,
        _ when left <= TimeSpan.FromHours(1) => DeadlineUrgency.Soon,
        _ when left <= near => DeadlineUrgency.Near,
        _ => DeadlineUrgency.Later,
    };

    /// <summary>Calm for later, amber when near, orange in the last hour, red once overdue.</summary>
    public static GlowColor Color(DeadlineUrgency urgency) => urgency switch
    {
        DeadlineUrgency.Overdue => GlowColor.Red,
        DeadlineUrgency.Soon => GlowColor.Orange,
        DeadlineUrgency.Near => GlowColor.Amber,
        _ => GlowColor.Blue,
    };

    /// <summary>
    /// The pill's chip, a few characters wide: "3d", "5h", "42m", "now", or for an overdue one how late, "-2h".
    /// Rounds down, so a chip never promises more time than there is.
    /// </summary>
    public static string Chip(TimeSpan left)
    {
        if (left > TimeSpan.Zero && left < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        bool late = left <= TimeSpan.Zero;
        TimeSpan span = late ? -left : left;
        if (late && span < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        string amount = span.TotalDays >= 1 ? $"{(int)span.TotalDays}d"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h"
            : $"{(int)span.TotalMinutes}m";
        return late ? "-" + amount : amount;
    }

    /// <summary>A sentence part for the list and the pill: "in 2d 4h", "in 5h 20m", "in 42 min", "due now", "3h overdue".</summary>
    public static string Describe(TimeSpan left)
    {
        bool late = left <= TimeSpan.Zero;
        TimeSpan span = late ? -left : left;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "due now";
        }

        string amount = span.TotalDays >= 1 ? Pair((int)span.TotalDays, "d", span.Hours, "h")
            : span.TotalHours >= 1 ? Pair((int)span.TotalHours, "h", span.Minutes, "m")
            : $"{(int)span.TotalMinutes} min";
        return late ? amount + " overdue" : "in " + amount;

        static string Pair(int big, string bigUnit, int small, string smallUnit) =>
            small > 0 ? $"{big}{bigUnit} {small}{smallUnit}" : $"{big}{bigUnit}";
    }

    /// <summary>When it is due, the way a list reads it: "17:00", "Tomorrow 09:00", "Fri 17:00", "28 Nov 17:00".</summary>
    public static string When(DateTimeOffset due, DateTimeOffset now)
    {
        DateTimeOffset local = due.ToOffset(now.Offset);
        string time = local.ToString("t");
        int days = (local.Date - now.Date).Days;
        return days switch
        {
            0 => "Today " + time,
            1 => "Tomorrow " + time,
            -1 => "Yesterday " + time,
            > 1 and < 7 => $"{local:ddd} {time}",
            _ => $"{local:d MMM} {time}",
        };
    }

    /// <summary>The milestone passed between two looks at a deadline, the latest one when several were; null for none.</summary>
    public static DeadlineMilestone? Crossed(TimeSpan leftBefore, TimeSpan leftNow)
    {
        DeadlineMilestone? crossed = null;
        foreach ((DeadlineMilestone milestone, TimeSpan before) in Milestones)
        {
            if (leftBefore > before && leftNow <= before)
            {
                crossed = milestone;
            }
        }

        return crossed;
    }

    /// <summary>What an alert says about a milestone: "Due in 1 hour", "Due now".</summary>
    public static string Announce(DeadlineMilestone milestone) => milestone switch
    {
        DeadlineMilestone.Day => "Due in 1 day",
        DeadlineMilestone.Hour => "Due in 1 hour",
        DeadlineMilestone.Quarter => "Due in 15 minutes",
        _ => "Due now",
    };
}
