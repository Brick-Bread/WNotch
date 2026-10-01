using System.Text.Json;
using Notch.Core.Activities;

namespace Notch.Core.Plugins;

/// <summary>
/// One plugin's view of Notch. Everything the plugin does goes through here, scoped to its id,
/// so <see cref="Close"/> can undo all of it when the plugin stops or fails.
/// </summary>
internal sealed class PluginHost : IPluginHost
{
    private readonly string _dataDirectory;
    private readonly ScopedActivities _activities;
    private readonly ScopedCards _cards;

    public PluginHost(
        PluginManifest manifest,
        string pluginDirectory,
        string dataDirectory,
        ActivityManager activities,
        PluginCardBoard cards,
        PluginLog log)
    {
        Manifest = manifest;
        PluginDirectory = pluginDirectory;
        _dataDirectory = dataDirectory;

        var scopedLog = new ScopedLog(manifest.Id, log);
        Log = scopedLog;
        Activities = _activities = new ScopedActivities(manifest.Id, activities);
        Cards = _cards = new ScopedCards(manifest.Id, cards, scopedLog);
        Settings = new PluginSettingsStore(Path.Combine(dataDirectory, "settings.json"));
    }

    public PluginManifest Manifest { get; }

    public string PluginDirectory { get; }

    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(_dataDirectory);
            return _dataDirectory;
        }
    }

    public IPluginActivities Activities { get; }

    public IPluginCards Cards { get; }

    public IPluginSettings Settings { get; }

    public IPluginLog Log { get; }

    /// <summary>Removes what the plugin is showing and ignores whatever it publishes from now on.</summary>
    public void Close()
    {
        _activities.Close();
        _cards.Close();
    }

    private sealed class ScopedActivities(string pluginId, ActivityManager manager) : IPluginActivities
    {
        private readonly Lock _gate = new();
        private readonly HashSet<string> _ids = [];
        private bool _closed;

        public void Publish(Activity activity)
        {
            ArgumentNullException.ThrowIfNull(activity);
            ArgumentException.ThrowIfNullOrWhiteSpace(activity.Id);

            // Published under the lock so a publish racing with Close cannot outlive it.
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                string id = PluginManager.ActivityIdFor(pluginId, activity.Id);
                _ids.Add(id);
                manager.Publish(activity with { Id = id });
            }
        }

        public bool Remove(string id)
        {
            lock (_gate)
            {
                string scoped = PluginManager.ActivityIdFor(pluginId, id);
                _ids.Remove(scoped);
                return manager.Remove(scoped);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                foreach (string id in _ids)
                {
                    manager.Remove(id);
                }

                _ids.Clear();
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Clear();
            }
        }
    }

    private sealed class ScopedCards(string pluginId, PluginCardBoard board, ScopedLog log) : IPluginCards
    {
        private readonly Lock _gate = new();
        private bool _closed;

        public void Set(PluginCard card)
        {
            ArgumentNullException.ThrowIfNull(card);
            ArgumentException.ThrowIfNullOrWhiteSpace(card.Id);

            lock (_gate)
            {
                if (!_closed)
                {
                    board.Set(pluginId, card with { Clicked = Guard(card) });
                }
            }
        }

        public bool Remove(string id) => board.Remove(pluginId, id);

        public void Clear() => board.RemoveAll(pluginId);

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Clear();
            }
        }

        /// <summary>The shell calls this on the UI thread; plugin code runs elsewhere and cannot throw into it.</summary>
        private Action? Guard(PluginCard card)
        {
            if (card.Clicked is not { } clicked)
            {
                return null;
            }

            return () => Task.Run(() =>
            {
                try
                {
                    clicked();
                }
                catch (Exception e)
                {
                    log.Error($"The click handler of card '{card.Id}' failed.", e);
                }
            });
        }
    }

    private sealed class ScopedLog(string pluginId, PluginLog log) : IPluginLog
    {
        public void Info(string message) => log.Write(pluginId, "info", message);

        public void Warn(string message) => log.Write(pluginId, "warn", message);

        public void Error(string message, Exception? exception = null) => log.Write(pluginId, "error", message, exception);
    }
}

/// <summary>A plugin's <c>settings.json</c>: a flat JSON object, read on first use and rewritten on every change.</summary>
internal sealed class PluginSettingsStore(string filePath) : IPluginSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private Dictionary<string, JsonElement>? _values;

    public T Get<T>(string key, T fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (!Load().TryGetValue(key, out JsonElement element))
            {
                return fallback;
            }

            try
            {
                return element.Deserialize<T>(Options) ?? fallback;
            }
            catch (JsonException)
            {
                // Hand-edited to something of the wrong shape.
                return fallback;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            Load()[key] = JsonSerializer.SerializeToElement(value, Options);
            Save();
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!Load().Remove(key))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    private Dictionary<string, JsonElement> Load()
    {
        if (_values is not null)
        {
            return _values;
        }

        try
        {
            if (File.Exists(filePath))
            {
                _values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(filePath), Options);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing or corrupt file yields defaults, like the app's own settings.
        }

        return _values ??= [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            // Write to a temporary file first so a crash cannot leave a half-written settings file.
            string temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, Options));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The value still applies for this run; it just will not survive a restart.
        }
    }
}
