using System.Text.Json.Serialization;

namespace Notch.Core.Widgets;

/// <summary>How much of a row on the Home tab a widget takes. Two half-width widgets next to each other share a row.</summary>
public enum WidgetWidth
{
    Full,
    Half,
}

/// <summary>One widget on the Home tab, as the settings keep it.</summary>
public sealed class HomeWidget
{
    /// <summary>Tells two widgets of the same kind apart, such as two notes.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>One of <see cref="WidgetKinds"/>.</summary>
    public string Kind { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<WidgetWidth>))]
    public WidgetWidth Width { get; set; } = WidgetWidth.Full;

    /// <summary>For a widget that can have nothing to show (<see cref="WidgetType.CanBeEmpty"/>): leave it out of Home until it has.</summary>
    public bool HideWhenEmpty { get; set; } = true;

    /// <summary>The widget's own settings, by <see cref="WidgetOption.Key"/>, and anything it keeps, such as a note's text.</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    /// <summary>An option's value, or what the widget's type gives it when it has not been set.</summary>
    public string Option(string key)
    {
        if (Options.TryGetValue(key, out string? value))
        {
            return value;
        }

        return WidgetCatalog.Find(Kind)?.Options.FirstOrDefault(o => o.Key == key)?.Default ?? "";
    }

    /// <summary>A toggle option is "true" or anything else.</summary>
    public bool Flag(string key) => Option(key) == WidgetOption.On;

    public static HomeWidget Create(WidgetType type) => new() { Kind = type.Kind, Width = type.DefaultWidth };

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>The kinds of widget there are. Stored in the settings, so never renamed.</summary>
public static class WidgetKinds
{
    public const string Media = "media";
    public const string Agents = "agents";
    public const string Timer = "timer";
    public const string Calendar = "calendar";
    public const string Clock = "clock";
    public const string Stats = "stats";
    public const string Shelf = "shelf";
    public const string Clipboard = "clipboard";
    public const string Weather = "weather";
    public const string Note = "note";
    public const string Launcher = "launcher";
    public const string Command = "command";
    public const string Feed = "feed";
    public const string Plugin = "plugin";
}

public enum WidgetOptionType
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>Several lines of text, one item per line.</summary>
    Lines,

    /// <summary>On or off, stored as <see cref="WidgetOption.On"/> or <see cref="WidgetOption.Off"/>.</summary>
    Toggle,

    /// <summary>One of <see cref="WidgetOption.Choices"/>; null choices are filled in by the app (the plugin cards there are).</summary>
    Choice,
}

/// <summary>Something about a widget the user can set from its gear button.</summary>
public sealed record WidgetOption(
    string Key,
    string Label,
    WidgetOptionType Type,
    string Default = "",
    IReadOnlyList<string>? Choices = null,
    string? Hint = null)
{
    public const string On = "true";
    public const string Off = "false";
}

/// <summary>A kind of widget as the "Add widget" picker offers it.</summary>
/// <param name="Single">Only one can be on Home, because it shows something there is only one of, such as the timer.</param>
/// <param name="CanBeEmpty">Sometimes has nothing to show, and can then be left out (<see cref="HomeWidget.HideWhenEmpty"/>).</param>
public sealed record WidgetType(
    string Kind,
    string Name,
    string Glyph,
    string Description,
    WidgetWidth DefaultWidth,
    bool Single,
    bool CanBeEmpty,
    IReadOnlyList<WidgetOption> Options)
{
    /// <summary>A new widget of this type has options that must be filled in before it shows anything.</summary>
    public bool NeedsSetup => Kind is WidgetKinds.Weather or WidgetKinds.Launcher or WidgetKinds.Command or WidgetKinds.Feed or WidgetKinds.Plugin;
}

public static class WidgetCatalog
{
    public const string TitleKey = "title";

    /// <summary>Where a note keeps what was typed into it. Not shown in the note's settings.</summary>
    public const string NoteTextKey = "text";

    public const string RunInTerminal = "Terminal tab";
    public const string RunHidden = "In the background";

