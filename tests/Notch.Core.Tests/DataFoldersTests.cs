using Notch.Core.Settings;

namespace Notch.Core.Tests;

public sealed class DataFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-folder-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the temp folder cleanup.
        }
    }

    [Fact]
    public void A_debug_build_starts_from_the_installed_settings_without_what_reaches_outside_Notch()
    {
        string installed = Path.Combine(_root, "Notch", "settings.json");
        string debug = Path.Combine(_root, "Notch-Debug", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        new SettingsStore(installed).Save(new AppSettings { AccentColor = "Green", WebhookEnabled = true, GlobalAgentHooks = true, EnabledPlugins = ["chomp.desk"] });

        Assert.True(DataFolders.SeedSettings(installed, debug));

        AppSettings copy = new SettingsStore(debug).Load();
        Assert.Equal("Green", copy.AccentColor);
        Assert.Equal(["chomp.desk"], copy.EnabledPlugins);
        Assert.False(copy.WebhookEnabled);
        Assert.False(copy.GlobalAgentHooks);
        Assert.True(new SettingsStore(installed).Load().WebhookEnabled);
    }

    [Fact]
    public void Settings_are_copied_only_once_and_never_onto_themselves()
    {
        string installed = Path.Combine(_root, "Notch", "settings.json");
        string debug = Path.Combine(_root, "Notch-Debug", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        new SettingsStore(installed).Save(new AppSettings { AccentColor = "Green" });
        Assert.True(DataFolders.SeedSettings(installed, debug));

        new SettingsStore(debug).Save(new AppSettings { AccentColor = "Red" });
        Assert.False(DataFolders.SeedSettings(installed, debug));
        Assert.Equal("Red", new SettingsStore(debug).Load().AccentColor);
        Assert.False(DataFolders.SeedSettings(installed, installed));
        Assert.False(DataFolders.SeedSettings(Path.Combine(_root, "missing.json"), Path.Combine(_root, "other", "settings.json")));
    }
}
