using Notch.Core.Activities;

namespace Notch.Core.Tests;

public class ElapsedClockTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(245, "4:05")]
    [InlineData(3599, "59:59")]
    [InlineData(5025, "1:23:45")]
    [InlineData(90000, "25:00:00")]
    [InlineData(-5, "0:00")]
    public void Counts_minutes_and_seconds_then_hours(int seconds, string expected)
    {
        Assert.Equal(expected, ElapsedClock.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Since_counts_from_the_moment_given()
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("1:00:30", ElapsedClock.Since(start, start.AddSeconds(3630)));
    }
}