    public static IReadOnlyList<WidgetType> All { get; } =
    [
        new(WidgetKinds.Media, "Media", "\uE8D6", "What is playing, with its controls", WidgetWidth.Full, Single: true, CanBeEmpty: true, []),
        new(WidgetKinds.Agents, "Agents", "\uE99A", "Coding agents and what they are doing", WidgetWidth.Full, Single: true, CanBeEmpty: true, []),
        new(WidgetKinds.Timer, "Timer", "\uE916", "Pomodoro and one-click timers", WidgetWidth.Half, Single: true, CanBeEmpty: false, []),
        new(WidgetKinds.Calendar, "Calendar", "\uE787", "Upcoming events from your feeds", WidgetWidth.Half, Single: true, CanBeEmpty: false, []),
        new(WidgetKinds.Clock, "Clock", "\uE121", "The time here or anywhere else", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new(TitleKey, "Label", WidgetOptionType.Text, Hint: "Shown above the time, such as a city"),
            new("zone", "Time zone", WidgetOptionType.Text, Hint: "Empty for this computer's, or a city or zone such as Tokyo or UTC"),
            new("hours24", "24-hour clock", WidgetOptionType.Toggle, WidgetOption.Off),
            new("seconds", "Show seconds", WidgetOptionType.Toggle, WidgetOption.Off),
        ]),
        new(WidgetKinds.Stats, "System stats", "\uE9D2", "CPU, memory and more at a glance", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new("cpu", "CPU", WidgetOptionType.Toggle, WidgetOption.On),
            new("memory", "Memory", WidgetOptionType.Toggle, WidgetOption.On),
            new("gpu", "GPU", WidgetOptionType.Toggle, WidgetOption.Off),
            new("network", "Network", WidgetOptionType.Toggle, WidgetOption.Off),
            new("battery", "Battery", WidgetOptionType.Toggle, WidgetOption.Off),
        ]),
        new(WidgetKinds.Shelf, "Shelf", "\uE7B8", "The latest files on the shelf", WidgetWidth.Full, Single: true, CanBeEmpty: true, []),
        new(WidgetKinds.Clipboard, "Clipboard", "\uE77F", "What you copied last", WidgetWidth.Half, Single: true, CanBeEmpty: true, []),
        new(WidgetKinds.Weather, "Weather", "\uE706", "Current weather from Open-Meteo", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new("city", "City", WidgetOptionType.Text, Hint: "Such as London or San Francisco"),
            new("units", "Units", WidgetOptionType.Choice, "Celsius", ["Celsius", "Fahrenheit"]),
        ]),
        new(WidgetKinds.Note, "Note", "\uE70B", "Text you type, kept for you", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new(TitleKey, "Title", WidgetOptionType.Text, "Note"),
        ]),
        new(WidgetKinds.Launcher, "Launcher", "\uE8A7", "Buttons that open apps, files, folders or links", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new(TitleKey, "Title", WidgetOptionType.Text, "Launcher"),
            new("items", "Buttons", WidgetOptionType.Lines, Hint: "One per line: Name = C:\\path\\app.exe, a folder, or https://link"),
        ]),
        new(WidgetKinds.Command, "Commands", "\uE756", "Buttons that run commands", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new(TitleKey, "Title", WidgetOptionType.Text, "Commands"),
            new("commands", "Buttons", WidgetOptionType.Lines, Hint: "One per line: Name = command, such as Build = dotnet build"),
            new("run", "Run in", WidgetOptionType.Choice, RunInTerminal, [RunInTerminal, RunHidden]),
        ]),
        new(WidgetKinds.Feed, "Web feed", "\uE774", "Text fetched from a link, kept up to date", WidgetWidth.Half, Single: false, CanBeEmpty: false,
        [
            new(TitleKey, "Title", WidgetOptionType.Text, "Feed"),
            new("url", "Link", WidgetOptionType.Text, Hint: "https://… returning text or JSON"),
            new("field", "JSON field", WidgetOptionType.Text, Hint: "Optional, such as data.status or items.0.title"),
            new("minutes", "Refresh every", WidgetOptionType.Choice, "15 min", ["1 min", "5 min", "15 min", "60 min"]),
        ]),
        new(WidgetKinds.Plugin, "Plugin card", "\uE71D", "A card from a plugin, on Home", WidgetWidth.Half, Single: false, CanBeEmpty: true,
        [
            new("card", "Card", WidgetOptionType.Choice),
        ]),
    ];

    public static WidgetType? Find(string? kind) => All.FirstOrDefault(t => t.Kind == kind);
}

