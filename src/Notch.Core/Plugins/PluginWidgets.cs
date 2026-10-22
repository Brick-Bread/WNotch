using Notch.Core.Widgets;

namespace Notch.Core.Plugins;

/// <summary>
/// A kind of widget a plugin offers for the Home tab. It shows in the "Add widget" picker next to the
/// built-in ones, and the user can place it, move it, size it and set it up like any other. API version 8.
/// </summary>
/// <remarks>
/// The callbacks run on a background thread, never on the UI thread, and an exception thrown from one is
/// logged without affecting Notch.
/// </remarks>
public sealed record PluginWidgetType
{
    /// <summary>Tells the plugin's widget types apart. Kept in the user's layout, so do not change it between versions.</summary>
    public required string Id { get; init; }

    /// <summary>What the picker and edit mode call it.</summary>
    public required string Name { get; init; }

    /// <summary>One line for the picker.</summary>
    public string? Description { get; init; }

    /// <summary>A Segoe Fluent Icons glyph for the picker and edit mode.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>A new widget of this type takes a whole row instead of half of one. The user can change it.</summary>
    public bool FullWidth { get; init; }

    /// <summary>Only one widget of this type can be on Home.</summary>
    public bool Single { get; init; }

    /// <summary>
    /// Settings shown under the widget's gear in edit mode, read back through <see cref="PluginWidget.Option"/>.
    /// When there are any, the form opens as soon as the widget is added.
    /// </summary>
    public IReadOnlyList<WidgetOption> Options { get; init; } = [];

    /// <summary>
    /// A widget of this type is on Home: the user just added it, or it was there when the plugin started.
    /// Show something for it with <see cref="IPluginWidgets.Show"/>.
    /// </summary>
    public Action<PluginWidget>? Placed { get; init; }

    /// <summary>The user took a widget of this type off Home. Stop updating it.</summary>
    public Action<PluginWidget>? Removed { get; init; }

    /// <summary>The user saved new settings for a widget of this type; the argument carries them.</summary>
    public Action<PluginWidget>? OptionsChanged { get; init; }

    /// <summary>Home came on screen (true) or went off it (false). A widget need not be kept up to date while it cannot be seen.</summary>
    public Action<PluginWidget, bool>? VisibleChanged { get; init; }
}

/// <summary>One widget of a plugin's type on Home. There can be several of the same type, each with its own settings.</summary>
/// <param name="Id">Pass to <see cref="IPluginWidgets.Show"/>. Stays the same across restarts.</param>
/// <param name="TypeId">The <see cref="PluginWidgetType.Id"/> it is a widget of.</param>
/// <param name="Options">The settings, by <see cref="WidgetOption.Key"/>, with <see cref="WidgetOption.Default"/> for any the user has not set.</param>
public sealed record PluginWidget(string Id, string TypeId, IReadOnlyDictionary<string, string> Options)
{
    /// <summary>A setting's value, or <paramref name="fallback"/> when the user has not set it.</summary>
    public string Option(string key, string fallback = "") => Options.TryGetValue(key, out string? value) ? value : fallback;
}

/// <summary>What a plugin's widget shows: blocks Notch draws in its own style, or a WPF element of the plugin's own.</summary>
public sealed record PluginWidgetContent
{
    /// <summary>The caption at the top of the card. Null uses the type's name; empty shows none.</summary>
    public string? Title { get; init; }

    /// <summary>The widget's body, the same blocks a page uses (see <see cref="PluginPage.Blocks"/>).</summary>
    public IReadOnlyList<PluginBlock> Blocks { get; init; } = [];

