using Notch.Core.Activities;

namespace Notch.Core.Plugins;

/// <summary>
/// The plugin's pages: each one is a tab of its own in the expanded notch, with a row of figures
/// on top and a console below it. API version 3.
/// </summary>
public interface IPluginPages
{
    /// <summary>
    /// Adds <paramref name="page"/>, or replaces the plugin's page with the same <see cref="PluginPage.Id"/>.
    /// The console's lines are kept when a page is replaced.
    /// </summary>
    void Set(PluginPage page);

    /// <summary>Appends a line to a page's console. Ignored when the page does not exist.</summary>
    void Append(string pageId, string line);

    /// <summary>Empties a page's console.</summary>
    void ClearConsole(string pageId);

    /// <summary>Expands the notch on this page. Ignored when the page does not exist.</summary>
    void Open(string pageId);

    /// <summary>Removes a page by id. False when there was no such page.</summary>
    bool Remove(string id);

    /// <summary>Removes all of the plugin's pages.</summary>
    void Clear();
}

/// <summary>A tab: figures on top, a console below. Plain data; Notch draws it in its own style.</summary>
public sealed record PluginPage
{
    /// <summary>Stable per page and unique within the plugin.</summary>
    public required string Id { get; init; }

    /// <summary>The tab's name. Keep it short.</summary>
    public required string Title { get; init; }

    /// <summary>One line under the tab names, e.g. a connection state. Null for none.</summary>
    public string? Status { get; init; }

    /// <summary>The figures in a row on top, up to eight.</summary>
    public IReadOnlyList<PluginStat> Stats { get; init; } = [];

    /// <summary>
    /// Shows an input line under the console and is called with each line the user submits.
    /// Null makes the console read-only. Called on a background thread, and exceptions it throws
    /// are logged rather than propagated.
    /// </summary>
    public Action<string>? Input { get; init; }

    /// <summary>Greyed text in the empty input line, e.g. "Send a command".</summary>
    public string? InputHint { get; init; }

    /// <summary>
    /// Shows a back button in the top left of the page that calls this. Null for none.
    /// Called on a background thread. API version 4.
    /// </summary>
    public Action? Back { get; init; }

    /// <summary>The back button's text, e.g. "Servers". Defaults to "Back".</summary>
    public string? BackLabel { get; init; }

    /// <summary>
    /// A list of rows the user can click, shown in place of the console while it is not empty.
    /// API version 4.
    /// </summary>
    public IReadOnlyList<PluginChoice> Choices { get; init; } = [];

    /// <summary>
    /// A row of buttons in the top right of the page, e.g. Start, Stop, Restart. Up to six.
    /// API version 5.
    /// </summary>
    public IReadOnlyList<PluginAction> Actions { get; init; } = [];

    /// <summary>
    /// Free-form content: text, tables, charts, switches, sliders, text boxes and more, in a
    /// scrolling column. Shown in place of the console while it is not empty (and while there are
    /// no <see cref="Choices"/>, which win). Up to 100 blocks. API version 5.
    /// </summary>
    public IReadOnlyList<PluginBlock> Blocks { get; init; } = [];
}

/// <summary>A button on a page.</summary>
public sealed record PluginAction
{
    public required string Label { get; init; }

    /// <summary>Colour of the label; null for the notch's own colours.</summary>
    public GlowColor? Color { get; init; }

    /// <summary>False greys the button out and ignores clicks, e.g. Start while the server is running.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// For actions that cannot be undone: the first click changes the label to "Click again" for
    /// a few seconds, and only a second click inside that time calls <see cref="Clicked"/>.
    /// </summary>
    public bool Confirm { get; init; }

    /// <summary>Shown as a tooltip.</summary>
    public string? Hint { get; init; }

    /// <summary>Called on a background thread when the button is clicked (and confirmed, if asked to).</summary>
    public Action? Clicked { get; init; }
}

/// <summary>One clickable row of a page's list: a name, a headline value and a line of detail.</summary>
public sealed record PluginChoice
{
    public required string Label { get; init; }

    public string? Value { get; init; }

    public string? Detail { get; init; }

    /// <summary>Colour of the value; null for the notch's own colours.</summary>
    public GlowColor? Color { get; init; }

    /// <summary>Called on a background thread when the row is clicked.</summary>
    public Action? Clicked { get; init; }
}

/// <summary>One figure on a page: a caption, a headline value and a line of detail.</summary>
public sealed record PluginStat
{
    public required string Label { get; init; }

    public string? Value { get; init; }

    public string? Detail { get; init; }

    /// <summary>0..1 draws a bar along the bottom; null for none.</summary>
    public double? Progress { get; init; }

    /// <summary>Colour of the value and the bar; null for the notch's own colours.</summary>
    public GlowColor? Color { get; init; }
}

