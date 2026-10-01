using System.Reflection;
using System.Windows;
using Notch.Core.Settings;
using Notch.Platform.Display;
using Notch.Platform.Startup;

namespace Notch.App.Settings;

/// <summary>Edits <see cref="AppSettings"/> in place. <see cref="Window.DialogResult"/> is not used; listen to <see cref="Saved"/>.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;

    public SettingsWindow(AppSettings settings, SettingsStore store)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;

        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        ExpandOnHover.IsChecked = settings.ExpandOnHover;
        HideInFullscreen.IsChecked = settings.HideInFullscreen;
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

        Save.Click += (_, _) => OnSave();
        Cancel.Click += (_, _) => Close();
    }

    /// <summary>Raised after the settings object was updated and written to disk.</summary>
    public event EventHandler? Saved;

    private void OnSave()
    {
        _settings.ExpandOnHover = ExpandOnHover.IsChecked == true;
        _settings.HideInFullscreen = HideInFullscreen.IsChecked == true;
        _settings.ShowMedia = ShowMedia.IsChecked == true;
        _settings.ShowVolume = ShowVolume.IsChecked == true;
        _settings.ShowBrightness = ShowBrightness.IsChecked == true;
        _settings.ShowPower = ShowPower.IsChecked == true;
        _settings.ShowBluetooth = ShowBluetooth.IsChecked == true;
        _settings.DisplayIndex = Display.SelectedIndex > 0 ? Display.SelectedIndex - 1 : null;
        _settings.CalendarFeeds = [.. CalendarFeeds.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
}
