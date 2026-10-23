namespace Notch.Core.Plugins;

/// <summary>
/// The plugin's entries in the command palette: fixed commands, ranked with Notch's own by what the user
/// types, and live results worked out for what was typed, such as files or a sum. API version 9.
/// </summary>
public interface IPluginPalette
{
    /// <summary>Adds <paramref name="entry"/>, or replaces the plugin's entry with the same <see cref="PluginPaletteEntry.Id"/>.</summary>
    void Set(PluginPaletteEntry entry);

    /// <summary>Removes an entry by id. False when there was no such entry.</summary>
    bool Remove(string id);

    /// <summary>Removes all of the plugin's fixed entries.</summary>
    void Clear();

    /// <summary>
    /// Asks <paramref name="provider"/> for results each time the user pauses typing in the palette, with what they
    /// typed. Called on a background thread; the token is cancelled when the text changes again, and results that
    /// come after about two seconds, or after the text changed, are dropped. Notch shows up to five of them, below
    /// its best matches, in the order given. Null stops asking. A plugin has one provider; setting another replaces it.
    /// </summary>
    void SetProvider(Func<string, CancellationToken, Task<IReadOnlyList<PluginPaletteEntry>>>? provider);
}

/// <summary>One row of the command palette. Plain data; Notch draws it in its own style.</summary>
public sealed record PluginPaletteEntry
{
    /// <summary>Unique among the plugin's fixed entries. For live results it only has to be unique within one answer.</summary>
    public required string Id { get; init; }

    /// <summary>What the row says, e.g. "Start timer on Bumper Cars Chaos".</summary>
    public required string Title { get; init; }

    /// <summary>A word or two on the right, e.g. "Chomp" or "File". Defaults to the plugin's name.</summary>
    public string? Detail { get; init; }

    /// <summary>More words that find a fixed entry, which the user does not see, e.g. "project track".</summary>
    public string? Keywords { get; init; }

    /// <summary>A Segoe Fluent Icons glyph on the left.</summary>
    public string? Glyph { get; init; }

    /// <summary>A picture on the left in place of <see cref="Glyph"/>, e.g. an app's icon. PNG, JPEG or any format WPF reads.</summary>
    public byte[]? Image { get; init; }

    /// <summary>Run when the user picks the row, after the palette has closed. Called on a background thread.</summary>
    public required Action Invoke { get; init; }
}

/// <summary>A palette entry together with the plugin that offers it.</summary>
public sealed record PluginPaletteItem(string PluginId, string PluginName, PluginPaletteEntry Entry);

/// <summary>
/// Holds the palette entries and live providers of all plugins. Plugins write through <see cref="IPluginPalette"/>;
/// the palette reads the entries each time it opens and asks the providers as the user types.
/// </summary>
public sealed class PluginPaletteBoard
{
    /// <summary>Live results shown per plugin, and in all.</summary>
    public const int MaxResultsPerPlugin = 5;

    public const int MaxResults = 5;

    /// <summary>Fixed entries kept per plugin; more are ignored.</summary>
    public const int MaxEntriesPerPlugin = 100;

    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string PluginId, string Id), (PluginPaletteItem Item, long Sequence)> _entries = [];
    private readonly Dictionary<string, Provider> _providers = [];
    private long _sequence;

    /// <summary>Told when a plugin's provider throws, with the plugin's id. The manager logs it.</summary>
    public Action<string, Exception>? ProviderFailed { get; set; }

    public void Set(string pluginId, string pluginName, PluginPaletteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Title);
        ArgumentNullException.ThrowIfNull(entry.Invoke);

        lock (_gate)
        {
            var key = (pluginId, entry.Id);
            if (_entries.TryGetValue(key, out var previous))
            {
                _entries[key] = (new PluginPaletteItem(pluginId, pluginName, entry), previous.Sequence);
            }
            else if (_entries.Keys.Count(k => k.PluginId == pluginId) < MaxEntriesPerPlugin)
            {
                _entries[key] = (new PluginPaletteItem(pluginId, pluginName, entry), ++_sequence);
            }
        }
    }

    public bool Remove(string pluginId, string id)
    {
        lock (_gate)
        {
            return _entries.Remove((pluginId, id));
        }
    }

    /// <summary>Forgets a plugin's entries, and its provider too when <paramref name="provider"/> is true.</summary>
    public void RemoveAll(string pluginId, bool provider = true)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.PluginId == pluginId).ToList())
            {
                _entries.Remove(key);
            }

            if (provider)
            {
                _providers.Remove(pluginId);
            }
        }
    }

    public void SetProvider(string pluginId, string pluginName, Func<string, CancellationToken, Task<IReadOnlyList<PluginPaletteEntry>>>? provider)
    {
        lock (_gate)
        {
            if (provider is null)
            {
                _providers.Remove(pluginId);
            }
            else
            {
                _providers[pluginId] = new Provider(pluginName, provider);
            }
        }
    }

    /// <summary>The fixed entries of every plugin, in the order they were first added.</summary>
    public IReadOnlyList<PluginPaletteItem> Entries()
    {
        lock (_gate)
        {
            return [.. _entries.Values.OrderBy(e => e.Sequence).Select(e => e.Item)];
        }
    }

    public bool HasProviders
    {
        get
        {
            lock (_gate)
            {
                return _providers.Count > 0;
            }
        }
    }

    /// <summary>
    /// Asks every plugin's provider about <paramref name="query"/> at once and gathers what they answer in time,
    /// in the order the plugins set their providers. A provider that throws, or is too slow, adds nothing.
    /// </summary>
    /// <param name="onError">Told which plugin's provider failed, and how; <see cref="ProviderFailed"/> when null.</param>
    public async Task<IReadOnlyList<PluginPaletteItem>> QueryAsync(string query, CancellationToken token, Action<string, Exception>? onError = null)
    {
        onError ??= ProviderFailed;
        KeyValuePair<string, Provider>[] providers;
        lock (_gate)
        {
            providers = [.. _providers];
        }

        if (providers.Length == 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        IReadOnlyList<PluginPaletteItem>[] answers = await Task.WhenAll(providers.Select(p => AskAsync(p.Key, p.Value, query, token, onError)));
        token.ThrowIfCancellationRequested();
        return [.. answers.SelectMany(a => a).Take(MaxResults)];
    }

    private static async Task<IReadOnlyList<PluginPaletteItem>> AskAsync(
        string pluginId, Provider provider, string query, CancellationToken token, Action<string, Exception>? onError)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProviderTimeout);
        try
        {
            // Off the caller's thread even when the provider does its work before its first await.
            Task<IReadOnlyList<PluginPaletteEntry>> asked = Task.Run(() => provider.Ask(query, timeout.Token), timeout.Token);
            IReadOnlyList<PluginPaletteEntry> results = await asked.WaitAsync(timeout.Token);
            return [.. (results ?? [])
                .Where(r => r is { Id.Length: > 0, Title.Length: > 0, Invoke: not null })
                .Take(MaxResultsPerPlugin)
                .Select(r => new PluginPaletteItem(pluginId, provider.PluginName, r))];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception e)
        {
            onError?.Invoke(pluginId, e);
            return [];
        }
    }

    private sealed record Provider(string PluginName, Func<string, CancellationToken, Task<IReadOnlyList<PluginPaletteEntry>>> Ask);
}
