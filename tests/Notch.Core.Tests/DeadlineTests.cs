using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Automation;
using Notch.Core.Deadlines;
using Notch.Core.Widgets;

namespace Notch.Core.Tests;

public sealed class DeadlineTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "notch-deadlines-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>Wednesday 25 November 2026, 10:00 on this PC.</summary>
    private static DateTimeOffset Wednesday10 => Local(2026, 11, 25, 10, 0);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Theory]
    [InlineData("in 3d", 3 * 24 * 60)]
    [InlineData("3d 4h", (3 * 24 * 60) + 240)]
    [InlineData("2h30m", 150)]
    [InlineData("in 45 min", 45)]
    [InlineData("1w", 7 * 24 * 60)]
    public void Reads_lengths_from_now(string text, int minutes)
    {
        Assert.True(DueParser.TryParse(text, Wednesday10, out DateTimeOffset due));
        Assert.Equal(Wednesday10.AddMinutes(minutes), due);
    }

    [Theory]
    [InlineData("today", 2026, 11, 25, 23, 59)]
    [InlineData("today 17:00", 2026, 11, 25, 17, 0)]
    [InlineData("tonight", 2026, 11, 25, 20, 0)]
    [InlineData("tomorrow", 2026, 11, 26, 23, 59)]
    [InlineData("tomorrow 9am", 2026, 11, 26, 9, 0)]
    [InlineData("Fri 5pm", 2026, 11, 27, 17, 0)]
    [InlineData("friday", 2026, 11, 27, 23, 59)]
    [InlineData("wed", 2026, 11, 25, 23, 59)]
    [InlineData("wed 9:00", 2026, 12, 2, 9, 0)]
    [InlineData("next wed", 2026, 12, 2, 23, 59)]
    [InlineData("mon at 8:30", 2026, 11, 30, 8, 30)]
    [InlineData("17:00", 2026, 11, 25, 17, 0)]
    [InlineData("9:00", 2026, 11, 26, 9, 0)]
    [InlineData("12", 2026, 11, 25, 12, 0)]
    [InlineData("2026-12-01", 2026, 12, 1, 23, 59)]
    [InlineData("2026-12-01 09:30", 2026, 12, 1, 9, 30)]
    [InlineData("2026-12-01T18:00", 2026, 12, 1, 18, 0)]
    public void Reads_days_and_times(string text, int year, int month, int day, int hour, int minute)
    {
        Assert.True(DueParser.TryParse(text, Wednesday10, out DateTimeOffset due), text);
        Assert.Equal(Local(year, month, day, hour, minute), due);
    }

    [Fact]
    public void Reads_a_time_with_an_offset_as_given()
    {
        Assert.True(DueParser.TryParse("2026-12-01T18:00Z", Wednesday10, out DateTimeOffset due));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 18, 0, 0, TimeSpan.Zero), due);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("whenever")]
    [InlineData("25:00")]
    [InlineData("in 99999d")]
    public void Refuses_what_it_cannot_read(string text) => Assert.False(DueParser.TryParse(text, Wednesday10, out _));

    [Theory]
    [InlineData(3 * 24 * 60 + 300, "3d")]
    [InlineData(5 * 60 + 59, "5h")]
    [InlineData(42, "42m")]
    [InlineData(0.5, "now")]
    [InlineData(-0.5, "now")]
    [InlineData(-130, "-2h")]
    [InlineData(-3 * 24 * 60, "-3d")]
    public void The_chip_is_a_few_characters(double minutes, string chip) =>
        Assert.Equal(chip, DeadlineTiming.Chip(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData((2 * 24 * 60) + 240, "in 2d 4h")]
    [InlineData(2 * 24 * 60, "in 2d")]
    [InlineData(320, "in 5h 20m")]
    [InlineData(42, "in 42 min")]
    [InlineData(0.2, "due now")]
    [InlineData(-180, "3h overdue")]
    public void Describes_how_long_is_left(double minutes, string text) =>
        Assert.Equal(text, DeadlineTiming.Describe(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(48 * 60, DeadlineUrgency.Later, "Blue")]
    [InlineData(20 * 60, DeadlineUrgency.Near, "Amber")]
    [InlineData(30, DeadlineUrgency.Soon, "Orange")]
    [InlineData(-1, DeadlineUrgency.Overdue, "Red")]
    public void Urgency_grows_as_it_comes_closer(double minutes, DeadlineUrgency urgency, string color)
    {
        DeadlineUrgency actual = DeadlineTiming.Urgency(TimeSpan.FromMinutes(minutes), TimeSpan.FromHours(24));
        Assert.Equal(urgency, actual);
        Assert.Equal(GlowColor.FromName(color), DeadlineTiming.Color(actual));
    }

    [Fact]
    public void Near_hours_out_of_range_fall_back_to_a_day()
    {
        Assert.Equal(TimeSpan.FromHours(6), DeadlineTiming.NearFromHours(6));
        Assert.Equal(DeadlineTiming.DefaultNear, DeadlineTiming.NearFromHours(0));
        Assert.Equal(DeadlineTiming.DefaultNear, DeadlineTiming.NearFromHours(100_000));
    }

    [Fact]
    public void Names_when_it_is_due_relative_to_today()
    {
        Assert.StartsWith("Today ", DeadlineTiming.When(Wednesday10.AddHours(3), Wednesday10));
        Assert.StartsWith("Tomorrow ", DeadlineTiming.When(Wednesday10.AddDays(1), Wednesday10));
        Assert.StartsWith("Yesterday ", DeadlineTiming.When(Wednesday10.AddDays(-1), Wednesday10));
    }

    [Fact]
    public void Milestones_are_the_latest_one_passed()
    {
        Assert.Equal(DeadlineMilestone.Day, DeadlineTiming.Crossed(TimeSpan.FromHours(25), TimeSpan.FromHours(23)));
        Assert.Equal(DeadlineMilestone.Hour, DeadlineTiming.Crossed(TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(59)));
        Assert.Equal(DeadlineMilestone.Quarter, DeadlineTiming.Crossed(TimeSpan.FromHours(2), TimeSpan.FromMinutes(10)));
        Assert.Equal(DeadlineMilestone.Due, DeadlineTiming.Crossed(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(-2)));
        Assert.Null(DeadlineTiming.Crossed(TimeSpan.FromHours(5), TimeSpan.FromHours(4)));
        Assert.Null(DeadlineTiming.Crossed(TimeSpan.FromMinutes(-1), TimeSpan.FromMinutes(-2)));
    }

    [Fact]
    public void Alerts_only_for_milestones_passed_while_watching()
    {
        var alerts = new DeadlineAlerts();
        var deadline = new Deadline("a", "Report", Wednesday10.AddMinutes(61), DeadlineSource.Manual);

        // First look: nothing to compare with, so nothing is announced.
        Assert.Empty(alerts.Check([deadline], Wednesday10));

        IReadOnlyList<DeadlineAlert> passed = alerts.Check([deadline], Wednesday10.AddMinutes(2));
        Assert.Equal(DeadlineMilestone.Hour, Assert.Single(passed).Milestone);

        // Once only.
        Assert.Empty(alerts.Check([deadline], Wednesday10.AddMinutes(3)));
    }

    [Fact]
    public void Snoozed_deadlines_pass_their_milestones_quietly()
    {
        var alerts = new DeadlineAlerts();
        var deadline = new Deadline("a", "Report", Wednesday10.AddMinutes(1), DeadlineSource.Manual);
        alerts.Check([deadline], Wednesday10);
        Assert.Empty(alerts.Check([deadline], Wednesday10.AddMinutes(2), _ => true));
    }

    [Fact]
    public void A_moved_deadline_starts_over()
    {
        var alerts = new DeadlineAlerts();
        var deadline = new Deadline("a", "Report", Wednesday10.AddMinutes(61), DeadlineSource.Manual);
        alerts.Check([deadline], Wednesday10);

        // Moved later: its first look at the new time announces nothing.
        Assert.Empty(alerts.Check([deadline with { Due = Wednesday10.AddMinutes(30) }], Wednesday10.AddMinutes(2)));
    }

    [Fact]
    public void The_board_keeps_the_users_deadlines_in_order_and_on_disk()
    {
        string path = Path.Combine(_folder, "deadlines.json");
        var time = new FakeTimeProvider(Wednesday10);
        var board = new DeadlineBoard(path, time);
        int changes = 0;
        board.Changed += (_, _) => changes++;

        Deadline later = board.Add("  Taxes  ", Wednesday10.AddDays(10))!;
        Deadline sooner = board.Add("Report", Wednesday10.AddDays(1))!;
        Assert.Equal("Taxes", later.Title);
        Assert.Equal(["Report", "Taxes"], board.All().Select(d => d.Title));
        Assert.Equal(sooner, board.Next());
        Assert.Equal(2, changes);
        Assert.Null(board.Add("   ", Wednesday10));

        var reloaded = new DeadlineBoard(path, time);
        Assert.Equal(["Report", "Taxes"], reloaded.All().Select(d => d.Title));

        Assert.True(reloaded.Edit(sooner.Id, "Report v2", Wednesday10.AddDays(20)));
        Assert.Equal(["Taxes", "Report v2"], reloaded.All().Select(d => d.Title));
        Assert.True(reloaded.Complete(later.Id));
        Assert.Equal(["Report v2"], new DeadlineBoard(path, time).All().Select(d => d.Title));
    }

    [Fact]
    public void A_broken_file_starts_an_empty_list()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "deadlines.json");
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new DeadlineBoard(path).All());
    }

    [Fact]
    public void Snoozing_keeps_a_deadline_out_of_the_pill_until_then()
    {
        var time = new FakeTimeProvider(Wednesday10);
        var board = new DeadlineBoard(null, time);
        Deadline first = board.Add("First", Wednesday10.AddHours(2))!;
        Deadline second = board.Add("Second", Wednesday10.AddHours(5))!;

        Assert.True(board.Snooze(first.Id, Wednesday10.AddHours(1)));
        Assert.Equal(second, board.Next());
        Assert.Equal(2, board.All().Count);
        Assert.NotNull(board.SnoozedUntil(first));

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Null(board.SnoozedUntil(first));
        Assert.Equal(first, board.Next());
    }

    [Fact]
    public void Links_set_by_title_so_running_a_script_twice_moves_it()
    {
        var board = new DeadlineBoard();
        board.SetLinked("Release", Wednesday10.AddDays(2), "v0.23");
        board.SetLinked("release", Wednesday10.AddDays(3));
        Deadline only = Assert.Single(board.All());
        Assert.Equal(Wednesday10.AddDays(3), only.Due);
        Assert.Equal(DeadlineSource.Link, only.Source);

        Assert.True(board.RemoveLinked("RELEASE"));
        Assert.Empty(board.All());
        Assert.False(board.RemoveLinked("Release"));
    }

    [Fact]
    public void A_completed_calendar_deadline_stays_hidden_until_it_moves()
    {
        string path = Path.Combine(_folder, "deadlines.json");
        var time = new FakeTimeProvider(Wednesday10);
        var board = new DeadlineBoard(path, time);
        var due = new Deadline("cal:0:x", "Essay", Wednesday10.AddDays(1), DeadlineSource.Calendar);
        board.SetCalendar([due]);
        Assert.True(board.Complete(due.Id));
        Assert.Empty(board.All());

        // Still hidden after a refresh and a restart.
        var reloaded = new DeadlineBoard(path, time);
        reloaded.SetCalendar([due]);
        Assert.Empty(reloaded.All());

        // The calendar moved it: it is back.
        reloaded.SetCalendar([due with { Due = Wednesday10.AddDays(2) }]);
        Assert.Single(reloaded.All());
    }

    [Fact]
    public void Plugins_deadlines_are_their_own_and_go_with_them()
    {
        var board = new DeadlineBoard();
        Assert.True(board.SetPlugin("acme.a", new PluginDeadline { Id = "x", Title = "Ship", Due = Wednesday10.AddDays(1) }));
        Assert.True(board.SetPlugin("acme.b", new PluginDeadline { Id = "x", Title = "Review", Due = Wednesday10.AddDays(2) }));
        Assert.False(board.SetPlugin("acme.a", new PluginDeadline { Id = "y", Title = " ", Due = Wednesday10 }));
        Assert.Equal(2, board.All().Count);
        Assert.Equal("acme.a", board.All()[0].Owner);

        board.ClearPlugin("acme.a");
        Assert.Equal(["Review"], board.All().Select(d => d.Title));
        Assert.True(board.RemovePlugin("acme.b", "x"));
        Assert.Empty(board.All());

        for (int i = 0; i < DeadlineBoard.MaxPerPlugin; i++)
        {
            Assert.True(board.SetPlugin("acme.c", new PluginDeadline { Id = $"d{i}", Title = $"D{i}", Due = Wednesday10 }));
        }

        Assert.False(board.SetPlugin("acme.c", new PluginDeadline { Id = "one-too-many", Title = "X", Due = Wednesday10 }));
    }

    [Fact]
    public void The_pill_glances_at_a_far_deadline_and_hands_a_near_one_the_pill()
    {
        var deadline = new Deadline("a", "Report", Wednesday10.AddDays(3), DeadlineSource.Manual);
        Assert.Null(DeadlineActivities.ForPill(null, Wednesday10, TimeSpan.FromHours(24)));

        Activity far = DeadlineActivities.ForPill(deadline, Wednesday10, TimeSpan.FromHours(24))!;
        Assert.Equal(ActivityTier.Glance, far.Tier);
        Assert.Equal("3d · Report", far.Title);

        Activity near = DeadlineActivities.ForPill(deadline, Wednesday10.AddDays(2.5), TimeSpan.FromHours(24))!;
        Assert.Equal(ActivityTier.Attention, near.Tier);
        Assert.Equal("Report", near.Title);
        Assert.Equal("in 12h", near.Detail);
        Assert.Equal(new Glow(GlowColor.Amber, GlowPattern.Breathe, 0.45), near.Glow);

        Activity late = DeadlineActivities.ForPill(deadline, Wednesday10.AddDays(3.5), TimeSpan.FromHours(24))!;
        Assert.Equal("12h overdue", late.Detail);
        Assert.Equal(new Glow(GlowColor.Red, GlowPattern.Pulse), late.Glow);
    }

    [Fact]
    public void Commands_add_move_and_remove_deadlines()
    {
        Assert.True(CommandParser.TryParseArguments(["deadline", "Report", "--due", "in 3d", "--detail", "Q4"], out NotchCommand? command, out _));
        var set = Assert.IsType<SetDeadlineCommand>(command);
        Assert.Equal("Report", set.Title);
        Assert.Equal("Q4", set.Detail);
        Assert.InRange(set.Due - DateTimeOffset.Now, TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));

        Assert.True(CommandParser.TryParseUrl("notch://deadline?title=Taxes&in=2w", out command, out _));
        Assert.Equal("Taxes", Assert.IsType<SetDeadlineCommand>(command).Title);

        Assert.True(CommandParser.TryParseArguments(["deadline", "Report", "--done"], out command, out _));
        Assert.Equal("Report", Assert.IsType<RemoveDeadlineCommand>(command).Title);

        Assert.False(CommandParser.TryParseArguments(["deadline", "Report"], out _, out string? error));
        Assert.Contains("--due", error);
        Assert.False(CommandParser.TryParseArguments(["deadline", "--due", "fri"], out _, out _));
        Assert.False(CommandParser.TryParseArguments(["deadline", new string('x', 200), "--due", "fri"], out _, out _));
    }

    [Fact]
    public void Calendar_deadlines_are_tagged_events_and_tasks_with_a_due_date()
    {
        const string Ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            BEGIN:VEVENT
            UID:essay
            DTSTAMP:20260101T000000Z
            DTSTART:20261127T160000Z
            DTEND:20261127T170000Z
            SUMMARY:Due: Essay draft
            END:VEVENT
            BEGIN:VEVENT
            UID:launch
            DTSTAMP:20260101T000000Z
            DTSTART;VALUE=DATE:20261130
            SUMMARY:Launch
            CATEGORIES:Work,Deadline
            END:VEVENT
            BEGIN:VEVENT
            UID:lunch
            DTSTAMP:20260101T000000Z
            DTSTART:20261126T120000Z
            DTEND:20261126T130000Z
            SUMMARY:Lunch
            END:VEVENT
            BEGIN:VTODO
            UID:taxes
            DTSTAMP:20260101T000000Z
            DUE:20261201T120000Z
            SUMMARY:File taxes
            END:VTODO
            BEGIN:VTODO
            UID:done
            DTSTAMP:20260101T000000Z
            DUE:20261128T120000Z
            STATUS:COMPLETED
            SUMMARY:Already done
            END:VTODO
            END:VCALENDAR
            """;

        DateTimeOffset from = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<Deadline> deadlines = CalendarFeed.ReadDeadlines(Ics, from, from.AddDays(30), feed: 2);

        Assert.Equal(["Essay draft", "Launch", "File taxes"], deadlines.Select(d => d.Title));
        Assert.All(deadlines, d => Assert.Equal(DeadlineSource.Calendar, d.Source));
        Assert.All(deadlines, d => Assert.StartsWith("cal:2:", d.Id));
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 16, 0, 0, TimeSpan.Zero), deadlines[0].Due);

        // An all-day deadline is due at the end of its day.
        Assert.Equal(new TimeSpan(23, 59, 0), deadlines[1].Due.TimeOfDay);
    }

    [Theory]
    [InlineData("Due: Essay", null, "Essay")]
    [InlineData("due:   Essay  ", null, "Essay")]
    [InlineData("Essay", "deadline", "Essay")]
    [InlineData("Due:", null, "(No title)")]
    [InlineData("Essay", "work", null)]
    [InlineData("Overdue: nothing", null, null)]
    public void Tells_deadline_events_by_title_or_category(string summary, string? category, string? title) =>
        Assert.Equal(title, CalendarFeed.DeadlineTitle(summary, category is null ? null : [category]));
}
