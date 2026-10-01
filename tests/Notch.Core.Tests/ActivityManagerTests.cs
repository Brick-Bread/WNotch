using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;

namespace Notch.Core.Tests;

public class ActivityManagerTests
{
    private static Activity Make(string id, ActivityTier tier, TimeSpan? lifetime = null) =>
        new() { Id = id, Tier = tier, Title = id, Lifetime = lifetime };

    [Fact]
    public void Snapshot_orders_by_tier_then_most_recent()
    {
        using var manager = new ActivityManager(new FakeTimeProvider());

        manager.Publish(Make("media", ActivityTier.Ongoing));
        manager.Publish(Make("agent", ActivityTier.Attention));
        manager.Publish(Make("timer", ActivityTier.Ongoing));
        manager.Publish(Make("volume", ActivityTier.Transient));

        Assert.Equal(["volume", "agent", "timer", "media"], manager.Snapshot().Select(a => a.Id));
    }

    [Fact]
    public void Republishing_replaces_and_keeps_position()
    {
        using var manager = new ActivityManager(new FakeTimeProvider());

        manager.Publish(Make("media", ActivityTier.Ongoing));
        manager.Publish(Make("timer", ActivityTier.Ongoing));
        manager.Publish(Make("media", ActivityTier.Ongoing) with { Title = "updated" });

        IReadOnlyList<Activity> snapshot = manager.Snapshot();
        Assert.Equal(["timer", "media"], snapshot.Select(a => a.Id));
        Assert.Equal("updated", snapshot[1].Title);
    }

    [Fact]
    public void Transient_expires_after_its_lifetime()
    {
        var time = new FakeTimeProvider();
        using var manager = new ActivityManager(time);
        int changes = 0;
        manager.Changed += (_, _) => changes++;

        manager.Publish(Make("volume", ActivityTier.Transient, TimeSpan.FromSeconds(2)));
        time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Single(manager.Snapshot());

        time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Empty(manager.Snapshot());
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Republishing_a_transient_restarts_its_lifetime()
    {
        var time = new FakeTimeProvider();
        using var manager = new ActivityManager(time);

        manager.Publish(Make("volume", ActivityTier.Transient, TimeSpan.FromSeconds(2)));
        time.Advance(TimeSpan.FromSeconds(1.5));
        manager.Publish(Make("volume", ActivityTier.Transient, TimeSpan.FromSeconds(2)));
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(manager.Snapshot());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(manager.Snapshot());
    }

    [Fact]
    public void Ongoing_activities_do_not_expire()
    {
        var time = new FakeTimeProvider();
        using var manager = new ActivityManager(time);

        manager.Publish(Make("media", ActivityTier.Ongoing));
        time.Advance(TimeSpan.FromHours(1));

        Assert.Single(manager.Snapshot());
    }

    [Fact]
    public void Suppressed_ids_are_removed_and_ignored()
    {
        using var manager = new ActivityManager(new FakeTimeProvider());
        manager.Publish(Make("media", ActivityTier.Ongoing));
        manager.Publish(Make("timer", ActivityTier.Ongoing));

        manager.SetSuppressed(["media"]);
        manager.Publish(Make("media", ActivityTier.Ongoing));
        Assert.Equal(["timer"], manager.Snapshot().Select(a => a.Id));

        manager.SetSuppressed([]);
        manager.Publish(Make("media", ActivityTier.Ongoing));
        Assert.Equal(2, manager.Snapshot().Count);
    }

    [Fact]
    public void Remove_reports_whether_anything_was_removed()
    {
        using var manager = new ActivityManager(new FakeTimeProvider());
        manager.Publish(Make("media", ActivityTier.Ongoing));

        Assert.True(manager.Remove("media"));
        Assert.False(manager.Remove("media"));
        Assert.Empty(manager.Snapshot());
    }
}
