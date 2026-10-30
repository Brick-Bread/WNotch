using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class UsageTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Claudes_answer_gives_the_limits_the_plan_has()
    {
        const string json = """
            {"five_hour":{"utilization":33.0,"resets_at":"2026-10-30T14:00:00.528743+00:00"},
             "seven_day":{"utilization":13.5,"resets_at":"2026-11-03T00:59:59.951713+00:00"},
             "seven_day_opus":null,
             "extra_usage":{"is_enabled":false}}
            """;

        UsageReading reading = UsageMeters.ReadClaude(json, Now)!;

        Assert.Equal(["5 hours", "Week"], reading.Windows.Select(w => w.Label));
        Assert.Equal(33.0, reading.Windows[0].UsedPercent);
        Assert.Equal(new DateTime(2026, 10, 30, 14, 0, 0, DateTimeKind.Utc), reading.Windows[0].ResetsAt!.Value.AddTicks(-(reading.Windows[0].ResetsAt!.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Null(UsageMeters.ReadClaude("""{"five_hour":null}""", Now));
        Assert.Null(UsageMeters.ReadClaude("not json", Now));
    }

    [Fact]
    public void A_limit_that_has_reset_since_it_was_read_is_back_at_nothing()
    {
        UsageReading reading = UsageMeters.ReadClaude("""{"five_hour":{"utilization":80,"resets_at":"2026-10-30T11:00:00Z"}}""", Now)!;

        Assert.Equal(0, reading.Windows[0].UsedPercent);
        Assert.Null(reading.Windows[0].ResetsAt);
    }

    [Fact]
    public void Claudes_sign_in_is_read_with_when_it_runs_out()
    {
        (string Token, DateTime? Expires) signIn = UsageMeters.ReadClaudeSignIn("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-x","refreshToken":"r","expiresAt":1793376000000}}""")!.Value;

        Assert.Equal("sk-ant-oat01-x", signIn.Token);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793376000000).UtcDateTime, signIn.Expires);
        Assert.Null(UsageMeters.ReadClaudeSignIn("""{"other":{}}"""));
        Assert.Null(UsageMeters.ReadClaudeSignIn(null));
    }

    [Fact]
    public void Codex_limits_come_from_the_last_token_count_that_has_them()
    {
        string[] lines =
        [
            """{"timestamp":"2026-10-30T11:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":10.0,"window_minutes":300,"resets_at":1793383200},"secondary":{"used_percent":40.0,"window_minutes":10080,"resets_at":1793800000}}}}""",
            """{"timestamp":"2026-10-30T11:30:00Z","type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":{"primary":{"used_percent":12.5,"window_minutes":300,"resets_at":1793383200},"secondary":{"used_percent":41.0,"window_minutes":10080,"resets_at":1793800000}}}}""",
            """{"timestamp":"2026-10-30T11:31:00Z","type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":null}}""",
            """{"timestamp":"2026-10-30T11:31:00Z","type":"response_item","payload":{}}""",
            "{\"half a line",
        ];

        UsageReading reading = UsageMeters.ReadCodex(lines, Now)!;

        Assert.Equal(["5 hours", "Week"], reading.Windows.Select(w => w.Label));
        Assert.Equal([12.5, 41.0], reading.Windows.Select(w => w.UsedPercent));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793383200).UtcDateTime, reading.Windows[0].ResetsAt);
        Assert.Equal(new DateTime(2026, 10, 30, 11, 30, 0, DateTimeKind.Utc), reading.At);
        Assert.Null(UsageMeters.ReadCodex(["{}", "nothing"], Now));
    }

    [Fact]
    public void Older_codex_versions_said_how_long_until_the_reset()
    {
        string[] lines = ["""{"timestamp":"2026-10-30T11:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":5,"window_minutes":299,"resets_in_seconds":7200}}}}"""];

        UsageWindow window = Assert.Single(UsageMeters.ReadCodex(lines, Now)!.Windows);
        Assert.Equal("5 hours", window.Label);
        Assert.Equal(new DateTime(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc), window.ResetsAt);
    }

    [Theory]
    [InlineData(300, "5 hours")]
    [InlineData(10080, "Week")]
    [InlineData(1440, "Day")]
    [InlineData(43200, "Month")]
    [InlineData(120, "2 hours")]
    public void Limits_are_named_by_their_length(int minutes, string name) => Assert.Equal(name, UsageMeters.WindowName(minutes));

    [Fact]
    public void Resets_and_token_counts_read_briefly()
    {
        var now = new DateTime(2026, 10, 30, 12, 0, 0);
        Assert.Equal("in 40 min", UsageMeters.ResetText(now.AddMinutes(39.5), now));
        Assert.StartsWith("at ", UsageMeters.ResetText(now.AddHours(3), now));
        Assert.Equal("now", UsageMeters.ResetText(now.AddMinutes(-1), now));

        Assert.Equal("950", UsageMeters.TokenText(950));
        Assert.Equal(1.2.ToString("0.#") + "K", UsageMeters.TokenText(1_234));
        Assert.Equal("45K", UsageMeters.TokenText(45_000));
        Assert.Equal(4.1.ToString("0.#") + "M", UsageMeters.TokenText(4_100_000));
    }

    [Fact]
    public void Opencodes_day_adds_up_steps_and_starts_over_the_next_day()
    {
        var day = new DailyUsage();
        var morning = new DateTime(2026, 10, 30, 9, 0, 0);

        (double cost, long tokens, long cached) = DailyUsage.ReadStep("""{"hook_event_name":"Usage","agent":"opencode","cost":0.01,"input":100,"output":20,"reasoning":5,"cache_read":1000,"cache_write":0}""")!.Value;
        day.Add(morning, cost, tokens, cached);
        day.Add(morning.AddHours(1), 0.02, 200, 0);

        Assert.True(day.HasToday(morning.AddHours(2)));
        Assert.Equal(0.03, day.Cost, 6);
        Assert.Equal(325, day.Tokens);
        Assert.Equal(1000, day.CachedTokens);
        Assert.Equal(2, day.Steps);

        DailyUsage again = DailyUsage.Load(day.Save());
        Assert.Equal(day.Tokens, again.Tokens);
        Assert.False(again.HasToday(morning.AddDays(1)));

        again.Add(morning.AddDays(1), 0.5, 10, 0);
        Assert.Equal(0.5, again.Cost, 6);
        Assert.Equal(1, again.Steps);

        Assert.Null(DailyUsage.ReadStep("""{"hook_event_name":"Stop"}"""));
        Assert.Equal(0, DailyUsage.Load("garbage").Steps);
    }
}
