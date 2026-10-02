using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Notch.Core.Activities;
using Notch.Core.Plugins;
using Notch.Core.Settings;
using Notch.Platform.Display;

namespace Notch.App.Shell;

/// <summary>
/// Puts the theme the settings ask for into effect. The app's own look is Themes/Design.xaml
/// (fonts, sizes, radii), Themes/Styles.xaml (control styles) and one of Themes/Dark.xaml or
/// Light.xaml (colours). A theme offered by a plugin is a further resource dictionary merged after
/// those, so it can replace any key of them. Everything in XAML refers to these keys with
/// DynamicResource, so swapping dictionaries restyles the notch live.
/// </summary>
internal static class ThemeManager
{
    private static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    ];

    // The styles a theme may replace, and what each one styles; a style for another type would fail when it is applied.
    private static readonly Dictionary<string, Type> StyleTargets = new()
    {
        ["IconButton"] = typeof(Button),
        ["PillButton"] = typeof(Button),
        ["PillTextBox"] = typeof(TextBox),
        ["TabButton"] = typeof(RadioButton),
        ["Card"] = typeof(Border),
        ["CardLabel"] = typeof(TextBlock),
        ["CardValue"] = typeof(TextBlock),
    };

    private static List<ResourceDictionary> _merged = [];
    private static string? _signature;
    private static PluginThemeBoard? _board;
    private static AppSettings? _settings;
    private static ResourceDictionary? _plugin;
    private static string? _pluginSource;

    /// <summary>
    /// Used instead of the saved theme for this run (<c>--theme=</c>, for development), so a
    /// debug build can be looked at in either theme without changing the settings file. While it
    /// is set, no plugin theme is applied.
    /// </summary>
    public static NotchTheme? Override { get; set; }

    /// <summary>
    /// A plugin theme's key used instead of the saved one for this run (<c>--plugin-theme=</c>, for
    /// writing themes). Ignored while <see cref="Override"/> is set.
    /// </summary>
    public static string? PluginThemeOverride { get; set; }

    /// <summary>Whether the light theme is the one showing. For a plugin theme, whether it is built on the light one.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>The key (<see cref="PluginThemeEntry.Key"/>) of the plugin theme in effect, or null for Notch's own.</summary>
    public static string? ActivePluginTheme { get; private set; }

    /// <summary>Why the chosen plugin theme is not showing (its file is missing or wrong), or null.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Raised on the UI thread after every <see cref="Apply"/>, for whatever is coloured outside XAML.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Starts following the themes plugins offer: when the chosen one appears, changes or goes away
    /// (plugins start after the window does), it is applied or taken off again.
    /// </summary>
    public static void Attach(PluginThemeBoard board)
    {
        _board = board;
        board.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_settings is not null)
            {
                Apply(_settings);
            }
        });
    }

    /// <summary>Puts the theme the settings ask for into effect. Call again when the settings or the Windows theme change.</summary>
    public static void Apply(AppSettings settings)
    {
        _settings = settings;

        PluginThemeEntry? entry = Override is null ? _board?.Find(PluginThemeOverride ?? settings.PluginTheme) : null;
        ResourceDictionary? plugin = null;
        string source = "";
        Problem = null;
        if (entry is not null)
        {
            // The file is read again whenever it changed on disk, which suits writing a theme.
            source = $"{entry.FilePath}|{Stamp(entry.FilePath)}";
            plugin = source == _pluginSource ? _plugin : LoadPluginTheme(entry);
            if (plugin is null)
            {
                entry = null;
                source = "";
            }
        }

        bool light = entry is not null
            ? entry.Base == PluginThemeBase.Light
            : NotchThemes.IsLight(Override ?? settings.Theme, SystemTheme.AppsUseLightTheme);

        string signature = $"{light}#{source}";
        if (signature != _signature)
        {
            var next = new List<ResourceDictionary>
            {
                new() { Source = new Uri($"/Notch;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative) },
            };
            if (plugin is not null)
            {
                next.Add(plugin);
            }

            // Add before removing, so no lookup ever finds the brushes missing.
            var merged = Application.Current.Resources.MergedDictionaries;
            foreach (ResourceDictionary dictionary in next)
            {
                merged.Add(dictionary);
            }

            foreach (ResourceDictionary old in _merged)
            {
                merged.Remove(old);
            }

            _merged = next;
            _signature = signature;
        }

        _plugin = plugin;
        _pluginSource = plugin is null ? null : source;
        ActivePluginTheme = entry?.Key;
        IsLight = light;

        // A theme that defines its own accent keeps it; otherwise the user's accent colour is laid over the theme.
        ApplyAccent(plugin is not null && Defines(plugin, "AccentBrush") ? null : GlowColor.FromName(settings.AccentColor));
        Changed?.Invoke();
    }

    /// <summary>A brush in <paramref name="color"/>, deepened in the light theme so it reads on the light island.</summary>
    public static SolidColorBrush Brush(GlowColor color, byte alpha = 0xFF)
    {
        if (IsLight)
        {
            color = color.OnLight();
        }

        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// How the terminal should look under the plugin theme in effect: xterm colours taken from the
    /// theme's brushes, and its code font. Null under Notch's own themes, whose colours the page has built in.
    /// </summary>
    public static (JsonObject Palette, System.Drawing.Color? Background)? TerminalPalette()
    {
        if (_plugin is null)
        {
            return null;
        }

        var palette = new JsonObject();
        System.Drawing.Color? background = null;
        if ((SolidColor("TerminalBackgroundBrush") ?? SolidColor("IslandBrush")) is { } back)
        {
            palette["background"] = Hex(back);
            background = System.Drawing.Color.FromArgb(back.R, back.G, back.B);
        }

        Color? foreground = SolidColor("TerminalForegroundBrush") ?? SolidColor("TextBrush");
        if (foreground is { } fore)
        {
            palette["foreground"] = Hex(fore);
        }

        if ((SolidColor("TerminalCursorBrush") ?? foreground) is { } cursor)
        {
            palette["cursor"] = Hex(cursor);
        }

        if (SolidColor("TerminalSelectionBrush") is { } selection)
        {
            palette["selectionBackground"] = Hex(selection);
        }

        for (int i = 0; i < AnsiNames.Length; i++)
        {
            if (SolidColor($"TerminalAnsi{i}Brush") is { } ansi)
            {
                palette[AnsiNames[i]] = Hex(ansi);
            }
        }

        if (Application.Current.TryFindResource("CodeFontFamily") is FontFamily code)
        {
            palette["fontFamily"] = string.Join(", ", code.Source.Split(',').Select(name => $"\"{name.Trim()}\"")) + ", monospace";
        }

        return (palette, background);

        static Color? SolidColor(string key) =>
            Application.Current.TryFindResource(key) is SolidColorBrush brush ? brush.Color : null;

        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>The scale a theme asks for on the island's corner radius, kept to something that still looks like a notch.</summary>
    public static double IslandRadiusScale =>
        Application.Current.TryFindResource("IslandRadiusScale") is double scale ? Math.Clamp(scale, 0, 2) : 1;

    // Set on the application itself, where they win over the theme dictionary's uncoloured ones.
    private static void ApplyAccent(GlowColor? accent)
    {
        ResourceDictionary resources = Application.Current.Resources;
        if (accent is { } color)
        {
            resources["AccentBrush"] = Brush(color);
            resources["AccentHoverBrush"] = Brush(color, 0x59);
            resources["AccentPressedBrush"] = Brush(color, 0x80);
        }
        else
        {
            resources.Remove("AccentBrush");
            resources.Remove("AccentHoverBrush");
            resources.Remove("AccentPressedBrush");
        }
    }

    private static string Stamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Reads the theme's XAML. Sets <see cref="Problem"/> and returns null when it cannot be used.</summary>
    private static ResourceDictionary? LoadPluginTheme(PluginThemeEntry entry)
    {
        try
        {
            using FileStream stream = File.OpenRead(entry.FilePath);

            // The base address lets the file refer to images, fonts and further dictionaries next to it.
            var context = new ParserContext { BaseUri = new Uri(entry.FilePath) };
            if (XamlReader.Load(stream, context) is not ResourceDictionary dictionary)
            {
                Problem = $"The theme '{entry.Name}' is not usable: the root element of {Path.GetFileName(entry.FilePath)} must be a ResourceDictionary.";
                return null;
            }

            if (Validate(dictionary) is { } mistake)
            {
                Problem = $"The theme '{entry.Name}' is not usable: {mistake}";
                return null;
            }

            return dictionary;
        }
        catch (Exception e)
        {
            // The file is a plugin's, and XAML can fail in many ways (a bad file, a missing font or image,
            // a type that does not exist); none of them may take the notch down.
            Problem = $"The theme '{entry.Name}' could not be loaded: {e.Message}";
            return null;
        }
    }

    /// <summary>The first thing wrong with a theme's keys, or null. A key of the wrong type would throw as soon as something used it.</summary>
    private static string? Validate(ResourceDictionary dictionary)
    {
        foreach (object key in dictionary.Keys)
        {
            if (key is not string name)
            {
                continue;
            }

            object? value = dictionary[name];
            if (StyleTargets.TryGetValue(name, out Type? target))
            {
                if (value is not Style style || style.TargetType != target)
                {
                    return $"\"{name}\" must be a Style with TargetType {target.Name}.";
                }
            }
            else if (ExpectedType(name) is { } expected && !expected.IsInstanceOfType(value))
            {
                return $"\"{name}\" must be a {expected.Name}.";
            }
        }

        return dictionary.MergedDictionaries.Select(Validate).FirstOrDefault(m => m is not null);
    }

    private static Type? ExpectedType(string name) => name switch
    {
        "UiFontSize" or "IslandRadiusScale" => typeof(double),
        "IconFont" => typeof(FontFamily),
        _ when name.EndsWith("Brush", StringComparison.Ordinal) => typeof(Brush),
        _ when name.EndsWith("FontFamily", StringComparison.Ordinal) => typeof(FontFamily),
        _ when name.EndsWith("Radius", StringComparison.Ordinal) => typeof(CornerRadius),
        _ => null,
    };

    private static bool Defines(ResourceDictionary dictionary, string key) =>
        dictionary.Contains(key) || dictionary.MergedDictionaries.Any(d => Defines(d, key));
}
