using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Core.Agents;

/// <summary>How much of one of a plan's limits is used.</summary>
/// <param name="Label">Which limit: "5 hours", "Week".</param>
/// <param name="UsedPercent">0 to 100.</param>
/// <param name="ResetsAt">When it starts again from nothing, in UTC, when known.</param>
public sealed record UsageWindow(string Label, double UsedPercent, DateTime? ResetsAt);

/// <summary>An agent's plan limits as last read.</summary>
/// <param name="At">When the figures are from, in UTC.</param>
public sealed record UsageReading(IReadOnlyList<UsageWindow> Windows, DateTime At);

/// <summary>
/// Reads how much of their plans' limits Claude and Codex have used. Claude says it through the usage
/// address Claude Code itself asks, with the sign-in Claude Code keeps; Codex writes it into the
/// transcript of every session. Pure logic: the app reads the files and asks the address.
/// </summary>
public static class UsageMeters
{
    /// <summary>The address Claude Code asks for its plan's usage. Not a published API; it may change.</summary>
    public const string ClaudeUsageAddress = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The header value Claude's usage address wants with the sign-in.</summary>
    public const string ClaudeBeta = "oauth-2025-04-20";

    /// <summary>Claude's limits worth showing, in the order shown, and what to call them.</summary>
    private static readonly (string Key, string Label)[] ClaudeWindows =
    [
        ("five_hour", "5 hours"),
        ("seven_day", "Week"),
        ("seven_day_opus", "Week, Opus"),
        ("seven_day_sonnet", "Week, Sonnet"),
    ];

    /// <summary>Claude Code's sign-in from its <c>.credentials.json</c>: the token, and when it runs out (UTC).</summary>
    public static (string Token, DateTime? Expires)? ReadClaudeSignIn(string? credentialsJson)
    {
        JsonNode? oauth = Parse(credentialsJson)?["claudeAiOauth"];
        if (Text(oauth?["accessToken"]) is not { } token)
        {
            return null;
        }

        DateTime? expires = oauth?["expiresAt"] is JsonValue value && value.TryGetValue(out long ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
        return (token, expires);
    }

    /// <summary>What Claude's usage address answered. Limits the plan does not have come back empty and are left out.</summary>
    public static UsageReading? ReadClaude(string? json, DateTime now)
    {
        if (Parse(json) is not JsonObject root)
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        foreach ((string key, string label) in ClaudeWindows)
        {
            if (root[key] is JsonObject window && Number(window["utilization"]) is { } used)
            {
                windows.Add(Settled(new UsageWindow(label, used, Time(window["resets_at"])), now));
            }
        }

        return windows.Count == 0 ? null : new UsageReading(windows, now);
    }

    /// <summary>
    /// The latest limits a Codex transcript (<c>sessions\YYYY\MM\DD\rollout-*.jsonl</c>) holds: its last
    /// <c>token_count</c> event that has them. Lines that cannot be read are passed over.
    /// </summary>
    public static UsageReading? ReadCodex(IReadOnlyList<string> lines, DateTime now)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            JsonNode? node = Parse(lines[i]);
            if (Text(node?["type"]) != "event_msg" || node?["payload"] is not JsonObject payload || Text(payload["type"]) != "token_count"
                || payload["rate_limits"] is not JsonObject limits)
            {
                continue;
            }

            DateTime at = Time(node["timestamp"]) ?? now;
            var windows = new List<UsageWindow>();
            foreach (string key in new[] { "primary", "secondary" })
            {
                if (limits[key] is not JsonObject window || Number(window["used_percent"]) is not { } used)
                {
                    continue;
                }

                int? minutes = Number(window["window_minutes"]) is { } m ? (int)m : null;
                DateTime? resets = Number(window["resets_at"]) is { } seconds
                    ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime
                    : Number(window["resets_in_seconds"]) is { } inSeconds ? at.AddSeconds(inSeconds) : null;
                windows.Add(Settled(new UsageWindow(WindowName(minutes, key), used, resets), now));
            }

            if (windows.Count > 0)
            {
                return new UsageReading(windows, at);
            }
        }

