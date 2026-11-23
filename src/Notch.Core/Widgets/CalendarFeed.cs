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

    /// <summary>The category that marks an event as a deadline, besides a title starting "Due:".</summary>
    public const string DeadlineCategory = "DEADLINE";

    /// <summary>
    /// Deadlines in the calendar that fall due in [<paramref name="from"/>, <paramref name="to"/>): events whose title
    /// starts with "Due:" or that carry the <see cref="DeadlineCategory"/> category (due when they start, an all-day one
    /// at the end of its day), and tasks (VTODO) with a due time that are not completed. Earliest first.
    /// </summary>
    /// <exception cref="FormatException">The text is not a calendar.</exception>
    public static IReadOnlyList<Deadlines.Deadline> ReadDeadlines(string icsText, DateTimeOffset from, DateTimeOffset to, int feed = 0)
    {
        Calendar calendar = Load(icsText);
        var deadlines = new List<Deadlines.Deadline>();
        var searchStart = new CalDateTime(from.UtcDateTime.AddDays(-1), "UTC");
        var searchEnd = new CalDateTime(to.UtcDateTime, "UTC");

        foreach (Occurrence occurrence in calendar.GetOccurrences<CalendarEvent>(searchStart).TakeWhileBefore(searchEnd))
        {
            if (occurrence.Source is not CalendarEvent source || DeadlineTitle(source.Summary, source.Categories) is not { } title)
            {
                continue;
            }

            DateTimeOffset due = DueOf(occurrence.Period.StartTime);
            if (due >= from && due < to)
            {
                deadlines.Add(new Deadlines.Deadline(
                    $"cal:{feed}:{source.Uid ?? title}:{due.UtcTicks}",
                    title,
                    due,
                    Deadlines.DeadlineSource.Calendar,
                    string.IsNullOrWhiteSpace(source.Location) ? null : source.Location.Trim()));
            }
        }

        foreach (Todo todo in calendar.Todos)
        {
            bool completed = todo.Completed is not null
                || string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(todo.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
            if (completed || todo.Due is not { } dueTime || string.IsNullOrWhiteSpace(todo.Summary))
            {
                continue;
            }

            DateTimeOffset due = DueOf(dueTime);
            if (due >= from && due < to)
            {
                string title = Deadlines.Deadline.Tidy(todo.Summary);
                deadlines.Add(new Deadlines.Deadline($"cal:{feed}:{todo.Uid ?? title}:{due.UtcTicks}", title, due, Deadlines.DeadlineSource.Calendar));
            }
        }

        return [.. deadlines.OrderBy(d => d.Due).ThenBy(d => d.Title, StringComparer.CurrentCulture)];
    }

    /// <summary>The deadline's title for an event that is one ("Due: Report" reads "Report"); null for any other event.</summary>
    public static string? DeadlineTitle(string? summary, IEnumerable<string>? categories)
    {
        string title = (summary ?? "").Trim();
        bool tagged = categories?.Any(c => string.Equals(c?.Trim(), DeadlineCategory, StringComparison.OrdinalIgnoreCase)) == true;
        if (title.StartsWith("due:", StringComparison.OrdinalIgnoreCase))
        {
            title = title[4..].Trim();
            tagged = true;
        }

        if (!tagged)
        {
            return null;
        }

        title = Deadlines.Deadline.Tidy(title);
        return title.Length > 0 ? title : "(No title)";
    }

    private static Calendar Load(string icsText)
    {
        try
        {
            return Calendar.Load(icsText) ?? throw new FormatException("The feed is not a valid iCalendar file.");
        }
        catch (Exception e) when (e is not OutOfMemoryException and not FormatException)
        {
            throw new FormatException("The feed is not a valid iCalendar file.", e);
        }
    }

    /// <summary>A due time; a date alone is due at the end of that day.</summary>
    private static DateTimeOffset DueOf(CalDateTime time)
    {
        DateTimeOffset local = ToLocal(time);
        if (time.HasTime)
        {
            return local;
        }

        DateTime end = DateTime.SpecifyKind(local.Date + Deadlines.DueParser.EndOfDay, DateTimeKind.Unspecified);
        return new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));
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
