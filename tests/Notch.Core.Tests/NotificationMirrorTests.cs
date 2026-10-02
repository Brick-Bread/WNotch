using Notch.Core.Activities;
using Notch.Core.Shell;

namespace Notch.Core.Tests;

public class NotificationMirrorTests
{
    private static NotificationInfo N(long id, string app = "Mail", string title = "Ada Lovelace", string? body = "Lunch tomorrow?") =>
        new(id, app, title, body);

    [Fact]
    public void With_no_list_every_app_is_shown()
    {
        Assert.True(NotificationMirror.Allows("Anything", []));
    }

    [Fact]
    public void A_list_limits_it_to_the_apps_named_in_any_case()
    {
        string[] allowed = ["outlook", " Teams "];
        Assert.True(NotificationMirror.Allows("Microsoft Outlook", allowed));
        Assert.True(NotificationMirror.Allows("Microsoft Teams (work)", allowed));
        Assert.False(NotificationMirror.Allows("Slack", allowed));
    }

    [Fact]
    public void Blank_lines_in_the_list_do_not_let_everything_in()
    {
        Assert.False(NotificationMirror.Allows("Slack", ["", "  "]));
    }

    [Fact]
    public void The_pill_shows_the_sender_and_the_app_but_never_the_message()
    {
        Activity activity = NotificationMirror.ToActivity(N(7))!;

        Assert.Equal("notification.7", activity.Id);
        Assert.Equal(ActivityTier.Transient, activity.Tier);
        Assert.Equal("Ada Lovelace", activity.Title);
        Assert.Equal("Mail", activity.Detail);
        Assert.DoesNotContain("Lunch", activity.Title + activity.Detail);
        Assert.Equal(NotificationMirror.Lifetime, activity.Lifetime);
    }

    [Fact]
    public void Long_and_multi_line_titles_are_shortened_to_one_line()
    {
        Activity activity = NotificationMirror.ToActivity(N(1, title: "first line\r\nsecond line " + new string('x', 200)))!;
        Assert.DoesNotContain('\n', activity.Title);
        Assert.True(activity.Title.Length <= NotificationMirror.MaxTitleLength);
        Assert.EndsWith("…", activity.Title);
    }

    [Fact]
    public void A_notification_without_a_title_falls_back_to_the_app()
    {
        Activity activity = NotificationMirror.ToActivity(N(2, app: "Backup", title: ""))!;
        Assert.Equal("Backup", activity.Title);
        Assert.Null(activity.Detail);
    }

    [Fact]
    public void A_notification_with_nothing_to_show_is_skipped()
    {
        Assert.Null(NotificationMirror.ToActivity(N(3, app: " ", title: "")));
    }

    [Fact]
    public void The_first_poll_learns_what_is_there_and_reports_nothing()
    {
        var diff = new NotificationDiff();
        Assert.Empty(diff.Fresh([N(1), N(2)]));
        Assert.Empty(diff.Fresh([N(1), N(2)]));
    }

    [Fact]
    public void Only_new_notifications_are_reported_afterwards()
    {
        var diff = new NotificationDiff();
        diff.Fresh([N(1)]);

        Assert.Equal([2L, 3L], diff.Fresh([N(1), N(2), N(3)]).Select(n => n.Id));
        Assert.Empty(diff.Fresh([N(1), N(2), N(3)]));
    }

    [Fact]
    public void A_dismissed_notification_that_comes_back_counts_as_new()
    {
        var diff = new NotificationDiff();
        diff.Fresh([N(1)]);
        diff.Fresh([]);

        Assert.Single(diff.Fresh([N(1)]));
    }

    [Fact]
    public void Resetting_forgets_everything()
    {
        var diff = new NotificationDiff();
        diff.Fresh([N(1)]);
        diff.Reset();

        Assert.Empty(diff.Fresh([N(1), N(2)]));
    }
}
