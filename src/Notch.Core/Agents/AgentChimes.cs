namespace Notch.Core.Agents;

/// <summary>The sounds Notch can play for agents.</summary>
public enum AgentChime
{
    /// <summary>An agent finished its turn.</summary>
    Done,

    /// <summary>An agent waits for the user: a permission, a question.</summary>
    NeedsInput,
}

/// <summary>
/// Decides when an agent sound plays, from what the <see cref="AgentBoard"/> shows. A sound plays when
/// a session moves into "Done" or "Needs input", not for sessions that were already there at the
/// first look (Notch starting while an agent waits), and not more than once in a short while, so
/// several agents finishing together make one sound. Pure logic: playing it is the app's job.
/// </summary>
public sealed class AgentChimes
{
    /// <summary>The least time between two sounds.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, AgentState> _last = [];
    private DateTime _lastPlayed = DateTime.MinValue;
    private bool _primed;

    /// <summary>
    /// Takes the board as it is now and returns the sound to play, or null. "Needs input" wins over
    /// "Done" when both happen at once, since it is the one that holds an agent up.
    /// </summary>
    public AgentChime? Update(IReadOnlyList<AgentEntry> entries, DateTime now)
    {
        bool done = false;
        bool needsInput = false;
        var seen = new HashSet<string>();
        foreach (AgentEntry entry in entries)
        {
            seen.Add(entry.Key);
            bool known = _last.TryGetValue(entry.Key, out AgentState before);
            _last[entry.Key] = entry.State;

            // What was there at the first look was not just now changed. Later, a session that turns up
            // already waiting (its hook came before the scan that finds it) did change, from nothing.
            if ((!known && !_primed) || before == entry.State)
            {
                continue;
            }

            done |= entry.State == AgentState.Done;
            needsInput |= entry.State == AgentState.NeedsInput;
        }

        foreach (string gone in _last.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _last.Remove(gone);
        }

        _primed = true;
        AgentChime? chime = needsInput ? AgentChime.NeedsInput : done ? AgentChime.Done : null;
        if (chime is null || now - _lastPlayed < Gap)
        {
            return null;
        }

        _lastPlayed = now;
        return chime;
    }
}
