using Notch.Core.Plugins;

namespace NeonTheme;

/// <summary>
/// The sample theme from docs/plugins.md: a plugin whose only job is to offer a look for the
/// whole notch. The look itself is themes/neon.xaml.
/// </summary>
public sealed class NeonThemePlugin : INotchPlugin
{
    public void Start(IPluginHost host) =>
        host.Themes.Set(new PluginTheme
        {
            Id = "neon",
            Name = "Neon",
            Description = "Purple and pink on deep indigo, with square corners.",
            File = "themes/neon.xaml",
            Base = PluginThemeBase.Dark,
        });

    // Notch removes the theme when the plugin stops.
    public void Stop()
    {
    }
}
