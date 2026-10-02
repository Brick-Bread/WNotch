namespace Notch.Core.Shelf;

/// <summary>
/// Files and folders the user has put aside on the notch, to drag out again later. It holds
/// their paths, not copies: the shell reads <see cref="Snapshot"/> whenever <see cref="Changed"/> fires.
/// </summary>
public sealed class FileShelf
{
    /// <summary>As many as the shelf keeps; adding more drops the ones put there longest ago.</summary>
    public const int Capacity = 24;

    private readonly Lock _gate = new();
    private readonly List<string> _paths = [];

    /// <param name="paths">What the shelf held when it was last saved, newest first.</param>
    public FileShelf(IEnumerable<string>? paths = null)
    {
        _paths.AddRange(Tidy(paths ?? []).Take(Capacity));
    }

    /// <summary>Raised on whichever thread caused the change.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Puts paths at the front of the shelf, in the order given. One that is already there moves
    /// to the front instead of showing twice.
    /// </summary>
    public void Add(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        List<string> added = Tidy(paths);
        if (added.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            List<string> next = [.. added, .. _paths.Where(path => !added.Contains(path, StringComparer.OrdinalIgnoreCase))];
            if (next.Count > Capacity)
            {
                next.RemoveRange(Capacity, next.Count - Capacity);
            }

            if (next.SequenceEqual(_paths, StringComparer.Ordinal))
            {
                return;
            }

            _paths.Clear();
            _paths.AddRange(next);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            if (_paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_paths.Count == 0)
            {
                return;
            }

            _paths.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops what is no longer there, such as a file that was moved or deleted.</summary>
    /// <param name="exists">Whether a path still leads to a file or folder. May be slow; the shelf is not held up while it runs.</param>
    public void Prune(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        string[] gone = [.. Snapshot().Where(path => !exists(path))];
        if (gone.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_paths.RemoveAll(path => gone.Contains(path, StringComparer.OrdinalIgnoreCase)) == 0)
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _paths];
        }
    }

    private static List<string> Tidy(IEnumerable<string> paths) => [.. paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)];
}
