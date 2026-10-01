namespace Notch.Core.Plugins;

/// <summary>A card together with the plugin that owns it.</summary>
public sealed record PluginCardEntry(string PluginId, PluginCard Card);

/// <summary>
/// Holds the cards of all plugins. Plugins write through <see cref="IPluginCards"/>; the shell
/// reads <see cref="Snapshot"/> whenever <see cref="Changed"/> fires.
/// </summary>
public sealed class PluginCardBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string PluginId, string CardId), Entry> _entries = [];
    private long _sequence;

    /// <summary>Raised on whichever thread caused the change.</summary>
    public event EventHandler? Changed;

    public void Set(string pluginId, PluginCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(card.Id);

        lock (_gate)
        {
            // An update keeps its place so a card that refreshes often does not jump around.
            var key = (pluginId, card.Id);
            long sequence = _entries.TryGetValue(key, out Entry? previous) ? previous.Sequence : ++_sequence;
            _entries[key] = new Entry(new PluginCardEntry(pluginId, card), sequence);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string pluginId, string cardId)
    {
        lock (_gate)
        {
            if (!_entries.Remove((pluginId, cardId)))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RemoveAll(string pluginId)
    {
        bool removed = false;
        lock (_gate)
        {
            foreach ((string PluginId, string CardId) key in _entries.Keys.Where(k => k.PluginId == pluginId).ToList())
            {
                removed |= _entries.Remove(key);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>In the order the cards were first added.</summary>
    public IReadOnlyList<PluginCardEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Values.OrderBy(e => e.Sequence).Select(e => e.Card)];
        }
    }

    private sealed record Entry(PluginCardEntry Card, long Sequence);
}
