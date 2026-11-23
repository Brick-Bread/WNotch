using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notch.Core.Deadlines;

/// <summary>
/// Every deadline the notch knows of: the user's own and those from links (kept in <c>deadlines.json</c>), those from
/// calendar feeds (replaced on every refresh) and those plugins set. Also remembers which of the calendar's and plugins'
/// deadlines the user completed, and which are snoozed. Safe to use from any thread.
/// </summary>
public sealed class DeadlineBoard
{
    /// <summary>The most deadlines of the user's own and from links together.</summary>
    public const int MaxOwn = 200;

    public const int MaxPerPlugin = 50;

    /// <summary>A completed calendar or plugin deadline is remembered this long after it was due.</summary>
    private static readonly TimeSpan HiddenKept = TimeSpan.FromDays(60);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Deadline> _own = [];
    private readonly Dictionary<string, Deadline> _plugins = [];
    private readonly HashSet<string> _hidden = [];
    private readonly Dictionary<string, DateTimeOffset> _snoozed = [];
    private IReadOnlyList<Deadline> _calendar = [];

    /// <param name="path">The file the user's own deadlines are kept in; null keeps them in memory only.</param>
    public DeadlineBoard(string? path = null, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        Load();
    }

    /// <summary>Raised on whichever thread made the change.</summary>
    public event EventHandler? Changed;

    /// <summary>Every deadline not completed, earliest due first, snoozed ones included.</summary>
    public IReadOnlyList<Deadline> All()
    {
        lock (_gate)
        {
            return [.. _own.Values.Concat(_calendar).Concat(_plugins.Values)
                .Where(d => !_hidden.Contains(d.Key))
                .OrderBy(d => d.Due)
                .ThenBy(d => d.Title, StringComparer.CurrentCulture)];
        }
    }

    /// <summary>Until when a deadline is snoozed; null when it is not.</summary>
    public DateTimeOffset? SnoozedUntil(Deadline deadline)
    {
        lock (_gate)
        {
            return _snoozed.TryGetValue(deadline.Key, out DateTimeOffset until) && until > _time.GetUtcNow() ? until : null;
        }
    }

    /// <summary>The deadline the pill shows: the earliest one that is not snoozed, overdue ones included.</summary>
    public Deadline? Next() => All().FirstOrDefault(d => SnoozedUntil(d) is null);

    public Deadline? Find(string id) => All().FirstOrDefault(d => d.Id == id);

    /// <summary>Adds one of the user's own. Null for an empty title or a full list.</summary>
    public Deadline? Add(string title, DateTimeOffset due)
    {
        title = Deadline.Tidy(title);
        if (title.Length == 0)
        {
            return null;
        }

        var deadline = new Deadline("own:" + Guid.NewGuid().ToString("N")[..10], title, due, DeadlineSource.Manual);
        lock (_gate)
        {
            if (_own.Count >= MaxOwn)
            {
                return null;
            }

            _own[deadline.Id] = deadline;
        }

        SaveAndNotify();
        return deadline;
    }

    /// <summary>Changes the title and due time of one of the user's own or a link's. False for any other.</summary>
    public bool Edit(string id, string title, DateTimeOffset due)
    {
        title = Deadline.Tidy(title);
        lock (_gate)
        {
            if (title.Length == 0 || !_own.TryGetValue(id, out Deadline? deadline))
            {
                return false;
            }

            _snoozed.Remove(deadline.Key);
            _own[id] = deadline with { Title = title, Due = due };
        }

        SaveAndNotify();
        return true;
    }

    /// <summary>
    /// Ticks a deadline off. The user's own and links' are deleted; the calendar's and plugins' are hidden until
    /// their source moves them to another time.
    /// </summary>
    public bool Complete(string id)
    {
        lock (_gate)
        {
            if (_own.Remove(id, out Deadline? own))
            {
                _snoozed.Remove(own.Key);
            }
            else if (_calendar.Concat(_plugins.Values).FirstOrDefault(d => d.Id == id) is { } other)
            {
                _hidden.Add(other.Key);
                _snoozed.Remove(other.Key);
            }
            else
            {
                return false;
            }
        }

        SaveAndNotify();
        return true;
    }

    /// <summary>Keeps a deadline out of the pill and its alerts until <paramref name="until"/>; it stays in the list.</summary>
    public bool Snooze(string id, DateTimeOffset until)
    {
        lock (_gate)
        {
            Deadline? deadline = _own.Values.Concat(_calendar).Concat(_plugins.Values).FirstOrDefault(d => d.Id == id);
            if (deadline is null)
            {
                return false;
            }

            if (until <= _time.GetUtcNow())
            {
                _snoozed.Remove(deadline.Key);
            }
            else
            {
                _snoozed[deadline.Key] = until;
            }
        }

        SaveAndNotify();
        return true;
    }

