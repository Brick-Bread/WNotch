namespace Notch.Core.Settings;

public enum NotchTheme
{
    /// <summary>Black island with light text.</summary>
    Dark,

    /// <summary>Light island with dark text.</summary>
    Light,

    /// <summary>Whichever of the two Windows is set to use for apps.</summary>
    System,
}

public static class NotchThemes
{
    /// <summary>Whether <paramref name="theme"/> comes out light, given what Windows is set to.</summary>
    public static bool IsLight(NotchTheme theme, bool systemIsLight) =>
        theme == NotchTheme.Light || (theme == NotchTheme.System && systemIsLight);
}
