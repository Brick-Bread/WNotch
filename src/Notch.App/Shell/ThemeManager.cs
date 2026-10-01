using System.Windows;
using System.Windows.Media;
using Notch.Core.Activities;
using Notch.Core.Settings;
using Notch.Platform.Display;

namespace Notch.App.Shell;

/// <summary>
/// Switches the app between the dark and light brush sets in Themes/. Everything in XAML
/// refers to those brushes with DynamicResource, so swapping the dictionary recolours it live.
/// </summary>
internal static class ThemeManager
{
    private static ResourceDictionary? _current;

    /// <summary>
    /// Used instead of the saved theme for this run (<c>--theme=</c>, for development), so a
    /// debug build can be looked at in either theme without changing the settings file.
    /// </summary>
    public static NotchTheme? Override { get; set; }

    /// <summary>Whether the light theme is the one showing.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Raised on the UI thread after every <see cref="Apply"/>, for whatever is coloured outside XAML.</summary>
    public static event Action? Changed;

    /// <summary>Puts the theme the settings ask for into effect. Call again when the settings or the Windows theme change.</summary>
    public static void Apply(AppSettings settings)
    {
        bool light = NotchThemes.IsLight(Override ?? settings.Theme, SystemTheme.AppsUseLightTheme);
        if (_current is null || light != IsLight)
        {
            var next = new ResourceDictionary
            {
                Source = new Uri($"/Notch;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative),
            };

            // Add before removing, so no lookup ever finds the brushes missing.
            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Add(next);
            if (_current is not null)
            {
                merged.Remove(_current);
            }

            _current = next;
            IsLight = light;
        }

        ApplyAccent(GlowColor.FromName(settings.AccentColor));
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
}
