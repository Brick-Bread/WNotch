using System.Windows;
using System.Windows.Controls;
using Notch.Core.Plugins;

namespace Notch.App.Plugins;

/// <summary>Lists the plugins from the website. Installing one asks first, the same as following an install link.</summary>
public partial class PluginBrowserWindow : Window
{
    private readonly PluginRegistry _registry;
    private readonly PluginInstallFlow _flow;
    private readonly PluginManager _plugins;

    public PluginBrowserWindow(PluginRegistry registry, PluginInstallFlow flow, PluginManager plugins)
    {
        InitializeComponent();
        _registry = registry;
        _flow = flow;
        _plugins = plugins;

        Search.TextChanged += (_, _) => ShowRows();
        ShowRows();
    }

    private void ShowRows()
    {
        string filter = Search.Text.Trim();
        RegistryEntry[] shown = [.. _registry.Entries.Where(e => Matches(e, filter))];
        Summary.Text = _registry.Entries.Count == 0
            ? "The plugin list is empty."
            : shown.Length == 0 ? "No plugin matches." : $"{shown.Length} of {_registry.Entries.Count} plugins";

        Rows.Children.Clear();
        foreach (RegistryEntry entry in shown)
        {
            Rows.Children.Add(Row(entry));
        }
    }

    private UIElement Row(RegistryEntry entry)
    {
        bool installed = _plugins.Plugins.Any(p => p.Id == entry.Id);
        var button = new Button { Content = installed ? "Reinstall" : "Install", MinWidth = 80, VerticalAlignment = VerticalAlignment.Top };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                CommandResultText(await _flow.InstallAsync(entry, this));
                ShowRows();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = entry.Name + (entry.Verified ? "  ✔ checked" : ""),
            FontWeight = FontWeights.SemiBold,
        });
        text.Children.Add(new TextBlock { Text = (entry.Author is null ? "" : entry.Author + "  ·  ") + entry.Repository, Opacity = 0.65 });
        if (entry.Description is not null)
        {
            text.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }

        if (entry.Permissions.Count > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = "Says it uses: " + string.Join(", ", entry.Permissions),
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(text);
        return row;
    }

    private void CommandResultText(Notch.Core.Automation.CommandResult result)
    {
        if (result is { Ok: true, Message: { } message })
        {
            Summary.Text = message;
        }
    }

    private static bool Matches(RegistryEntry entry, string filter) =>
        filter.Length == 0
        || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (entry.Author?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (entry.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || entry.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
