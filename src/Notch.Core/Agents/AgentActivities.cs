using Notch.Core.Activities;

namespace Notch.Core.Agents;

public static class AgentActivities
{
    public static string IdFor(string sessionId) => "agent." + sessionId;

    /// <summary>The pill activity for an agent session, or null when it has nothing to report.</summary>
    public static Activity? For(string sessionId, string displayName, string glyph, AgentState state) => state switch
    {
        AgentState.Working => Make(sessionId, displayName, glyph, ActivityTier.Ongoing, "Working"),
        AgentState.NeedsInput => Make(sessionId, displayName, glyph, ActivityTier.Attention, "Needs input"),
        AgentState.Done => Make(sessionId, displayName, glyph, ActivityTier.Attention, "Done"),
        _ => null,
    };

    private static Activity Make(string sessionId, string displayName, string glyph, ActivityTier tier, string detail) => new()
    {
        Id = IdFor(sessionId),
        Tier = tier,
        Title = displayName,
        Detail = detail,
        Glyph = glyph,
    };
}
