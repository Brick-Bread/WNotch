using Microsoft.Extensions.Time.Testing;
using Notch.Core.Widgets;

namespace Notch.Core.Tests;

public class WidgetTests
{
    [Fact]
    public void Countdown_runs_pauses_and_finishes()
    {
        var time = new FakeTimeProvider();
        var timer = new CountdownTimer(time);
        Assert.Equal(CountdownState.Idle, timer.State);

        timer.Start(TimeSpan.FromMinutes(5));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CountdownState.Running, timer.State);
        Assert.Equal(TimeSpan.FromMinutes(3), timer.Remaining);
        Assert.Equal(0.4, timer.Progress, precision: 6);

        timer.Pause();
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CountdownState.Paused, timer.State);
        Assert.Equal(TimeSpan.FromMinutes(3), timer.Remaining);

        timer.Resume();
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(CountdownState.Finished, timer.State);
        Assert.Equal(TimeSpan.Zero, timer.Remaining);

        timer.Reset();
        Assert.Equal(CountdownState.Idle, timer.State);
    }

    [Fact]
    public void Pomodoro_alternates_and_takes_a_long_break_every_fourth_focus()
    {
        var cycle = new PomodoroCycle(PomodoroDurations.Classic);
        var phases = new List<string> { cycle.Label };

        for (int i = 0; i < 9; i++)
        {
            cycle.Advance();
            phases.Add(cycle.Label);
        }

        Assert.Equal(
            ["Focus 1/4", "Short break", "Focus 2/4", "Short break", "Focus 3/4", "Short break", "Focus 4/4", "Long break", "Focus 1/4", "Short break"],
            phases);
        Assert.Equal(5, cycle.CompletedFocuses);
    }

    [Fact]
    public void Pomodoro_durations_follow_the_phase()
    {
        var cycle = new PomodoroCycle(new PomodoroDurations(TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), 2));

        Assert.Equal(TimeSpan.FromMinutes(50), cycle.CurrentDuration);
        cycle.Advance();
        Assert.Equal(TimeSpan.FromMinutes(10), cycle.CurrentDuration);
        cycle.Advance();
        cycle.Advance();
        Assert.Equal(PomodoroPhase.LongBreak, cycle.Phase);
        Assert.Equal(TimeSpan.FromMinutes(30), cycle.CurrentDuration);
    }

    [Theory]
    [InlineData(300, "5:00")]
    [InlineData(299.2, "5:00")]
    [InlineData(59, "0:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-4, "0:00")]
    public void Countdown_formats_remaining_time(double seconds, string expected) =>
        Assert.Equal(expected, CountdownTimer.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("12", 720)]
    [InlineData("1.5", 90)]
    [InlineData(" 1:30 ", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("90s", 90)]
    [InlineData("45m", 2700)]
    [InlineData("1h20m", 4800)]
    [InlineData("1 hr 20 min", 4800)]
    [InlineData("2 Hours", 7200)]
    [InlineData("1m30s", 90)]
    [InlineData("24h", 86400)]
    public void Durations_are_read_as_typed(string text, int seconds)
    {
        Assert.True(DurationParser.TryParse(text, out TimeSpan duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1h 20")]
    [InlineData("1:2:3:4")]
    [InlineData("1:xx")]
    [InlineData("25h")]
    [InlineData("0.001s")]
    public void Durations_that_make_no_sense_are_rejected(string? text) =>
        Assert.False(DurationParser.TryParse(text, out _));

    [Theory]
    [InlineData(300, "5m")]
    [InlineData(5400, "1h30m")]
    [InlineData(90, "1m30s")]
    [InlineData(45, "45s")]
    [InlineData(3600, "1h")]
    public void Durations_are_described_in_a_form_that_reads_back(int seconds, string expected)
    {
        Assert.Equal(expected, DurationParser.Describe(TimeSpan.FromSeconds(seconds)));
        Assert.True(DurationParser.TryParse(expected, out TimeSpan duration));
        Assert.Equal(seconds, duration.TotalSeconds);
    }

    [Theory]
    [InlineData("15m", "", 900, "15m")]
    [InlineData("Tea 3m", "Tea", 180, "Tea")]
    [InlineData("Long walk 1h 20m", "Long walk", 4800, "Long walk")]
    [InlineData("Round 2 1:30", "Round 2", 90, "Round 2")]
    [InlineData("A very long preset name 10m", "A very lon", 600, "A very lon")]
    public void Preset_lines_have_an_optional_name(string line, string name, int seconds, string label)
    {
        Assert.True(TimerPreset.TryParse(line, out TimerPreset preset));
        Assert.Equal(new TimerPreset(name, seconds), preset);
        Assert.Equal(label, preset.Label);
        Assert.True(TimerPreset.TryParse(preset.ToLine(), out TimerPreset again));
        Assert.Equal(preset, again);
    }

    [Theory]
    [InlineData("Tea")]
    [InlineData("Tea soon")]
    [InlineData("")]
    public void Preset_lines_without_a_length_are_rejected(string line) =>
        Assert.False(TimerPreset.TryParse(line, out _));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(12.4 * 1024 * 1024, "12 MB")]
    [InlineData(3.25 * 1024 * 1024 * 1024, "3.3 GB")]
    public void Byte_sizes_are_compact(double bytes, string expected) =>
        Assert.Equal(expected, StatsFormat.Bytes(bytes));

    [Fact]
    public void Webcal_links_become_https() =>
        Assert.Equal("https://example.com/cal.ics", CalendarService.NormalizeUrl(" webcal://example.com/cal.ics "));

    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:standup
        DTSTAMP:20260101T000000Z
        DTSTART:20261001T090000Z
        DTEND:20261001T091500Z
        RRULE:FREQ=DAILY;COUNT=5
        SUMMARY:Standup
        LOCATION:Room 2
        END:VEVENT
        BEGIN:VEVENT
        UID:holiday
        DTSTAMP:20260101T000000Z
        DTSTART;VALUE=DATE:20261002
        DTEND;VALUE=DATE:20261003
        SUMMARY:Holiday
        END:VEVENT
        BEGIN:VEVENT
        UID:old
        DTSTAMP:20260101T000000Z
        DTSTART:20260901T090000Z
        DTEND:20260901T100000Z
        SUMMARY:Long gone
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Feed_expands_recurrences_within_the_window()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        IReadOnlyList<CalendarEntry> entries = CalendarFeed.ReadEntries(Ics, from, from.AddDays(3));

        Assert.Equal(3, entries.Count(e => e.Title == "Standup"));
        Assert.DoesNotContain(entries, e => e.Title == "Long gone");

        CalendarEntry standup = entries.First(e => e.Title == "Standup");
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), standup.Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromMinutes(15), standup.End - standup.Start);
        Assert.Equal("Room 2", standup.Location);
        Assert.False(standup.IsAllDay);
    }

    [Fact]
    public void Feed_reads_all_day_events_as_local_dates()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        CalendarEntry holiday = CalendarFeed.ReadEntries(Ics, from, from.AddDays(3)).Single(e => e.Title == "Holiday");

        Assert.True(holiday.IsAllDay);
        Assert.Equal(new DateTime(2026, 10, 2), holiday.Start.DateTime);
    }

    [Fact]
    public void Feed_rejects_text_that_is_not_a_calendar() =>
        Assert.Throws<FormatException>(() => CalendarFeed.ReadEntries("<html>Sign in</html>", DateTimeOffset.Now, DateTimeOffset.Now.AddDays(1)));

    [Fact]
    public async Task Service_merges_feeds_and_counts_failures()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(new StubHandler(url => url.Contains("good") ? Ics : null));
        var service = new CalendarService(http, time);

        await service.RefreshAsync(["webcal://feeds.test/good.ics", "https://feeds.test/missing.ics"]);

        Assert.Equal(1, service.FailedFeeds);
        Assert.Contains(service.Upcoming, e => e.Title == "Holiday");
        Assert.All(service.Upcoming, e => Assert.Equal(0, e.Feed));
        Assert.Equal(service.Upcoming.OrderBy(e => e.Start), service.Upcoming);
    }

    private sealed class StubHandler(Func<string, string?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = respond(request.RequestUri!.ToString());
            return Task.FromResult(body is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
