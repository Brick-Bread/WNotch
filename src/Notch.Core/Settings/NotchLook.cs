namespace Notch.Core.Settings;

/// <summary>How the island is drawn, over the dark or light colours <see cref="NotchTheme"/> picks.</summary>
public enum NotchLook
{
    /// <summary>A solid black or light island, as the Dynamic Island.</summary>
    Classic,

    /// <summary>Translucent glass with a lit rim; the desktop shows through it, blurred where Windows allows.</summary>
    Frosted,

    /// <summary>Warm paper with ink, or dark paper with cream ink; serif figures and a burnt-orange edge.</summary>
    Paper,

    /// <summary>Smoky glass with a one-pixel outline; panels have no fill and are set apart by hairlines.</summary>
    Hairline,
}

public static class NotchLooks
{
    /// <summary>Whether the island is see-through in <paramref name="look"/>, so the desktop behind it can be blurred.</summary>
    public static bool IsGlass(NotchLook look) => look is NotchLook.Frosted or NotchLook.Hairline;

    /// <summary>
    /// The name of the look's resource dictionary for the dark or light colours, such as "FrostedLight",
    /// or null for <see cref="NotchLook.Classic"/> and anything unknown, which are the colours alone.
    /// </summary>
    public static string? DictionaryName(NotchLook look, bool light) =>
        look is NotchLook.Frosted or NotchLook.Paper or NotchLook.Hairline
            ? $"{look}{(light ? "Light" : "Dark")}"
            : null;
}
