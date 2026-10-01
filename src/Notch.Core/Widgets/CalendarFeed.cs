using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Notch.Core.Widgets;

/// <param name="Start">In local time. For all-day events, midnight at the start of the day.</param>
/// <param name="Feed">Position of the feed the event came from in the list of feeds, to tell calendars apart.</param>
public sealed record CalendarEntry(string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string? Location, int Feed = 0);

/// <summary>Reads iCalendar (.ics) text, as published by Google Calendar, Outlook and most other calendars.</summary>
public static class CalendarFeed
{
    /// <summary>Events that overlap [<paramref name="from"/>, <paramref name="to"/>), recurring ones expanded, earliest first.</summary>
    /// <exception cref="FormatException">The text is not a calendar.</exception>
    public static IReadOnlyList<CalendarEntry> ReadEntries(string icsText, DateTimeOffset from, DateTimeOffset to)
    {
        Calendar? calendar;
        try
        {
            calendar = Calendar.Load(icsText);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new FormatException("The feed is not a valid iCalendar file.", e);
        }

        if (calendar is null)
        {
            throw new FormatException("The feed is not a valid iCalendar file.");
        }

        // Start a day early so events already in progress (and all-day events for today) are found.
        var searchStart = new CalDateTime(from.UtcDateTime.AddDays(-1), "UTC");
        var searchEnd = new CalDateTime(to.UtcDateTime, "UTC");

        var entries = new List<CalendarEntry>();
        foreach (Occurrence occurrence in calendar.GetOccurrences<CalendarEvent>(searchStart).TakeWhileBefore(searchEnd))
        {
            if (occurrence.Source is not CalendarEvent source)
            {
                continue;
            }

            bool allDay = !occurrence.Period.StartTime.HasTime;
            DateTimeOffset start = ToLocal(occurrence.Period.StartTime);
            DateTimeOffset end = occurrence.Period.EffectiveEndTime is { } endTime ? ToLocal(endTime) : start;

            if (end > from && start < to)
            {
                entries.Add(new CalendarEntry(
                    string.IsNullOrWhiteSpace(source.Summary) ? "(No title)" : source.Summary.Trim(),
                    start,
                    end,
                    allDay,
                    string.IsNullOrWhiteSpace(source.Location) ? null : source.Location.Trim()));
            }
        }

        return [.. entries.OrderBy(e => e.Start).ThenBy(e => e.Title, StringComparer.CurrentCulture)];
    }

    private static DateTimeOffset ToLocal(CalDateTime time)
    {
        // Dates without a time, and "floating" times without a zone, mean wall-clock time wherever the reader is.
        if (!time.HasTime || time.IsFloating)
        {
            var local = DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        return new DateTimeOffset(time.AsUtc, TimeSpan.Zero).ToLocalTime();
    }
}