        return null;
    }

    /// <summary>What to call a limit by its length, as Codex itself does; close enough counts.</summary>
    public static string WindowName(int? minutes, string fallbackKey = "primary")
    {
        (int Length, string Name)[] known = [(300, "5 hours"), (1440, "Day"), (10080, "Week"), (43200, "Month")];
        foreach ((int length, string name) in known)
        {
            if (minutes is { } m && Math.Abs(m - length) <= length * 0.05)
            {
                return name;
            }
        }

        return minutes is { } other ? $"{FormatMinutes(other)}" : fallbackKey == "primary" ? "Usage" : "Longer limit";
    }

    /// <summary>When a limit starts again, as short as it can be said: "in 40 min", "at 14:20", "Tue 09:00".</summary>
    public static string ResetText(DateTime resetsLocal, DateTime nowLocal)
    {
        TimeSpan left = resetsLocal - nowLocal;
        if (left <= TimeSpan.Zero)
        {
            return "now";
        }

        if (left < TimeSpan.FromHours(1))
        {
            return $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        if (resetsLocal.Date == nowLocal.Date)
        {
            return "at " + resetsLocal.ToString("t", culture);
        }

        return left < TimeSpan.FromDays(6)
            ? resetsLocal.ToString("ddd ", culture) + resetsLocal.ToString("t", culture)
            : resetsLocal.ToString("d MMM", culture);
    }

    /// <summary>A count of tokens the way people say it: 950, 12.3K, 4.1M.</summary>
    public static string TokenText(long tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(CultureInfo.CurrentCulture),
        < 1_000_000 => (tokens / 1_000d).ToString(tokens < 10_000 ? "0.#" : "0", CultureInfo.CurrentCulture) + "K",
        _ => (tokens / 1_000_000d).ToString(tokens < 10_000_000 ? "0.#" : "0", CultureInfo.CurrentCulture) + "M",
    };

    /// <summary>A limit whose reset has passed since it was read is back at nothing.</summary>
    private static UsageWindow Settled(UsageWindow window, DateTime now) =>
        window.ResetsAt is { } resets && resets <= now ? window with { UsedPercent = 0, ResetsAt = null } : window with { UsedPercent = Math.Clamp(window.UsedPercent, 0, 100) };

    private static string FormatMinutes(int minutes) =>
        minutes % 1440 == 0 ? $"{minutes / 1440} days" : minutes % 60 == 0 ? $"{minutes / 60} hours" : $"{minutes} min";

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static DateTime? Time(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) ? at.UtcDateTime : null;
}

/// <summary>
/// What opencode spent today, from what its plugin reports after each step: tokens and cost, which the
/// model's provider gives. Starts again at nothing each day. Kept in a small file so a restart of Notch
/// does not lose the day.
/// </summary>
public sealed class DailyUsage
{
    /// <summary>The day the figures are for, as yyyy-MM-dd in local time.</summary>
    public string Day { get; set; } = "";

    /// <summary>In US dollars, as opencode reports it.</summary>
    public double Cost { get; set; }

    /// <summary>Tokens read, written and reasoned, not counting what came from the provider's cache.</summary>
    public long Tokens { get; set; }

    public long CachedTokens { get; set; }

    public int Steps { get; set; }

    /// <summary>Adds one step's figures, starting the day over first when it is a new one.</summary>
    public void Add(DateTime localNow, double cost, long tokens, long cachedTokens)
    {
        Roll(localNow);
        Cost += Math.Max(0, cost);
        Tokens += Math.Max(0, tokens);
        CachedTokens += Math.Max(0, cachedTokens);
        Steps++;
    }

    /// <summary>Whether there is anything for today.</summary>
    public bool HasToday(DateTime localNow) => Day == DayOf(localNow) && Steps > 0;

    /// <summary>The usage line opencode's plugin sent, or null when it is not one.</summary>
    public static (double Cost, long Tokens, long Cached)? ReadStep(string? payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload) || JsonNode.Parse(payload) is not JsonObject step || (string?)step["hook_event_name"] != AgentHooks.UsageEvent)
            {
                return null;
            }

            double Get(string key) => step[key] is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : 0;
            return (Get("cost"), (long)(Get("input") + Get("output") + Get("reasoning")), (long)(Get("cache_read") + Get("cache_write")));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static DailyUsage Load(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new DailyUsage() : JsonSerializer.Deserialize<DailyUsage>(json) ?? new DailyUsage();
        }
        catch (JsonException)
        {
            return new DailyUsage();
        }
    }

    public string Save() => JsonSerializer.Serialize(this);

    private void Roll(DateTime localNow)
    {
        string today = DayOf(localNow);
        if (Day != today)
        {
            Day = today;
            Cost = 0;
            Tokens = 0;
            CachedTokens = 0;
            Steps = 0;
        }
    }

    private static string DayOf(DateTime localNow) => localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
