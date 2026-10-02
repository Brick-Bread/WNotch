using System.Windows;
using System.Windows.Controls;
using Notch.Core.Plugins;

namespace Notch.App.Plugins;

public enum PluginConfirmChoice
{
    Cancel,
    Install,
    InstallAndEnable,
}

/// <summary>
/// Asks before a plugin from the plugin list is installed: who made it, where it comes from and
/// what it says it uses. Nothing is downloaded until the user presses a button here.
/// </summary>
public partial class PluginConfirmWindow : Window
{
    public PluginConfirmWindow(RegistryEntry entry, string? installedVersion)
    {
        InitializeComponent();

        PluginName.Text = entry.Name;
        ByLine.Text = (entry.Author is null ? "" : $"by {entry.Author}  ·  ") + $"github.com/{entry.Repository}";
        Verified.Visibility = entry.Verified ? Visibility.Visible : Visibility.Collapsed;
        Description.Text = entry.Description ?? "";
        Description.Visibility = entry.Description is null ? Visibility.Collapsed : Visibility.Visible;

        if (entry.Permissions.Count == 0)
        {
            PermissionList.Children.Add(new TextBlock { Text = "It does not say. Treat it as able to do anything.", Opacity = 0.7 });
        }
        else
        {
            foreach (string permission in entry.Permissions)
            {
                PermissionList.Children.Add(new TextBlock
                {
                    Text = "•  " + PluginManifest.DescribePermission(permission),
                    Margin = new Thickness(0, 1, 0, 1),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        if (installedVersion is not null)
        {
            Title = "Reinstall plugin";
            InstallOnly.Content = "Reinstall";
            ShowProblem($"This plugin is already installed{(installedVersion.Length > 0 ? $" ({installedVersion})" : "")}. Installing again replaces it with the latest release.", blocking: false);
        }

        if (entry.ApiVersion > PluginApi.Version)
        {
            ShowProblem($"This plugin needs a newer Notch (plugin API {entry.ApiVersion}; this Notch has {PluginApi.Version}). Update Notch first.", blocking: true);
        }

        InstallOnly.Click += (_, _) => Choose(PluginConfirmChoice.Install);
        InstallAndEnable.Click += (_, _) => Choose(PluginConfirmChoice.InstallAndEnable);
    }

    public PluginConfirmChoice Choice { get; private set; }

    private void ShowProblem(string text, bool blocking)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
        if (blocking)
        {
            InstallOnly.IsEnabled = InstallAndEnable.IsEnabled = false;
        }
    }

    private void Choose(PluginConfirmChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
