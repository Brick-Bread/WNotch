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
    private readonly ScopedPages _pages;
    private readonly ScopedThemes _themes;
    private readonly ScopedGlow _glow;
    private readonly ScopedAudio _audio;
    private readonly ScopedShell _shell;
    private readonly ScopedBus _bus;

    public PluginHost(
        PluginManifest manifest,
        string pluginDirectory,
        string dataDirectory,
        ActivityManager activities,
        PluginCardBoard cards,
        PluginPageBoard pages,
        PluginThemeBoard themes,
        PluginGlowBoard glow,
        PluginAudioHub audio,
        PluginShellState shell,
        PluginBus bus,
        PluginLog log)
    {
        Manifest = manifest;
        PluginDirectory = pluginDirectory;
        _dataDirectory = dataDirectory;

        var scopedLog = new ScopedLog(manifest.Id, log);
        Log = scopedLog;
        Activities = _activities = new ScopedActivities(manifest.Id, activities);
        Cards = _cards = new ScopedCards(manifest.Id, cards, scopedLog);
        Pages = _pages = new ScopedPages(manifest.Id, pages, scopedLog);
        Themes = _themes = new ScopedThemes(manifest.Id, pluginDirectory, themes);
        Glow = _glow = new ScopedGlow(manifest.Id, glow);
        Audio = _audio = new ScopedAudio(audio);
        Shell = _shell = new ScopedShell(manifest.Id, shell, Activities);
        Bus = _bus = new ScopedBus(manifest.Id, bus, scopedLog);
        Settings = SettingsStore = new PluginSettingsStore(Path.Combine(dataDirectory, "settings.json"));
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

    public IPluginPages Pages { get; }

    public IPluginThemes Themes { get; }

    public IPluginGlow Glow { get; }

    public IPluginAudio Audio { get; }

    public IPluginShell Shell { get; }

    public IPluginBus Bus { get; }

    public IPluginSettings Settings { get; }

    /// <summary>The same object as <see cref="Settings"/>, with the parts only Notch itself uses.</summary>
    internal PluginSettingsStore SettingsStore { get; }

    public IPluginLog Log { get; }

    /// <summary>Removes what the plugin is showing and ignores whatever it publishes from now on.</summary>
    public void Close()
    {
        _activities.Close();
        _cards.Close();
        _pages.Close();
        _themes.Close();
        _glow.Close();
        _audio.Close();
        _bus.Close();
        _shell.Close();
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

    private sealed class ScopedThemes(string pluginId, string pluginDirectory, PluginThemeBoard board) : IPluginThemes
    {
        private readonly Lock _gate = new();
        private bool _closed;

        public void Set(PluginTheme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            ArgumentException.ThrowIfNullOrWhiteSpace(theme.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(theme.Name);
            if (theme.Id.Contains('/'))
            {
                throw new ArgumentException("A theme id cannot contain a slash.", nameof(theme));
            }

            string path = ResolveFile(theme.File);
            lock (_gate)
            {
                if (!_closed)
                {
                    board.Set(new PluginThemeEntry(pluginId, theme.Id, theme.Name.Trim(), theme.Description, theme.Base, path));
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

        /// <summary>The full path of a .xaml file inside the plugin's folder; anything else is refused, so a theme cannot point at other files.</summary>
        private string ResolveFile(string? file)
        {
            if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) || !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A theme's file must be a relative path to a .xaml file in the plugin's folder.", nameof(file));
            }

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDirectory)) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, file));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A theme's file must be inside the plugin's folder.", nameof(file));
            }

            return full;
        }
    }

    private sealed class ScopedGlow(string pluginId, PluginGlowBoard board) : IPluginGlow
    {
        private readonly Lock _gate = new();
        private bool _closed;

        public void Set(GlowFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);

            lock (_gate)
            {
                if (!_closed)
                {
                    board.Set(pluginId, frame);
                }
            }
        }

        public void Clear() => board.Remove(pluginId);

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Clear();
            }
        }
    }

    private sealed class ScopedAudio(PluginAudioHub hub) : IPluginAudio
    {
        private readonly Lock _gate = new();
        private IDisposable? _reader;
        private bool _closed;

        public AudioFrame Latest
        {
            get
            {
                lock (_gate)
                {
                    if (_closed)
                    {
                        return AudioFrame.Silent;
                    }

                    // The first read is what starts the listening.
                    _reader ??= hub.Acquire();
                }

                return hub.Latest;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _reader?.Dispose();
                _reader = null;
            }
        }
    }

    private sealed class ScopedPages(string pluginId, PluginPageBoard board, ScopedLog log) : IPluginPages
    {
        private readonly Lock _gate = new();
        private bool _closed;

        public void Set(PluginPage page)
        {
            ArgumentNullException.ThrowIfNull(page);

            lock (_gate)
            {
                if (!_closed)
                {
                    board.Set(
                        pluginId,
                        page with
                        {
                            Input = Guard(page),
                            Back = page.Back is { } back ? () => Run(page.Id, back) : null,
                            Choices = [.. page.Choices.Select(c => c with { Clicked = c.Clicked is { } clicked ? () => Run(page.Id, clicked) : null })],
                            Actions = GuardActions(page.Id, page.Actions),
                            Blocks = [.. page.Blocks.Take(PluginPageBoard.MaxBlocks).Select(b => GuardBlock(page.Id, b))],
                        });
                }
            }
        }

        public void Append(string pageId, string line)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    board.Append(pluginId, pageId, line);
                }
            }
        }

        public void ClearConsole(string pageId)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    board.ClearConsole(pluginId, pageId);
                }
            }
        }

        public void Open(string pageId)
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    board.RequestOpen(pluginId, pageId);
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

        private PluginAction[] GuardActions(string pageId, IReadOnlyList<PluginAction> actions) =>
            [.. actions.Select(a => a with { Clicked = a.Clicked is { } clicked ? () => Run(pageId, clicked) : null })];

        /// <summary>Wraps the callbacks of a block, the same way as the page's own.</summary>
        private PluginBlock GuardBlock(string pageId, PluginBlock block) => block switch
        {
            PluginButtons b => b with { Actions = GuardActions(pageId, b.Actions) },
            PluginToggle b => b with { Changed = b.Changed is { } changed ? value => Run(pageId, () => changed(value)) : null },
            PluginSlider b => b with { Changed = b.Changed is { } changed ? value => Run(pageId, () => changed(value)) : null },
            PluginSelect b => b with { Changed = b.Changed is { } changed ? value => Run(pageId, () => changed(value)) : null },
            PluginTextField b => b with { Submitted = b.Submitted is { } submitted ? value => Run(pageId, () => submitted(value)) : null },
            _ => block,
        };

        private void Run(string pageId, Action action) => Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                log.Error($"A click handler of page '{pageId}' failed.", e);
            }
        });

        /// <summary>The shell calls this on the UI thread; plugin code runs elsewhere and cannot throw into it.</summary>
        private Action<string>? Guard(PluginPage page)
        {
            if (page.Input is not { } input)
            {
                return null;
            }

            return text => Task.Run(() =>
            {
                try
                {
                    input(text);
                }
                catch (Exception e)
                {
                    log.Error($"The input handler of page '{page.Id}' failed.", e);
                }
            });
        }
    }

    private sealed class ScopedShell : IPluginShell
    {
        private readonly string _pluginId;
        private readonly PluginShellState _state;
        private readonly IPluginActivities _activities;

        public ScopedShell(string pluginId, PluginShellState state, IPluginActivities activities)
        {
            _pluginId = pluginId;
            _state = state;
            _activities = activities;
            _state.Changed += OnChanged;
        }

        public void OpenSettings()
        {
            Action? open = _state.OpenSettings;
            if (open is not null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        open();
                    }
                    catch (Exception)
                    {
                        // The window could not be shown; there is nothing more to tell the plugin.
                    }
                });
            }
        }

        public bool IsExpanded => _state.IsExpanded;

        public bool IsPageVisible(string pageId) => _state.IsPageVisible(_pluginId, pageId);

        public bool AreCardsVisible => _state.CardsVisible;

        public bool IsDark => _state.IsDark;

        public GlowColor? Accent => _state.Accent;

        public event EventHandler? Changed;

        public void Notify(string title, string? detail = null, string? glyph = null, GlowColor? color = null, TimeSpan? lifetime = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);

            // Removed first so a notice right after another counts as new and gets its full time.
            _activities.Remove("notice");
            _activities.Publish(new Activity
            {
                Id = "notice",
                Tier = ActivityTier.Transient,
                Title = title,
                Detail = detail,
                Glyph = glyph ?? "",
                Glow = color is { } c ? new Glow(c, GlowPattern.Flash) : null,
                Lifetime = lifetime ?? TimeSpan.FromSeconds(4),
            });
        }

        public void Close() => _state.Changed -= OnChanged;

        private void OnChanged(object? sender, EventArgs e)
        {
            EventHandler? handler = Changed;
            if (handler is null)
            {
                return;
            }

            // Off the shell's thread, and a handler that throws must not reach it.
            _ = Task.Run(() =>
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // The plugin's own bug; nothing here can report it usefully.
                }
            });
        }
    }

    private sealed class ScopedBus(string pluginId, PluginBus bus, ScopedLog log) : IPluginBus
    {
        private readonly Lock _gate = new();
        private readonly List<IDisposable> _subscriptions = [];
        private bool _closed;

        public void Publish(string topic, string? payload = null)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }
            }

            bus.Publish(pluginId, topic, payload, (owner, e) => log.Error($"A subscriber of '{topic}' failed.", e));
        }

        public IDisposable Subscribe(string topic, Action<PluginMessage> handler)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return new Noop();
                }

                IDisposable subscription = bus.Subscribe(pluginId, topic, handler);
                _subscriptions.Add(subscription);
                return subscription;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                foreach (IDisposable subscription in _subscriptions)
                {
                    subscription.Dispose();
                }

                _subscriptions.Clear();
            }
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
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

    public event EventHandler<string>? Changed;

    /// <summary>True when the plugin listens for <see cref="Changed"/>, so Notch does not need to restart it.</summary>
    public bool HandlesChanges => Changed is not null;

    /// <summary>The stored value as JSON, or null when the key is not set.</summary>
    public JsonElement? Raw(string key)
    {
        lock (_gate)
        {
            return Load().TryGetValue(key, out JsonElement element) ? element : null;
        }
    }

    /// <summary>Stores a value the user changed in Notch's Settings window and tells the plugin.</summary>
    public void SetFromUser<T>(string key, T value)
    {
        Set(key, value);
        EventHandler<string>? handler = Changed;
        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handler(this, key);
                }
                catch (Exception)
                {
                    // The plugin's own bug; it must not reach the Settings window.
                }
            });
        }
    }

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
