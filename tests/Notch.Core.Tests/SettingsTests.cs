using Notch.Core.Activities;
using Notch.Core.Settings;
using Notch.Core.Widgets;

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

    [Fact]
    public void Style_and_position_default_to_a_notch_at_the_top_and_are_saved_by_name()
    {
        WithStore(store =>
        {
            AppSettings fresh = store.Load();
            Assert.Equal((NotchStyle.Notch, NotchPosition.TopCenter), (fresh.Style, fresh.Position));

            store.Save(new AppSettings { Style = NotchStyle.Island, Position = NotchPosition.TaskbarLeft });

            string json = File.ReadAllText(store.FilePath);
            Assert.Contains("\"Style\": \"Island\"", json);
            Assert.Contains("\"Position\": \"TaskbarLeft\"", json);
            Assert.Equal(NotchPosition.TaskbarLeft, store.Load().Position);
        });
    }

    [Fact]
    public void Accent_defaults_to_a_preset_and_none_switches_it_off()
    {
        Assert.NotNull(GlowColor.FromName(new AppSettings().AccentColor));
        Assert.Null(GlowColor.FromName(AppSettings.NoAccent));
    }

    [Fact]
    public void Timer_presets_default_to_the_classic_four_and_round_trip()
    {
        WithStore(store =>
        {
            Assert.Equal(["5m", "15m", "30m", "1h"], store.Load().Timers().Select(preset => preset.Label));

            store.Save(new AppSettings { TimerPresets = [new("Tea", 180)] });

            Assert.Equal([new TimerPreset("Tea", 180)], store.Load().Timers());
            Assert.DoesNotContain("Label", File.ReadAllText(store.FilePath));
        });
    }

    [Fact]
    public void Timer_presets_from_a_hand_edited_file_are_tidied()
    {
        var settings = new AppSettings
        {
            TimerPresets =
            [
                new("  Stretch  ", 120),
                new("Never", 0),
                new("Far too long a name", 60),
                new("", 100_000),
                .. Enumerable.Range(1, 10).Select(i => new TimerPreset("", i * 60)),
            ],
        };

        IReadOnlyList<TimerPreset> timers = settings.Timers();

        Assert.Equal(AppSettings.MaxTimerPresets, timers.Count);
        Assert.Equal(new TimerPreset("Stretch", 120), timers[0]);
        Assert.Equal("Far too lo", timers[1].Name);
        Assert.All(timers, preset => Assert.True(preset.IsValid));
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