    /// <summary>
    /// Instead of <see cref="Blocks"/>, a body the plugin draws itself: a function that returns a
    /// <c>System.Windows.FrameworkElement</c>. Notch calls it on the UI thread, once for each widget and again
    /// whenever content with a different function is shown, and puts the element in the card. The plugin
    /// then needs to target <c>net10.0-windows</c> with <c>&lt;UseWPF&gt;true&lt;/UseWPF&gt;</c>, and must
    /// change the element only on the UI thread (through its <c>Dispatcher</c>). It should use the theme's
    /// brushes by resource key, such as <c>TextBrush</c> and <c>CardBrush</c>, to look at home in every theme.
    /// </summary>
    public Func<object>? View { get; init; }

    /// <summary>Called on a background thread when the user clicks the card outside a button or other control.</summary>
    public Action? Clicked { get; init; }
}

/// <summary>Widgets a plugin offers for the Home tab. API version 8.</summary>
public interface IPluginWidgets
{
    /// <summary>
    /// Offers <paramref name="type"/> in the picker, or replaces the plugin's type with the same id. Widgets
    /// of this type already on Home are then reported through <see cref="PluginWidgetType.Placed"/>.
    /// </summary>
    void Register(PluginWidgetType type);

    /// <summary>Withdraws a type. Its widgets stay in the user's layout and say that their plugin is not running.</summary>
    bool Unregister(string typeId);

    /// <summary>The plugin's widgets that are on Home now.</summary>
    IReadOnlyList<PluginWidget> Placed { get; }

    /// <summary>Sets what one of the plugin's widgets shows. Ignored for a widget that is not one of the plugin's or is no longer on Home.</summary>
    void Show(string widgetId, PluginWidgetContent content);
}

/// <summary>A plugin's widget type together with the plugin that offers it.</summary>
public sealed record PluginWidgetTypeEntry(string PluginId, PluginWidgetType Type)
{
    /// <summary>How a widget on Home refers to its type: <see cref="HomeWidget.Source"/>.</summary>
    public string Source => PluginWidgetBoard.SourceFor(PluginId, Type.Id);
}

