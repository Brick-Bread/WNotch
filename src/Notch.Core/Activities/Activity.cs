namespace Notch.Core.Activities;

/// <summary>Higher tiers win the pill. Ties go to the most recently published activity.</summary>
public enum ActivityTier
{
    /// <summary>Long-running background state: media playing, timer running, agent working.</summary>
    Ongoing = 0,

    /// <summary>Something wants the user: agent waiting for input, timer finished.</summary>
    Attention = 1,

    /// <summary>Short-lived HUD (volume, brightness, charging) that expires on its own.</summary>
    Transient = 2,
}

public sealed record Activity
{
    /// <summary>Stable per source, e.g. "media" or "hud.volume". Publishing the same id replaces it.</summary>
    public required string Id { get; init; }

    public required ActivityTier Tier { get; init; }

    public required string Title { get; init; }

    public string? Detail { get; init; }

    /// <summary>A Segoe Fluent Icons code point.</summary>
    public string? Glyph { get; init; }

    /// <summary>
    /// The coding agent this is about, by <see cref="Agents.AgentCatalog"/> id ("claude", "codex", ...). The pill
    /// shows that agent's logo in place of <see cref="Glyph"/> when it has one.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>What that agent is doing, for an activity about an agent; null otherwise.</summary>
    public Agents.AgentState? AgentState { get; init; }

    /// <summary>Encoded image bytes (PNG or JPEG) shown in place of <see cref="Glyph"/>, e.g. album art.</summary>
    public byte[]? Image { get; init; }

    /// <summary>0..1, or null when the activity has no meaningful progress.</summary>
    public double? Progress { get; init; }

    /// <summary>Light around the notch while this activity is on top; null for none.</summary>
    public Glow? Glow { get; init; }

    /// <summary>Only used by <see cref="ActivityTier.Transient"/>; defaults to <see cref="ActivityManager.DefaultTransientLifetime"/>.</summary>
    public TimeSpan? Lifetime { get; init; }

    /// <summary>
    /// For a plugin's activity: the <see cref="Plugins.PluginPage.Id"/> of one of the plugin's pages, which clicking the
    /// pill opens while this activity is showing, instead of Home. Null opens Home as usual. API version 9.
    /// </summary>
    public string? OpensPage { get; init; }

    /// <summary>
    /// A clock that counts up from this moment, shown in place of <see cref="Detail"/>. Notch keeps it ticking, so a
    /// running stopwatch needs no update each second. To pause, publish again with the time it stopped at as the
    /// detail and no clock. API version 9.
    /// </summary>
    public DateTimeOffset? CountingFrom { get; init; }
}
