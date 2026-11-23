using System.Globalization;
using System.Text.RegularExpressions;

namespace Notch.Core.Deadlines;

/// <summary>
/// Reads when something is due the way people type it: "in 3d", "2h30m", "tomorrow 17:00", "fri 5pm",
/// "17:00", "2026-11-30", "2026-11-30 17:00", "30 Nov 9:30". A day without a time means the end of that day.
/// </summary>
public static partial class DueParser
{
    /// <summary>Further ahead than this is refused, as a typo rather than a plan.</summary>
    public static readonly TimeSpan Furthest = TimeSpan.FromDays(5 * 366);

    /// <summary>A deadline for a whole day falls due a minute before midnight.</summary>
    public static readonly TimeSpan EndOfDay = new(23, 59, 0);

    public static bool TryParse(string? text, DateTimeOffset now, out DateTimeOffset due)
    {
        due = default;
        text = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (text.Length == 0 || text.Length > 64)
        {
            return false;
        }

        if (TryRelative(text, now, out due) || TryNamedDay(text, now, out due) || TryAbsolute(text, now, out due))
        {
            return due - now <= Furthest && now - due <= Furthest;
        }

        return false;
    }

    /// <summary>"in 3d", "3d 4h", "2w", "90m", "1h30m", "in 45 min". A bare number is not read: it could mean anything.</summary>
    private static bool TryRelative(string text, DateTimeOffset now, out DateTimeOffset due)
    {
        due = default;
        if (text.StartsWith("in ", StringComparison.Ordinal))
        {
            text = text[3..];
        }

        Match match = RelativePattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        double minutes = 0;
        CaptureCollection numbers = match.Groups["number"].Captures;
        CaptureCollection units = match.Groups["unit"].Captures;
        for (int i = 0; i < numbers.Count; i++)
        {
            double value = double.Parse(numbers[i].Value, CultureInfo.InvariantCulture);
            minutes += value * units[i].Value[0] switch
            {
                'w' => 7 * 24 * 60,
                'd' => 24 * 60,
                'h' => 60,
                _ => 1,
            };
        }

        if (minutes <= 0 || minutes > Furthest.TotalMinutes)
        {
            return false;
        }

        due = now + TimeSpan.FromMinutes(Math.Round(minutes));
        return true;
    }

    /// <summary>"today", "tonight", "tomorrow", a weekday ("fri", "friday", "next fri"), or a bare time, each with an optional time after it.</summary>
    private static bool TryNamedDay(string text, DateTimeOffset now, out DateTimeOffset due)
    {
        due = default;
        Match match = NamedDayPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        string day = match.Groups["day"].Value;
        TimeSpan? time = null;
        if (match.Groups["time"].Success)
        {
            if (!TryTime(match.Groups["time"].Value, out TimeSpan parsed))
            {
                return false;
            }

            time = parsed;
        }

        DateTime date;
        switch (day)
        {
            case "":
                // A time alone: today when it is still ahead, else tomorrow.
                if (time is not { } alone)
                {
                    return false;
                }

                date = now.Date + alone > now.DateTime ? now.Date : now.Date.AddDays(1);
                break;
            case "today":
                date = now.Date;
                break;
            case "tonight":
                date = now.Date;
                time ??= new TimeSpan(20, 0, 0);
                break;
            case "tomorrow" or "tmrw" or "tmr":
                date = now.Date.AddDays(1);
                break;
            default:
                if (!TryWeekday(day, out DayOfWeek weekday))
                {
                    return false;
                }

                // The coming one. "fri" on a Friday is today while the time (or the day) is still ahead;
                // "next fri" always skips today.
                int ahead = ((int)weekday - (int)now.DayOfWeek + 7) % 7;
                if (ahead == 0 && (match.Groups["next"].Success || now.Date + (time ?? EndOfDay) <= now.DateTime))
                {
                    ahead = 7;
                }

                date = now.Date.AddDays(ahead);
                break;
        }

        due = At(date, time ?? EndOfDay);
        return true;
    }

    /// <summary>Dates as ISO ("2026-11-30", "2026-11-30t17:00", with an offset or not) or in the user's own way of writing them.</summary>
    private static bool TryAbsolute(string text, DateTimeOffset now, out DateTimeOffset due)
    {
        due = default;
        const DateTimeStyles Styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;

        // With an explicit offset or Z, take it as given.
        if (OffsetPattern().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out due))
        {
            return true;
        }

        foreach (CultureInfo culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (!DateTime.TryParse(text, culture, Styles, out DateTime parsed))
            {
                continue;
            }

            bool hasTime = text.Contains(':') || AmPmPattern().IsMatch(text);
            DateTime date = parsed.Date;

            // "30 nov" without a year, already past: next year's.
            if (!YearPattern().IsMatch(text) && date < now.Date.AddDays(-1))
            {
                date = date.AddYears(1);
            }

            due = At(date, hasTime ? parsed.TimeOfDay : EndOfDay);
            return true;
        }

        return false;
    }

    private static bool TryTime(string text, out TimeSpan time)
    {
        time = default;
        Match match = TimePattern().Match(text.Replace(" ", ""));
        if (!match.Success)
        {
            return false;
        }

        int hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        string half = match.Groups["ampm"].Value;
        if (half.Length > 0)
        {
            if (hour is < 1 or > 12)
            {
                return false;
            }

            hour = hour % 12 + (half[0] == 'p' ? 12 : 0);
        }

        if (hour > 23 || minute > 59)
        {
            return false;
        }

        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    private static bool TryWeekday(string text, out DayOfWeek day)
    {
        foreach (DayOfWeek candidate in Enum.GetValues<DayOfWeek>())
        {
            string name = candidate.ToString().ToLowerInvariant();
            if (text.Length >= 3 && name.StartsWith(text, StringComparison.Ordinal))
            {
                day = candidate;
                return true;
            }
        }

        day = default;
        return false;
    }

    /// <summary>A wall-clock moment on this PC, with the offset that applies on that day.</summary>
    private static DateTimeOffset At(DateTime date, TimeSpan time)
    {
        DateTime local = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [GeneratedRegex(@"^(?:\s*(?<number>\d+(?:\.\d+)?)\s*(?<unit>weeks?|wks?|w|days?|d|hours?|hrs?|h|minutes?|mins?|m))+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePattern();

    [GeneratedRegex(@"^(?:(?<next>next )?(?<day>today|tonight|tomorrow|tmrw|tmr|mon\w*|tue\w*|wed\w*|thu\w*|fri\w*|sat\w*|sun\w*))?(?:(?:^|\s+)(?:at\s+)?(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm)?))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedDayPattern();

    [GeneratedRegex(@"^(?<h>\d{1,2})(?::(?<m>\d{2}))?(?<ampm>am|pm)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"(?:z|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();

    [GeneratedRegex(@"\d\s*(?:am|pm)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AmPmPattern();

    [GeneratedRegex(@"\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();
}