/// <summary>
/// The widget types plugins offer, which of their widgets are on Home, and what each shows. Plugins write
/// through <see cref="IPluginWidgets"/>; the shell places widgets and reads content when <see cref="Changed"/> fires.
/// </summary>
public sealed class PluginWidgetBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginWidgetTypeEntry> _types = [];
    private readonly Dictionary<string, Placement> _placed = [];
    private bool _visible;

    /// <summary>Raised on whichever thread caused the change.</summary>
    public event EventHandler? Changed;

    public static string SourceFor(string pluginId, string typeId) => $"{pluginId}/{typeId}";

    public IReadOnlyList<PluginWidgetTypeEntry> Types
    {
        get
        {
            lock (_gate)
            {
                return [.. _types.Values];
            }
        }
    }

    public PluginWidgetTypeEntry? FindType(string? source)
    {
        lock (_gate)
        {
            return source is not null && _types.TryGetValue(source, out PluginWidgetTypeEntry? entry) ? entry : null;
        }
    }

    public void Register(string pluginId, PluginWidgetType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(type.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(type.Name);

        lock (_gate)
        {
            var entry = new PluginWidgetTypeEntry(pluginId, type);
            _types[entry.Source] = entry;

            // A replaced type keeps its widgets, which now answer to the new callbacks.
            foreach (Placement placement in _placed.Values.Where(p => p.Source == entry.Source))
            {
                placement.Type = type;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string pluginId, string typeId)
    {
        string source = SourceFor(pluginId, typeId);
        lock (_gate)
        {
            if (!_types.Remove(source))
            {
                return false;
            }

            foreach (string id in _placed.Where(p => p.Value.Source == source).Select(p => p.Key).ToList())
            {
                _placed.Remove(id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Forgets everything of a plugin that stopped, without calling it back.</summary>
    public void RemoveAll(string pluginId)
    {
        bool removed;
        lock (_gate)
        {
            List<string> types = [.. _types.Where(t => t.Value.PluginId == pluginId).Select(t => t.Key)];
            types.ForEach(t => _types.Remove(t));
            List<string> placed = [.. _placed.Where(p => p.Value.PluginId == pluginId).Select(p => p.Key)];
            placed.ForEach(p => _placed.Remove(p));
            removed = types.Count > 0 || placed.Count > 0;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The shell has a widget of a registered type on Home. Tells its plugin, the first time only. False when
    /// its type is not registered (its plugin is not running).
    /// </summary>
    public bool Place(HomeWidget widget)
    {
        PluginWidgetType type;
        PluginWidget instance;
        bool visible;
        lock (_gate)
        {
            if (widget.Source is not { } source || !_types.TryGetValue(source, out PluginWidgetTypeEntry? entry))
            {
                return false;
            }

            if (_placed.ContainsKey(widget.Id))
            {
                return true;
            }

            instance = InstanceOf(widget, entry.Type);
            _placed[widget.Id] = new Placement(entry.PluginId, source, entry.Type, instance);
            type = entry.Type;
            visible = _visible;
        }

        type.Placed?.Invoke(instance);
        if (visible)
        {
            type.VisibleChanged?.Invoke(instance, true);
        }

        return true;
    }

    /// <summary>The user took a widget off Home.</summary>
    public void Unplace(string widgetId)
    {
        Placement? placement;
        lock (_gate)
        {
            if (!_placed.Remove(widgetId, out placement))
            {
                return;
            }
        }

        placement.Type.Removed?.Invoke(placement.Instance);
    }

    /// <summary>The user saved a widget's settings.</summary>
    public void UpdateOptions(HomeWidget widget)
    {
        Placement? placement;
        lock (_gate)
        {
            if (!_placed.TryGetValue(widget.Id, out placement))
            {
                return;
            }

            placement.Instance = InstanceOf(widget, placement.Type);
        }

        placement.Type.OptionsChanged?.Invoke(placement.Instance);
    }

    /// <summary>Home came on screen or went off it.</summary>
    public void SetVisible(bool visible)
    {
        List<Placement> placed;
        lock (_gate)
        {
            if (_visible == visible)
            {
                return;
            }

            _visible = visible;
            placed = [.. _placed.Values];
        }

        foreach (Placement placement in placed)
        {
            placement.Type.VisibleChanged?.Invoke(placement.Instance, visible);
        }
    }

    public bool IsPlaced(string widgetId)
    {
        lock (_gate)
        {
            return _placed.ContainsKey(widgetId);
        }
    }

    public IReadOnlyList<PluginWidget> PlacedFor(string pluginId)
    {
        lock (_gate)
        {
            return [.. _placed.Values.Where(p => p.PluginId == pluginId).Select(p => p.Instance)];
        }
    }

    /// <summary>What a placed widget shows, or null before its plugin has shown anything.</summary>
    public PluginWidgetContent? ContentFor(string widgetId)
    {
        lock (_gate)
        {
            return _placed.TryGetValue(widgetId, out Placement? placement) ? placement.Content : null;
        }
    }

    public void Show(string pluginId, string widgetId, PluginWidgetContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            if (!_placed.TryGetValue(widgetId, out Placement? placement) || placement.PluginId != pluginId)
            {
                return;
            }

            placement.Content = content;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static PluginWidget InstanceOf(HomeWidget widget, PluginWidgetType type)
    {
        // Only the plugin's own settings: not what Notch keeps for itself in the same dictionary.
        Dictionary<string, string> options = [];
        foreach (WidgetOption option in type.Options)
        {
            options[option.Key] = widget.Options.TryGetValue(option.Key, out string? value) ? value : option.Default;
        }

        return new PluginWidget(widget.Id, type.Id, options);
    }

    private sealed class Placement(string pluginId, string source, PluginWidgetType type, PluginWidget instance)
    {
        public string PluginId { get; } = pluginId;

        public string Source { get; } = source;

        public PluginWidgetType Type { get; set; } = type;

        public PluginWidget Instance { get; set; } = instance;

        public PluginWidgetContent? Content { get; set; }
    }
}
