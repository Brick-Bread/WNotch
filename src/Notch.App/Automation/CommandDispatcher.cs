using System.Windows;
using System.Windows.Threading;
using Notch.App.Plugins;
using Notch.App.Shell;
using Notch.Core.Automation;
using Notch.Core.Plugins;
using Notch.Core.Widgets;

namespace Notch.App.Automation;

/// <summary>
/// Carries out what outside callers ask for: <c>notch://</c> links, <c>notchctl</c> and the webhook
/// all arrive here as a <see cref="NotchCommand"/>. Runs everything on the UI thread.
/// </summary>
internal sealed class CommandDispatcher(
    Dispatcher ui,
    NotchWindow window,
    PluginManager plugins,
    PluginInstallFlow install,
    Action openSettings,
    Action openPalette)
{
    /// <summary>
    /// Handles the arguments a second copy of Notch or <c>notchctl</c> sent through the command pipe.
    /// No arguments, or only start-up switches, just bring the notch up.
    /// </summary>
    public Task<CommandResult> HandleArgumentsAsync(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            return ui.InvokeAsync(() =>
            {
                if (arguments.Contains("--settings", StringComparer.OrdinalIgnoreCase))
                {
                    openSettings();
                }
                else
                {
                    window.RevealNotch();
                }

                return new CommandResult(true);
            }).Task;
        }

        bool understood = arguments[0].StartsWith(CommandParser.Scheme + ":", StringComparison.OrdinalIgnoreCase)
            ? CommandParser.TryParseUrl(arguments[0], out NotchCommand? command, out string? error)
            : CommandParser.TryParseArguments(arguments, out command, out error);
        return understood ? RunAsync(command!) : Task.FromResult(new CommandResult(false, error));
    }

    /// <summary>Does what the command says. Safe to call from any thread.</summary>
    public async Task<CommandResult> RunAsync(NotchCommand command)
    {
        return await await ui.InvokeAsync(async () =>
        {
            switch (command)
            {
                case InstallPluginCommand install1:
                    return await install.RequestInstallAsync(install1.PluginId);

                case SetPluginEnabledCommand toggle:
                    return SetPluginEnabled(toggle);

                case StartTimerCommand timer:
                    window.StartTimer(timer.Duration);
                    return new CommandResult(true, $"Timer started: {DurationParser.Describe(timer.Duration)}.");

                case TimerActionCommand action:
                    window.ControlTimer(action.Action);
                    return new CommandResult(true);

                case OpenTabCommand tab:
                    window.OpenOnTab(tab.Tab);
                    return new CommandResult(true);

                case ToastCommand toast:
                    window.ShowNotice(toast);
                    return new CommandResult(true);

                case OpenPaletteCommand:
                    openPalette();
                    return new CommandResult(true);

                case OpenSettingsCommand:
                    openSettings();
                    return new CommandResult(true);

                default:
                    return new CommandResult(false, "Unknown command.");
            }
        });
    }

    private CommandResult SetPluginEnabled(SetPluginEnabledCommand toggle)
    {
        PluginInfo? plugin = plugins.Plugins.FirstOrDefault(p => p.Id == toggle.PluginId);
        if (plugin is null)
        {
            return new CommandResult(false, $"{toggle.PluginId} is not installed.");
        }

        // Switching a plugin on runs its code, so that is always the user's own decision.
        if (toggle.Enabled
            && MessageBox.Show(
                $"Something asked to switch on the plugin \"{plugin.Name}\". Plugins run as you, with access to everything you can reach. Switch it on?",
                "Notch",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return new CommandResult(false, "Cancelled.");
        }

        if (toggle.Enabled)
        {
            install.Enable(toggle.PluginId);
        }
        else
        {
            install.Disable(toggle.PluginId);
        }

        return new CommandResult(true);
    }
}
