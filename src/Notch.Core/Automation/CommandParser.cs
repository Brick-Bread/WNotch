using System.Text.RegularExpressions;
using Notch.Core.Activities;
using Notch.Core.Widgets;

namespace Notch.Core.Automation;

/// <summary>
/// Turns what outside callers send into a <see cref="NotchCommand"/>, or refuses it. Every
/// entry point (link, command line, webhook) goes through <see cref="Build"/>, so they all accept
/// exactly the same things. Anything unknown, oversized or malformed is refused, never guessed at.
/// </summary>
public static partial class CommandParser
{
    public const string Scheme = "notch";

    /// <summary>The longest <c>notch://</c> link or command line that is read at all.</summary>
    public const int MaxInputLength = 2048;

    public const int MaxTitleLength = 120;
    public const int MaxDetailLength = 300;
    public const int MaxGlyphLength = 8;
    public static readonly TimeSpan MaxTimer = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxToastLifetime = TimeSpan.FromSeconds(30);

    private static readonly string[] Tabs = ["home", "terminal", "stats", "shelf", "plugins", "clipboard"];

    /// <summary>Reads <c>notch://verb?name=value&amp;...</c>.</summary>
    public static bool TryParseUrl(string? url, out NotchCommand? command, out string? error)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxInputLength)
        {
            error = "That link is empty or too long.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = "That is not a notch:// link.";
            return false;
        }

        // notch://install?id=x puts the verb in the host; notch:install?id=x and notch:///install do not parse the same way.
        string verb = uri.Host.Length > 0 ? uri.Host : uri.AbsolutePath.Trim('/');
        return Build(verb, ParseQuery(uri.Query), out command, out error);
    }

    /// <summary>
    /// Reads the command line tool's arguments: <c>timer 5m</c>, <c>toast "Build done" --detail ok</c>,
    /// <c>install acme.tool</c>. The first positional argument after the verb fills the verb's main parameter.
    /// </summary>
    public static bool TryParseArguments(IReadOnlyList<string> arguments, out NotchCommand? command, out string? error)
    {
        command = null;
        if (arguments.Count == 0)
        {
            error = "Say what to do, e.g. timer 5m.";
            return false;
        }

        if (arguments.Sum(a => a.Length) > MaxInputLength)
        {
            error = "That is too long.";
            return false;
        }

        string verb = arguments[0].ToLowerInvariant();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? main = MainParameter(verb);
        for (int i = 1; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                string name = argument[2..];
                int equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    values[name[..equals]] = name[(equals + 1)..];
                }
                else if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values[name] = arguments[++i];
                }
                else
                {
                    values[name] = "true";
                }
            }
            else if (main is not null && !values.ContainsKey(main))
            {
                values[main] = argument;
            }
        }

        return Build(verb, values, out command, out error);
    }

    /// <summary>Builds a command from a verb and its named values (case-insensitive names).</summary>
    public static bool Build(string? verb, IReadOnlyDictionary<string, string> values, out NotchCommand? command, out string? error)
    {
        command = null;
        error = null;
        string Get(string name) => values.TryGetValue(name, out string? value) ? value.Trim() : string.Empty;

        switch (verb?.Trim().ToLowerInvariant())
        {
            case "install":
                if (!IsPluginId(Get("id")))
                {
                    error = "install needs a plugin id.";
                    return false;
                }

                command = new InstallPluginCommand(Get("id"));
                return true;

            case "plugin":
                if (!IsPluginId(Get("id")))
                {
                    error = "plugin needs a plugin id.";
                    return false;
                }

                if (!TryBool(Get("enable"), out bool enabled))
                {
                    error = "plugin needs enable=true or enable=false.";
                    return false;
                }

                command = new SetPluginEnabledCommand(Get("id"), enabled);
                return true;

            case "timer":
                return BuildTimer(Get, out command, out error);

            case "open":
                string tab = Get("tab").ToLowerInvariant();
                if (!Tabs.Contains(tab))
                {
                    error = $"open needs a tab: {string.Join(", ", Tabs)}.";
                    return false;
                }

                command = new OpenTabCommand(tab);
                return true;

            case "toast":
                return BuildToast(Get, out command, out error);

            case "palette":
                command = new OpenPaletteCommand();
                return true;

            case "settings":
                command = new OpenSettingsCommand();
                return true;

            default:
                error = "Unknown command.";
                return false;
        }
    }

    /// <summary>The parameter a bare argument after the verb stands for.</summary>
    private static string? MainParameter(string verb) => verb switch
    {
        "install" or "plugin" => "id",
        "timer" => "d",
        "open" => "tab",
        "toast" => "title",
        _ => null,
    };

    private static bool BuildTimer(Func<string, string> get, out NotchCommand? command, out string? error)
    {
        command = null;
        error = null;
        string text = get("d");
        if (text.Length == 0)
        {
            text = get("duration");
        }

        if (text.Length == 0)
        {
            text = get("action");
        }

        switch (text.ToLowerInvariant())
        {
            case "stop" or "reset" or "cancel":
                command = new TimerActionCommand(TimerAction.Stop);
                return true;
            case "pause":
                command = new TimerActionCommand(TimerAction.Pause);
                return true;
            case "resume":
                command = new TimerActionCommand(TimerAction.Resume);
                return true;
        }

        if (!DurationParser.TryParse(text, out TimeSpan duration) || duration <= TimeSpan.Zero)
        {
            error = "timer needs a duration like 5m, 90s or 1h20m, or stop, pause or resume.";
            return false;
        }

        if (duration > MaxTimer)
        {
            error = "A timer can be at most 24 hours.";
            return false;
        }

        command = new StartTimerCommand(duration);
        return true;
    }

    private static bool BuildToast(Func<string, string> get, out NotchCommand? command, out string? error)
    {
        command = null;
        error = null;
        string title = get("title");
        if (title.Length == 0)
        {
            title = get("text");
        }

        if (title.Length == 0 || title.Length > MaxTitleLength)
        {
            error = $"toast needs a title of 1 to {MaxTitleLength} characters.";
            return false;
        }

        string detail = get("detail");
        if (detail.Length > MaxDetailLength)
        {
            error = $"The detail can be at most {MaxDetailLength} characters.";
            return false;
        }

        string glyph = get("glyph");
        if (glyph.Length > MaxGlyphLength)
        {
            error = "The glyph is one symbol.";
            return false;
        }

        GlowColor? color = null;
        if (get("color") is { Length: > 0 } colorName)
        {
            color = ParseColor(colorName);
            if (color is null)
            {
                error = "The colour is a name like Green, or #rrggbb.";
                return false;
            }
        }

        TimeSpan? lifetime = null;
        if (get("lifetime") is { Length: > 0 } lifetimeText)
        {
            if (!DurationParser.TryParse(lifetimeText, out TimeSpan parsed) || parsed <= TimeSpan.Zero)
            {
                error = "The lifetime is a duration like 5s.";
                return false;
            }

            lifetime = parsed > MaxToastLifetime ? MaxToastLifetime : parsed;
        }

        command = new ToastCommand(title, detail.Length > 0 ? detail : null, glyph.Length > 0 ? glyph : null, color, lifetime);
        return true;
    }

    /// <summary>A colour name from <see cref="GlowColor.Named"/> or <c>#rrggbb</c>.</summary>
    public static GlowColor? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.Length == 7 && text[0] == '#'
            && byte.TryParse(text.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(text.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(text.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return new GlowColor(r, g, b);
        }

        return GlowColor.FromName(text);
    }

    /// <summary>The id rule plugin manifests use: lowercase letters and digits in groups split by dots or dashes.</summary>
    public static bool IsPluginId(string? id) => id is { Length: > 0 and <= 64 } && PluginIdPattern().IsMatch(id);

    private static bool TryBool(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "on":
                value = true;
                return true;
            case "false" or "0" or "no" or "off":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string name = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            string value = equals < 0 ? "true" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values.TryAdd(name, value);
        }

        return values;
    }

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex PluginIdPattern();
}