/// <summary>A page together with the plugin that owns it.</summary>
public sealed record PluginPageEntry(string PluginId, PluginPage Page);

/// <summary>What changed on a <see cref="PluginPageBoard"/>.</summary>
public enum PluginPageChange
{
    /// <summary>A page was added, replaced or removed.</summary>
    Pages,

    /// <summary>Lines were added to a console.</summary>
    Console,

    /// <summary>A console was emptied.</summary>
    ConsoleCleared,

    /// <summary>The plugin asked for a page to be shown.</summary>
    OpenRequested,
}

/// <summary>
/// Holds the pages of all plugins and their console lines. Plugins write through
/// <see cref="IPluginPages"/>; the shell reads the board when <see cref="Changed"/> fires.
/// </summary>
public sealed class PluginPageBoard
{
    /// <summary>Console lines kept per page; older ones are dropped.</summary>
    public const int MaxConsoleLines = 500;

    /// <summary>Blocks drawn per page; the rest are ignored.</summary>
    public const int MaxBlocks = 100;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string PluginId, string PageId), Entry> _entries = [];
    private long _sequence;

    /// <summary>Raised on whichever thread caused the change. The key is the page it concerns.</summary>
    public event EventHandler<PluginPageChangedEventArgs>? Changed;

    public void Set(string pluginId, PluginPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Title);

        lock (_gate)
        {
            var key = (pluginId, page.Id);
            Entry entry = _entries.TryGetValue(key, out Entry? previous) ? previous : new Entry(++_sequence);
            entry.Page = new PluginPageEntry(pluginId, page with { Stats = [.. page.Stats.Take(8)], Actions = [.. page.Actions.Take(6)] });
            _entries[key] = entry;
        }

        Raise(PluginPageChange.Pages, pluginId, page.Id);
    }

    public void Append(string pluginId, string pageId, string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        lock (_gate)
        {
            if (!_entries.TryGetValue((pluginId, pageId), out Entry? entry))
            {
                return;
            }

            entry.Console.Add(line);
            if (entry.Console.Count > MaxConsoleLines)
            {
                entry.Console.RemoveRange(0, entry.Console.Count - MaxConsoleLines);
            }
        }

        Raise(PluginPageChange.Console, pluginId, pageId, line);
    }

    public void ClearConsole(string pluginId, string pageId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue((pluginId, pageId), out Entry? entry))
            {
                return;
            }

            entry.Console.Clear();
        }

        Raise(PluginPageChange.ConsoleCleared, pluginId, pageId);
    }

    public void RequestOpen(string pluginId, string pageId)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey((pluginId, pageId)))
            {
                return;
            }
        }

        Raise(PluginPageChange.OpenRequested, pluginId, pageId);
    }

    public bool Remove(string pluginId, string pageId)
    {
        lock (_gate)
        {
            if (!_entries.Remove((pluginId, pageId)))
            {
                return false;
            }
        }

        Raise(PluginPageChange.Pages, pluginId, pageId);
        return true;
    }

    public void RemoveAll(string pluginId)
    {
        bool removed = false;
        lock (_gate)
        {
            foreach ((string PluginId, string PageId) key in _entries.Keys.Where(k => k.PluginId == pluginId).ToList())
            {
                removed |= _entries.Remove(key);
            }
        }

        if (removed)
        {
            Raise(PluginPageChange.Pages, pluginId, "");
        }
    }

    /// <summary>In the order the pages were first added.</summary>
    public IReadOnlyList<PluginPageEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Values.OrderBy(e => e.Sequence).Select(e => e.Page!)];
        }
    }

    /// <summary>The page's console lines, oldest first; empty when there is no such page.</summary>
    public IReadOnlyList<string> ConsoleOf(string pluginId, string pageId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue((pluginId, pageId), out Entry? entry) ? [.. entry.Console] : [];
        }
    }

    private void Raise(PluginPageChange change, string pluginId, string pageId, string? line = null) =>
        Changed?.Invoke(this, new PluginPageChangedEventArgs(change, pluginId, pageId, line));

    private sealed class Entry(long sequence)
    {
        public long Sequence { get; } = sequence;

        public PluginPageEntry? Page { get; set; }

        public List<string> Console { get; } = [];
    }
}

public sealed class PluginPageChangedEventArgs(PluginPageChange change, string pluginId, string pageId, string? line) : EventArgs
{
    public PluginPageChange Change { get; } = change;

    public string PluginId { get; } = pluginId;

    /// <summary>The page concerned; empty when every page of the plugin was removed.</summary>
    public string PageId { get; } = pageId;

    /// <summary>The line that was appended, for <see cref="PluginPageChange.Console"/>.</summary>
    public string? Line { get; } = line;
}
