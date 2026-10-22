using Notch.Core.Activities;

namespace Notch.Core.Agents;

public static class AgentActivities
{
    public static string IdFor(string sessionId) => "agent." + sessionId;

    /// <summary>
    /// The pill activity for an agent session, or null when it has nothing to report. Its title is the display name
    /// and, after " · ", the folder.
    /// </summary>
    /// <param name="folderName">The folder the session works in, to tell two sessions of the same agent apart.</param>
    /// <param name="others">How many other sessions also have something to report; the pill only has room for one.</param>
    /// <param name="agentId">Which agent it is (<see cref="AgentCatalog"/> id), so the pill can show its logo.</param>
    public static Activity? For(
        string sessionId, string displayName, string glyph, AgentState state, string? folderName = null, int others = 0, string? agentId = null)
    {
        string title = string.IsNullOrEmpty(folderName) ? displayName : $"{displayName} · {folderName}";
        return state switch
        {
            AgentState.Working => Make(ActivityTier.Ongoing, "Working", new Glow(GlowColor.Cyan, GlowPattern.Breathe, 0.8)),
            AgentState.NeedsInput => Make(ActivityTier.Attention, "Needs input", new Glow(GlowColor.Amber, GlowPattern.Pulse)),
            AgentState.Done => Make(ActivityTier.Attention, "Done", new Glow(GlowColor.Green, GlowPattern.Steady, 0.8)),
            _ => null,
        };

        Activity Make(ActivityTier tier, string detail, Glow glow) => new()
        {
            Id = IdFor(sessionId),
            Tier = tier,
            Title = title,
            Detail = others > 0 ? $"{detail} · +{others}" : detail,
            Glyph = glyph,
            AgentId = agentId,
            AgentState = state,
            Glow = glow,
        };
    }
}
