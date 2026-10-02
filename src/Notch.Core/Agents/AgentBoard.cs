using Notch.Core.Activities;

namespace Notch.Core.Agents;

public enum AgentSource
{
    /// <summary>Started from Notch's own terminal.</summary>
    Notch,

    /// <summary>Found running somewhere else on the computer.</summary>
    Detected,
}

/// <summary>One agent session as the Home tab and the pill show it.</summary>
/// <param name="Key">Unique among entries: the terminal session id, or the key of a detected agent.</param>
/// <param name="Host">Where it runs, for a detected agent: "Windows Terminal", "VS Code", ...</param>
/// <param name="Pid">The agent's process id, for a detected agent.</param>
public sealed record AgentEntry(
    string Key,
    string AgentId,
    string DisplayName,
    string Glyph,
    AgentState State,
    string? FolderName,
    AgentSource Source,
    int? Pid = null,
    string? Host = null)
{
    /// <summary>What the Home list says: the state, or "Open" for an agent that is there but reports nothing.</summary>
    public string StateText => State switch
    {
        AgentState.Working => "Working",
        AgentState.NeedsInput => "Needs input",
        AgentState.Done => "Done",
        _ => Source == AgentSource.Detected ? "Open" : "Idle",
    };
}

/// <summary>Every agent session there is, from every source. Writers update it; the pill and the Home tab read it.</summary>
public sealed class AgentBoard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentEntry> _entries = [];
    private readonly List<string> _order = [];

    /// <summary>Raised on whichever thread changed the board, after the change.</summary>
    public event Action? Changed;

    /// <summary>Adds an entry or replaces the one with the same key. Keeps its place in the list.</summary>
    public void Set(AgentEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out AgentEntry? old) && old == entry)
            {
                return;
            }

            if (!_entries.ContainsKey(entry.Key))
            {
                _order.Add(entry.Key);
            }

            _entries[entry.Key] = entry;
        }

        Changed?.Invoke();
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key))
            {
                return;
            }

            _order.Remove(key);
        }

        Changed?.Invoke();
    }

    /// <summary>Removes every entry of a source whose key is not in <paramref name="keep"/>.</summary>
    public void KeepOnly(AgentSource source, IReadOnlySet<string> keep)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (string key in _order.ToArray())
            {
                if (_entries[key].Source == source && !keep.Contains(key))
                {
                    _entries.Remove(key);
                    _order.Remove(key);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The entries in the order they appeared.</summary>
    public IReadOnlyList<AgentEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _order.Select(key => _entries[key])];
        }
    }

    public AgentEntry? Find(string key)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(key);
        }
    }
}

/// <summary>
/// Puts what is on an <see cref="AgentBoard"/> in the pill. Every entry is published, not just
/// the one that changed: each says how many others have something to report, since the pill shows one.
/// </summary>
public sealed class AgentPublisher : IDisposable
{
    private readonly ActivityManager _activities;
    private readonly AgentBoard _board;
    private readonly HashSet<string> _published = [];
    private readonly object _gate = new();

    public AgentPublisher(ActivityManager activities, AgentBoard board)
    {
        _activities = activities;
        _board = board;
        _board.Changed += Publish;
    }

    public void Dispose() => _board.Changed -= Publish;

    private void Publish()
    {
        lock (_gate)
        {
            IReadOnlyList<AgentEntry> entries = _board.Snapshot();
            int reporting = entries.Count(e => e.State != AgentState.Idle);
            var now = new HashSet<string>();
            foreach (AgentEntry entry in entries)
            {
                Activity? activity = AgentActivities.For(
                    entry.Key, entry.DisplayName, entry.Glyph, entry.State, entry.FolderName, reporting - 1);
                if (activity is not null)
                {
                    now.Add(entry.Key);
                    _activities.Publish(activity);
                }
            }

            foreach (string gone in _published.Except(now))
            {
                _activities.Remove(AgentActivities.IdFor(gone));
            }

            _published.Clear();
            _published.UnionWith(now);
        }
    }
}
