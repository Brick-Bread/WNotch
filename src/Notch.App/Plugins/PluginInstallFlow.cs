using System.Net.Http;
using System.Windows;
using Notch.Core.Activities;
using Notch.Core.Automation;
using Notch.Core.Plugins;
using Notch.Core.Settings;

namespace Notch.App.Plugins;

/// <summary>
/// Installing a plugin from the plugin list: look the id up, ask the user, download, and
/// optionally switch it on. The only way in from outside (a <c>notch://install</c> link) is an
/// id, and the repository always comes from the registry, never from the caller.
/// </summary>
public sealed class PluginInstallFlow(
    HttpClient http,
    PluginInstaller installer,
    PluginManager plugins,
    AppSettings settings,
    SettingsStore store,
    ActivityManager activities,
    string registryCacheFile)
{
    private static readonly TimeSpan RegistryLifetime = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private PluginRegistry? _registry;
    private DateTime _registryLoadedAt;

    /// <summary>Raised on the UI thread after a plugin was installed, so open windows can show it.</summary>
    public event Action<string>? Installed;

    /// <summary>The plugin list from the website, remembered for a few minutes.</summary>
    public async Task<PluginRegistry> GetRegistryAsync(bool forceReload = false)
    {
        if (_registry is null || forceReload || DateTime.UtcNow - _registryLoadedAt > RegistryLifetime)
        {
            _registry = await PluginRegistry.LoadAsync(http, registryCacheFile);
            _registryLoadedAt = DateTime.UtcNow;
        }

        return _registry;
    }

    /// <summary>A <c>notch://install</c> link or <c>notchctl install</c>: ask, then install. Call on the UI thread.</summary>
    public async Task<CommandResult> RequestInstallAsync(string pluginId)
    {
        RegistryEntry? entry;
        try
        {
            entry = (await GetRegistryAsync()).Find(pluginId);
        }
        catch (PluginLoadException e)
        {
            return new CommandResult(false, e.Message);
        }

        return entry is null
            ? new CommandResult(false, $"{pluginId} is not in the plugin list.")
            : await InstallAsync(entry, owner: null);
    }

    /// <summary>Asks the user about <paramref name="entry"/> and installs it if they agree. Call on the UI thread.</summary>
    public async Task<CommandResult> InstallAsync(RegistryEntry entry, Window? owner)
    {
        // A second link while the question is open would stack windows; only one at a time.
        if (!await _oneAtATime.WaitAsync(0))
        {
            return new CommandResult(false, "Another plugin is waiting for an answer.");
        }

        try
        {
            string? installedVersion = plugins.Plugins.FirstOrDefault(p => p.Id == entry.Id)?.Version ?? (plugins.Plugins.Any(p => p.Id == entry.Id) ? "" : null);
            var window = new PluginConfirmWindow(entry, installedVersion);
            if (owner is { IsLoaded: true })
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            if (window.ShowDialog() != true || window.Choice == PluginConfirmChoice.Cancel)
            {
                return new CommandResult(false, "Cancelled.");
            }

            PluginInstallResult result;
            try
            {
                result = await Task.Run(() => installer.InstallAsync(entry.Repository));
            }
            catch (PluginLoadException e)
            {
                MessageBox.Show(e.Message, "Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return new CommandResult(false, e.Message);
            }

            // The registry named this id; the download must really be that plugin.
            if (result.Manifest.Id != entry.Id)
            {
                string message = $"The release of {entry.Repository} holds a plugin called {result.Manifest.Id}, not {entry.Id}. It was installed but not switched on.";
                MessageBox.Show(message, "Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return new CommandResult(false, message);
            }

            if (window.Choice == PluginConfirmChoice.InstallAndEnable)
            {
                Enable(entry.Id);
            }

            Installed?.Invoke(entry.Id);
            string text = $"{result.Manifest.Name} installed" + (window.Choice == PluginConfirmChoice.InstallAndEnable ? " and switched on" : ". Switch it on in settings");
            if (result.Pending)
            {
                text += ". The new version starts the next time Notch does";
            }

            activities.Publish(new Activity
            {
                Id = "notice.plugin-installed",
                Tier = ActivityTier.Transient,
                Title = text.Split(". ")[0],
                Detail = result.Manifest.Name,
                Glyph = "",
                Glow = new Glow(GlowColor.Green, GlowPattern.Flash),
                Lifetime = TimeSpan.FromSeconds(5),
            });
            return new CommandResult(true, text + ".");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Switches an installed plugin on, as ticking it in settings and saving would.</summary>
    public void Enable(string pluginId)
    {
        if (!settings.EnabledPlugins.Contains(pluginId))
        {
            settings.EnabledPlugins.Add(pluginId);
        }

        store.Save(settings);
        string[] enabled = [.. settings.EnabledPlugins];
        _ = Task.Run(() =>
        {
            plugins.Discover();
            plugins.SetEnabled(enabled);
        });
    }

    /// <summary>Switches an installed plugin off.</summary>
    public void Disable(string pluginId)
    {
        settings.EnabledPlugins.RemoveAll(id => id == pluginId);
        store.Save(settings);
        string[] enabled = [.. settings.EnabledPlugins];
        _ = Task.Run(() => plugins.SetEnabled(enabled));
    }
}
