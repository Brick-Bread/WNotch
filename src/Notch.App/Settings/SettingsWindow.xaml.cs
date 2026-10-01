using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Notch.App.Shell;
using Notch.Core.Activities;
using Notch.Core.Plugins;
using Notch.Core.Settings;
using Notch.Core.Widgets;
using Notch.Platform.Display;
using Notch.Platform.Startup;

namespace Notch.App.Settings;

/// <summary>Edits <see cref="AppSettings"/> in place. <see cref="Window.DialogResult"/> is not used; listen to <see cref="Saved"/>.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly PluginManager _plugins;
    private readonly PluginInstaller _installer;
    private NotchTheme _shownTheme;

    public SettingsWindow(AppSettings settings, SettingsStore store, PluginManager plugins, PluginInstaller installer)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _plugins = plugins;
        _installer = installer;
        MaxHeight = SystemParameters.WorkArea.Height;

        // The theme can also change from the tray menu while this window is open.
        ShowTheme();
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;

        ListAccents(settings.AccentColor);
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        ExpandOnHover.IsChecked = settings.ExpandOnHover;
        HideInFullscreen.IsChecked = settings.HideInFullscreen;
        AutoUpdate.IsChecked = settings.AutoUpdate;
        GlowEffects.IsChecked = settings.GlowEffects;
        GlowIntensity.Minimum = GlowOutput.MinPercent;
        GlowIntensity.Maximum = GlowOutput.MaxPercent;
        GlowIntensity.ValueChanged += (_, _) => GlowIntensityText.Text = $"{GlowIntensity.Value:0}%";
        GlowIntensity.Value = Math.Clamp(settings.GlowIntensity, GlowOutput.MinPercent, GlowOutput.MaxPercent);
        TimerPresets.Text = string.Join(Environment.NewLine, settings.Timers().Select(preset => preset.ToLine()));
        PomodoroFocus.Text = settings.PomodoroFocusMinutes.ToString();
        PomodoroShortBreak.Text = settings.PomodoroShortBreakMinutes.ToString();
        PomodoroLongBreak.Text = settings.PomodoroLongBreakMinutes.ToString();
        ShowMedia.IsChecked = settings.ShowMedia;
        ShowVolume.IsChecked = settings.ShowVolume;
        ShowBrightness.IsChecked = settings.ShowBrightness;
        ShowPower.IsChecked = settings.ShowPower;
        ShowBluetooth.IsChecked = settings.ShowBluetooth;
        CalendarFeeds.Text = string.Join(Environment.NewLine, settings.CalendarFeeds);

        // First entry follows whichever display Windows treats as primary.
        IReadOnlyList<DisplayInfo> displays = Displays.GetAll();
        Display.Items.Add("Primary display");
        for (int i = 0; i < displays.Count; i++)
        {
            DisplayInfo display = displays[i];
            Display.Items.Add($"Display {i + 1} ({display.Bounds.Width} × {display.Bounds.Height}{(display.IsPrimary ? ", primary" : "")})");
        }

        Display.SelectedIndex = settings.DisplayIndex is { } index && index < displays.Count ? index + 1 : 0;

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.ToString(3)}";

        ListPlugins([.. settings.EnabledPlugins]);
        InstallPlugin.Click += (_, _) => OnInstallPlugin();
        PluginSource.KeyDown += (_, e) =>
        {
            // Enter in this box installs; without this it would press Save and close the window.
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OnInstallPlugin();
            }
        };
        OpenPluginsFolder.Click += (_, _) => OnOpenPluginsFolder();

        Save.Click += (_, _) => OnSave();
        Cancel.Click += (_, _) => Close();
    }

    /// <summary>Raised after the settings object was updated and written to disk.</summary>
    public event EventHandler? Saved;

    private void OnThemeChanged()
    {
        if (_settings.Theme != _shownTheme)
        {
            ShowTheme();
        }
    }

    /// <summary>Shows the saved theme in the list and puts this window in it.</summary>
    private void ShowTheme()
    {
        _shownTheme = _settings.Theme;
        Theme.SelectedIndex = (int)_shownTheme;
        ThemeMode = _shownTheme switch
        {
            NotchTheme.Light => ThemeMode.Light,
            NotchTheme.System => ThemeMode.System,
            _ => ThemeMode.Dark,
        };
    }

    /// <param name="chosen">Name of the accent to show as picked; an unknown name picks "no accent".</param>
    private void ListAccents(string chosen)
    {
        var style = (Style)AccentSwatches.FindResource("Swatch");
        bool known = GlowColor.FromName(chosen) is not null;

        foreach ((string name, GlowColor color) in GlowColor.Named)
        {
            AccentSwatches.Children.Add(new RadioButton
            {
                Style = style,
                Tag = name,
                ToolTip = name,
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                IsChecked = string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase),
            });
        }

        // The last swatch switches the accent off; it is drawn in the window's own text colour.
        var none = new RadioButton { Style = style, Tag = AppSettings.NoAccent, ToolTip = "No accent colour", IsChecked = !known };
        none.SetBinding(BackgroundProperty, new Binding(nameof(Foreground)) { Source = this });
        AccentSwatches.Children.Add(none);
    }

    private void OnSave()
    {
        if (!TryReadTimerPresets(out List<TimerPreset> presets))
        {
            return;
        }

        _settings.TimerPresets = presets;
        _settings.ExpandOnHover = ExpandOnHover.IsChecked == true;
        _settings.Theme = (NotchTheme)Math.Max(0, Theme.SelectedIndex);
        _settings.AccentColor = AccentSwatches.Children.OfType<RadioButton>()
            .FirstOrDefault(swatch => swatch.IsChecked == true)?.Tag as string ?? _settings.AccentColor;
        _settings.HideInFullscreen = HideInFullscreen.IsChecked == true;
        _settings.AutoUpdate = AutoUpdate.IsChecked == true;
        _settings.GlowEffects = GlowEffects.IsChecked == true;
        _settings.GlowIntensity = (int)Math.Round(GlowIntensity.Value);
        _settings.PomodoroFocusMinutes = ReadMinutes(PomodoroFocus.Text, _settings.PomodoroFocusMinutes);
        _settings.PomodoroShortBreakMinutes = ReadMinutes(PomodoroShortBreak.Text, _settings.PomodoroShortBreakMinutes);
        _settings.PomodoroLongBreakMinutes = ReadMinutes(PomodoroLongBreak.Text, _settings.PomodoroLongBreakMinutes);
        _settings.ShowMedia = ShowMedia.IsChecked == true;
        _settings.ShowVolume = ShowVolume.IsChecked == true;
        _settings.ShowBrightness = ShowBrightness.IsChecked == true;
        _settings.ShowPower = ShowPower.IsChecked == true;
        _settings.ShowBluetooth = ShowBluetooth.IsChecked == true;
        _settings.DisplayIndex = Display.SelectedIndex > 0 ? Display.SelectedIndex - 1 : null;
        _settings.CalendarFeeds = [.. CalendarFeeds.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        _settings.EnabledPlugins = [.. TickedPlugins()];
        _store.Save(_settings);

        try
        {
            StartupRegistration.SetEnabled(StartWithWindows.IsChecked == true);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            MessageBox.Show(this, "Could not change the start-with-Windows setting: " + e.Message, "Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>Reads the presets box. Says which line is wrong, and returns false, when one cannot be read.</summary>
    private bool TryReadTimerPresets(out List<TimerPreset> presets)
    {
        presets = [];
        string[] lines = TimerPresets.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string line in lines.Take(AppSettings.MaxTimerPresets))
        {
            if (!TimerPreset.TryParse(line, out TimerPreset preset))
            {
                MessageBox.Show(
                    this,
                    $"This timer preset could not be read:\n\n{line}\n\nUse a length such as 5m, 1:30 or 1h20m, with a name in front if you like, for example \"Tea 3m\".",
                    "Notch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                TimerPresets.Focus();
                return false;
            }

            presets.Add(preset);
        }

        return true;
    }

    /// <summary>The ids ticked in the list right now, which may differ from the saved settings.</summary>
    private HashSet<string> TickedPlugins() => [.. PluginList.Children.OfType<CheckBox>()
        .Where(box => box.IsChecked == true)
        .Select(box => (string)box.Tag)];

    /// <param name="ticked">Ids of the plugins to show as switched on.</param>
    private void ListPlugins(HashSet<string> ticked)
    {
        // Pick up plugins that were added to the folder since the app started.
        _plugins.Discover();
        IReadOnlyList<PluginInfo> plugins = _plugins.Plugins;
        PluginList.Children.Clear();
        if (plugins.Count == 0)
        {
            PluginList.Children.Add(new TextBlock { Text = "No plugins installed.", Opacity = 0.7 });
            return;
        }

        foreach (PluginInfo plugin in plugins)
        {
            string title = plugin.Version is null ? plugin.Name : $"{plugin.Name} {plugin.Version}";
            string? about = string.Join(Environment.NewLine, new[] { plugin.Description, plugin.Author, plugin.Directory }
                .Where(line => line is not null));

            if (plugin.Id is null)
            {
                // No usable manifest, so there is nothing to switch on; the reason follows below.
                PluginList.Children.Add(new CheckBox { Content = title, ToolTip = about, IsEnabled = false });
            }
            else if (plugin.AlwaysEnabled)
            {
                // Loaded with --plugin= for this run only; the saved choice (the tag) is left as it was.
                bool saved = ticked.Contains(plugin.Id);
                PluginList.Children.Add(new TextBlock { Text = title + " (development)", ToolTip = about, Margin = new Thickness(0, 4, 0, 4) });
                PluginList.Children.Add(new CheckBox { Tag = plugin.Id, IsChecked = saved, Visibility = Visibility.Collapsed });
            }
            else
            {
                PluginList.Children.Add(new CheckBox
                {
                    Content = title,
                    ToolTip = about,
                    Tag = plugin.Id,
                    IsChecked = ticked.Contains(plugin.Id),
                });
            }

            if (plugin is { Status: PluginStatus.Failed or PluginStatus.Invalid, Error: { } error })
            {
                PluginList.Children.Add(new TextBlock
                {
                    Text = (plugin.Status == PluginStatus.Failed ? "Failed to start: " : "Cannot be used: ") + error,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Margin = new Thickness(28, 0, 0, 6),
                });
            }
        }
    }

    private async void OnInstallPlugin()
    {
        string source = PluginSource.Text.Trim();
        if (source.Length == 0 || !InstallPlugin.IsEnabled)
        {
            return;
        }

        InstallPlugin.IsEnabled = PluginSource.IsEnabled = false;
        ShowInstallStatus("Getting the plugin from GitHub…");
        try
        {
            // Off the UI thread: unpacking is ordinary blocking file work.
            PluginInstallResult result = await Task.Run(() => _installer.InstallAsync(source));
            string title = $"{result.Manifest.Name} {result.Manifest.Version}".TrimEnd();

            HashSet<string> ticked = TickedPlugins();
            ticked.Add(result.Manifest.Id);
            ListPlugins(ticked);
            PluginSource.Clear();
            ShowInstallStatus(result.Pending
                ? $"Downloaded {title}. It replaces the version in use the next time Notch starts."
                : $"Installed {title} and ticked it above. Save to switch it on.");
        }
        catch (PluginLoadException e)
        {
            ShowInstallStatus(e.Message);
        }
        finally
        {
            InstallPlugin.IsEnabled = PluginSource.IsEnabled = true;
        }
    }

    private void ShowInstallStatus(string text)
    {
        InstallStatus.Text = text;
        InstallStatus.Visibility = Visibility.Visible;
    }

    private void OnOpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(_plugins.PluginsDirectory);
            Process.Start(new ProcessStartInfo(_plugins.PluginsDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "Could not open the plugins folder: " + e.Message, "Notch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Keeps the previous value when the box does not hold a sensible number of minutes.</summary>
    private static int ReadMinutes(string text, int previous) =>
        int.TryParse(text.Trim(), out int minutes) && minutes is >= 1 and <= 600 ? minutes : previous;
}
