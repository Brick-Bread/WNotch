using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class AgentChimeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AgentEntry E(string key, AgentState state, AgentSource source = AgentSource.Detected) =>
        new(key, "claude", "Claude", "", state, "app", source);

    [Fact]
    public void Agents_already_waiting_when_notch_first_looks_make_no_sound()
    {
        var chimes = new AgentChimes();

        Assert.Null(chimes.Update([E("a", AgentState.Done), E("b", AgentState.NeedsInput)], T0));
    }

    [Fact]
    public void Finishing_and_waiting_each_make_their_sound()
    {
        var chimes = new AgentChimes();
        chimes.Update([E("a", AgentState.Working)], T0);

        Assert.Equal(AgentChime.Done, chimes.Update([E("a", AgentState.Done)], T0.AddSeconds(10)));
        Assert.Null(chimes.Update([E("a", AgentState.Done)], T0.AddSeconds(20)));
        chimes.Update([E("a", AgentState.Working)], T0.AddSeconds(30));
        Assert.Equal(AgentChime.NeedsInput, chimes.Update([E("a", AgentState.NeedsInput)], T0.AddSeconds(40)));
    }

    [Fact]
    public void Waiting_wins_over_done_and_sounds_close_together_are_one()
    {
        var chimes = new AgentChimes();
        chimes.Update([E("a", AgentState.Working), E("b", AgentState.Working), E("c", AgentState.Working)], T0);

        Assert.Equal(AgentChime.NeedsInput, chimes.Update([E("a", AgentState.Done), E("b", AgentState.NeedsInput), E("c", AgentState.Working)], T0.AddSeconds(5)));
        Assert.Null(chimes.Update([E("a", AgentState.Done), E("b", AgentState.NeedsInput), E("c", AgentState.Done)], T0.AddSeconds(6)));
        Assert.Equal(AgentChime.Done, chimes.Update([E("a", AgentState.Done), E("b", AgentState.Working), E("c", AgentState.Idle), E("d", AgentState.Done)], T0.AddSeconds(30)));
    }

    [Fact]
    public void A_session_that_turns_up_already_waiting_after_the_first_look_counts()
    {
        var chimes = new AgentChimes();
        chimes.Update([], T0);

        Assert.Equal(AgentChime.NeedsInput, chimes.Update([E("new", AgentState.NeedsInput)], T0.AddSeconds(5)));
        Assert.Null(chimes.Update([E("idle", AgentState.Idle)], T0.AddSeconds(10)));
    }
}
