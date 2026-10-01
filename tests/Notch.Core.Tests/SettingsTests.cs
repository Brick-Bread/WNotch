using Notch.Core.Settings;

namespace Notch.Core.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData(NotchTheme.Dark, true, false)]
    [InlineData(NotchTheme.Light, false, true)]
    [InlineData(NotchTheme.System, true, true)]
    [InlineData(NotchTheme.System, false, false)]
    public void Theme_follows_windows_only_when_asked_to(NotchTheme theme, bool systemIsLight, bool expected) =>
        Assert.Equal(expected, NotchThemes.IsLight(theme, systemIsLight));

    [Fact]
    public void Theme_is_saved_by_name()
    {
        WithStore(store =>
        {
            Assert.Equal(NotchTheme.Dark, store.Load().Theme);

            store.Save(new AppSettings { Theme = NotchTheme.System });

            Assert.Contains("\"Theme\": \"System\"", File.ReadAllText(store.FilePath));
            Assert.Equal(NotchTheme.System, store.Load().Theme);
        });
    }

    private static void WithStore(Action<SettingsStore> test)
    {
        string file = Path.Combine(Path.GetTempPath(), $"notch-test-{Guid.NewGuid():N}", "settings.json");
        try
        {
            test(new SettingsStore(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