/// <summary>Arranges the Home tab's widgets and keeps the list in the settings sensible.</summary>
public static class HomeLayout
{
    /// <summary>More than this is more than the notch can show, even scrolling.</summary>
    public const int MaxWidgets = 24;

    /// <summary>What Home showed before it could be changed: media, agents, then the timer and calendar side by side.</summary>
    public static List<HomeWidget> Default() =>
    [
        new() { Kind = WidgetKinds.Media, Width = WidgetWidth.Full },
        new() { Kind = WidgetKinds.Agents, Width = WidgetWidth.Full },
        new() { Kind = WidgetKinds.Timer, Width = WidgetWidth.Half },
        new() { Kind = WidgetKinds.Calendar, Width = WidgetWidth.Half },
    ];

    /// <summary>
    /// The rows Home shows, top to bottom: a full-width widget alone, two half-width widgets that
    /// follow each other together, and a half-width widget with no partner alone on the left.
    /// Widgets that are not <paramref name="shown"/> take no room, so their neighbours pair up as if they were not there.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<HomeWidget>> Rows(IEnumerable<HomeWidget> widgets, Func<HomeWidget, bool>? shown = null)
    {
        var rows = new List<IReadOnlyList<HomeWidget>>();
        HomeWidget? waiting = null;
        foreach (HomeWidget widget in widgets.Where(w => shown?.Invoke(w) ?? true))
        {
            if (widget.Width == WidgetWidth.Full)
            {
                if (waiting is not null)
                {
                    rows.Add([waiting]);
                    waiting = null;
                }

                rows.Add([widget]);
            }
            else if (waiting is null)
            {
                waiting = widget;
            }
            else
            {
                rows.Add([waiting, widget]);
                waiting = null;
            }
        }

        if (waiting is not null)
        {
            rows.Add([waiting]);
        }

        return rows;
    }

    /// <summary>
    /// Repairs a list read from the settings: kinds this version does not know are dropped, as are
    /// second copies of a single widget, missing or repeated ids are replaced, and there are no more than fit.
    /// </summary>
    public static List<HomeWidget> Normalize(IEnumerable<HomeWidget?>? widgets)
    {
        var result = new List<HomeWidget>();
        var ids = new HashSet<string>();
        foreach (HomeWidget? widget in widgets ?? [])
        {
            if (widget is null || WidgetCatalog.Find(widget.Kind) is not { } type)
            {
                continue;
            }

            if (type.Single && result.Any(w => w.Kind == widget.Kind))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(widget.Id) || !ids.Add(widget.Id))
            {
                do
                {
                    widget.Id = HomeWidget.NewId();
                }
                while (!ids.Add(widget.Id));
            }

            widget.Options ??= [];
            if (!Enum.IsDefined(widget.Width))
            {
                widget.Width = type.DefaultWidth;
            }

            result.Add(widget);
            if (result.Count == MaxWidgets)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>The types the picker can still add: every type, less the single ones already on Home.</summary>
    public static IReadOnlyList<WidgetType> Addable(IReadOnlyCollection<HomeWidget> widgets) => widgets.Count >= MaxWidgets
        ? []
        : [.. WidgetCatalog.All.Where(t => !t.Single || widgets.All(w => w.Kind != t.Kind))];

    /// <summary>Moves a widget to a new place in the list, counted as if it had already been taken out. False when nothing moved.</summary>
    public static bool Move(List<HomeWidget> widgets, string id, int index)
    {
        int from = widgets.FindIndex(w => w.Id == id);
        if (from < 0)
        {
            return false;
        }

        index = Math.Clamp(index, 0, widgets.Count - 1);
        if (index == from)
        {
            return false;
        }

        HomeWidget widget = widgets[from];
        widgets.RemoveAt(from);
        widgets.Insert(index, widget);
        return true;
    }
}
