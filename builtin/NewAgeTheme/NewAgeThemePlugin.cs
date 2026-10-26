using Notch.Core.Plugins;

namespace NewAgeTheme;

/// <summary>
/// The New Age theme, which ships with Notch: Notch.App.csproj copies this plugin's build into
/// the app's own plugins folder, where it runs without being installed or switched on. The look
/// itself is themes/new-age.xaml.
/// </summary>
public sealed class NewAgeThemePlugin : INotchPlugin
{
    public void Start(IPluginHost host) =>
        host.Themes.Set(new PluginTheme
        {
            Id = "new-age",
            Name = "New Age",
            Description = "Navy-black with a cyan to violet glow, round capsules and Bricolage Grotesque.",
            File = "themes/new-age.xaml",
            Base = PluginThemeBase.Dark,
        });

    // Notch removes the theme when the plugin stops.
    public void Stop()
    {
    }
}