    /// <summary>
    /// Adds or moves a deadline from a link or <c>notchctl</c>. The title is its key, so a script that sets the same
    /// deadline again moves it rather than adding a second one.
    /// </summary>
    public Deadline? SetLinked(string title, DateTimeOffset due, string? detail = null)
    {
        title = Deadline.Tidy(title);
        if (title.Length == 0)
        {
            return null;
        }

        detail = Deadline.Tidy(detail, Deadline.MaxDetailLength) is { Length: > 0 } d ? d : null;
        string id = LinkedId(title);
        var deadline = new Deadline(id, title, due, DeadlineSource.Link, detail);
        lock (_gate)
        {
            if (!_own.ContainsKey(id) && _own.Count >= MaxOwn)
            {
                return null;
            }

            _own[id] = deadline;
        }

        SaveAndNotify();
        return deadline;
    }

    /// <summary>Removes a deadline a link set, by its title. False when there is none.</summary>
    public bool RemoveLinked(string title) => Complete(LinkedId(Deadline.Tidy(title)));

    /// <summary>The calendar's deadlines after a refresh, replacing the last ones.</summary>
    public void SetCalendar(IEnumerable<Deadline> deadlines)
    {
        IReadOnlyList<Deadline> list = [.. deadlines];
        lock (_gate)
        {
            if (list.SequenceEqual(_calendar))
            {
                return;
            }

            _calendar = list;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds or replaces a plugin's deadline. False for an empty title, or when the plugin already has as many as it may.</summary>
    public bool SetPlugin(string pluginId, PluginDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadline.Id);
        string title = Deadline.Tidy(deadline.Title);
        if (title.Length == 0)
        {
            return false;
        }

        string id = PluginKey(pluginId, deadline.Id);
        string? detail = Deadline.Tidy(deadline.Detail, Deadline.MaxDetailLength) is { Length: > 0 } d ? d : null;
        var entry = new Deadline(id, title, deadline.Due, DeadlineSource.Plugin, detail, pluginId);
        lock (_gate)
        {
            if (_plugins.TryGetValue(id, out Deadline? same) && same == entry)
            {
                return true;
            }

            if (!_plugins.ContainsKey(id) && _plugins.Values.Count(p => p.Owner == pluginId) >= MaxPerPlugin)
            {
                return false;
            }

            _plugins[id] = entry;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool RemovePlugin(string pluginId, string id)
    {
        lock (_gate)
        {
            if (!_plugins.Remove(PluginKey(pluginId, id)))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes all of a plugin's deadlines, e.g. when it stops.</summary>
    public void ClearPlugin(string pluginId)
    {
        bool removed = false;
        lock (_gate)
        {
            foreach (string id in _plugins.Values.Where(p => p.Owner == pluginId).Select(p => p.Id).ToList())
            {
                removed |= _plugins.Remove(id);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string LinkedId(string title) => "link:" + title.ToLowerInvariant();

    private static string PluginKey(string pluginId, string id) => $"plugin:{pluginId}/{id}";

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            Stored? stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json);
            DateTimeOffset now = _time.GetUtcNow();
            foreach (Deadline? deadline in stored?.Deadlines ?? [])
            {
                if (deadline is { Id.Length: > 0 } && Deadline.Tidy(deadline.Title) is { Length: > 0 } title
                    && deadline.Source is DeadlineSource.Manual or DeadlineSource.Link && _own.Count < MaxOwn)
                {
                    _own[deadline.Id] = deadline with { Title = title };
                }
            }

            foreach (string key in stored?.Hidden ?? [])
            {
                if (DueOfKey(key) is { } due && now - due < HiddenKept)
                {
                    _hidden.Add(key);
                }
            }

            foreach ((string key, DateTimeOffset until) in stored?.Snoozed ?? [])
            {
                if (until > now)
                {
                    _snoozed[key] = until;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A broken file starts the list afresh; the next save replaces it.
        }
    }

    private static DateTimeOffset? DueOfKey(string key) =>
        key.LastIndexOf('@') is var at and > 0 && long.TryParse(key.AsSpan(at + 1), out long ticks) && ticks is > 0 and < 3155378975999999999
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(
                new Stored([.. _own.Values.OrderBy(d => d.Due)], [.. _hidden], new Dictionary<string, DateTimeOffset>(_snoozed)),
                Json);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; the next change tries again.
        }
    }

    private sealed record Stored(List<Deadline?>? Deadlines, List<string>? Hidden, Dictionary<string, DateTimeOffset>? Snoozed);
}
