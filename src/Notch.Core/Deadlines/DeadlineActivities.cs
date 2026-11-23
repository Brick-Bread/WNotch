using Notch.Core.Activities;

namespace Notch.Core.Deadlines;

/// <summary>What the pill shows for deadlines: the next one at a glance, the one that is near, and the alerts.</summary>
public static class DeadlineActivities
{
    /// <summary>The next deadline in the pill, as a glance item while it is far off and an attention one when near.</summary>
    public const string PillId = "deadline";

    /// <summary>The short notice at a milestone.</summary>
    public const string AlertId = "deadline.alert";

    /// <summary>A flag, in Segoe Fluent Icons.</summary>
    public const string Glyph = "";

    public static readonly TimeSpan AlertLifetime = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The pill's activity for the next deadline, or null when there is none. Further away than
    /// <paramref name="near"/> it is a glance item ("3d · Report"), so it shows while nothing else does and as a chip
    /// beside what does; within it, it takes the pill: its title, how long is left, and a glow that grows more urgent.
    /// </summary>
    public static Activity? ForPill(Deadline? next, DateTimeOffset now, TimeSpan near)
    {
        if (next is null)
        {
            return null;
        }

        TimeSpan left = next.Due - now;
        DeadlineUrgency urgency = DeadlineTiming.Urgency(left, near);
        GlowColor color = DeadlineTiming.Color(urgency);
        if (urgency == DeadlineUrgency.Later)
        {
            return new Activity
            {
                Id = PillId,
                Tier = ActivityTier.Glance,
                Title = $"{DeadlineTiming.Chip(left)} · {next.Title}",
                Glyph = Glyph,
                Glow = new Glow(color, GlowPattern.Steady),
                Order = -10,
            };
        }

        return new Activity
        {
            Id = PillId,
            Tier = ActivityTier.Attention,
            Title = next.Title,
            Detail = DeadlineTiming.Describe(left),
            Glyph = Glyph,
            Glow = urgency switch
            {
                DeadlineUrgency.Near => new Glow(color, GlowPattern.Breathe, 0.45),
                _ => new Glow(color, GlowPattern.Pulse),
            },
        };
    }

    /// <summary>The notice for a milestone.</summary>
    public static Activity ForAlert(DeadlineAlert alert, TimeSpan near)
    {
        TimeSpan before = DeadlineTiming.Milestones.First(m => m.Milestone == alert.Milestone).Before;
        GlowColor color = DeadlineTiming.Color(DeadlineTiming.Urgency(before, near));
        return new Activity
        {
            Id = AlertId,
            Tier = ActivityTier.Transient,
            Title = alert.Deadline.Title,
            Detail = DeadlineTiming.Announce(alert.Milestone),
            Glyph = Glyph,
            Glow = new Glow(color, GlowPattern.Flash),
            Lifetime = AlertLifetime,
        };
    }
}
