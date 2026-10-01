namespace Notch.Core.Activities;

/// <summary>
/// Owns everything the notch can show. Sources publish and remove activities; the shell
/// reads <see cref="Snapshot"/> whenever <see cref="Changed"/> fires.
/// </summary>
public sealed class ActivityManager : IDisposable
{
    public static readonly TimeSpan DefaultTransientLifetime = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private long _sequence;
    private bool _disposed;

    public ActivityManager(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised on whichever thread caused the change, including timer threads.</summary>
    public event EventHandler? Changed;

    public void Publish(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // An update keeps its place in line so a ticking progress value does not reorder the pill.
            long sequence = _entries.Remove(activity.Id, out Entry? previous) ? previous.Sequence : ++_sequence;
            previous?.Expiry?.Dispose();

            var entry = new Entry(activity, sequence);
            if (activity.Tier == ActivityTier.Transient)
            {
                TimeSpan lifetime = activity.Lifetime ?? DefaultTransientLifetime;
                entry.Expiry = _time.CreateTimer(_ => Expire(entry), null, lifetime, Timeout.InfiniteTimeSpan);
            }

            _entries[activity.Id] = entry;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (!_entries.Remove(id, out Entry? entry))
            {
                return false;
            }

            entry.Expiry?.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Highest tier first, then most recently published first.</summary>
    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Values
                .OrderByDescending(e => e.Activity.Tier)
                .ThenByDescending(e => e.Sequence)
                .Select(e => e.Activity)];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Entry entry in _entries.Values)
            {
                entry.Expiry?.Dispose();
            }

            _entries.Clear();
        }
    }

    private void Expire(Entry entry)
    {
        lock (_gate)
        {
            // The id may have been republished since this timer was armed.
            if (!_entries.TryGetValue(entry.Activity.Id, out Entry? current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            _entries.Remove(entry.Activity.Id);
            entry.Expiry?.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Entry(Activity activity, long sequence)
    {
        public Activity Activity { get; } = activity;

        public long Sequence { get; } = sequence;

        public ITimer? Expiry { get; set; }
    }
}
