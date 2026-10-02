using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public sealed class PluginThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-theme-tests", Guid.NewGuid().ToString("N"));
    private readonly ActivityManager _activities = new(new FakeTimeProvider());
    private readonly Dictionary<string, Probe> _probes = [];

    public void Dispose()
    {
        _activities.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private PluginManager Manager()
    {
        string plugins = Path.Combine(_root, "plugins");
        foreach (string id in new[] { "a", "b" })
        {
            string folder = Path.Combine(plugins, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""{ "id": "{{id}}", "name": "{{id}}", "assembly": "{{id}}.dll", "apiVersion": 6 }""");
            File.WriteAllText(Path.Combine(folder, $"{id}.dll"), "x");
        }

        return new PluginManager(
            plugins,
            Path.Combine(_root, "data"),
            _activities,
            new PluginCardBoard(),
            new PluginLog(Path.Combine(_root, "plugins.log")),
            (manifest, _) => () => _probes[manifest.Id] = new Probe());
    }

    private IPluginHost Start(PluginManager manager, string id)
    {
        manager.Discover();
        manager.SetEnabled([id]);
        return _probes[id].Host!;
    }

    [Fact]
    public void A_theme_is_listed_under_its_plugin_with_the_full_path_of_its_file()
    {
        using PluginManager manager = Manager();
        IPluginHost host = Start(manager, "a");

        host.Themes.Set(new PluginTheme { Id = "midnight", Name = "Midnight", File = "themes/midnight.xaml", Base = PluginThemeBase.Light });

        PluginThemeEntry entry = Assert.Single(manager.Themes.Snapshot());
        Assert.Equal("a/midnight", entry.Key);
        Assert.Equal("Midnight", entry.Name);
        Assert.Equal(PluginThemeBase.Light, entry.Base);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "plugins", "a", "themes", "midnight.xaml")), entry.FilePath);
        Assert.Same(entry, manager.Themes.Find("a/midnight"));
        Assert.Null(manager.Themes.Find("a/other"));
        Assert.Null(manager.Themes.Find(null));
    }

    [Fact]
    public void Setting_a_theme_again_replaces_it_in_place()
    {
        using PluginManager manager = Manager();
        IPluginHost host = Start(manager, "a");
        host.Themes.Set(new PluginTheme { Id = "one", Name = "One", File = "one.xaml" });
        host.Themes.Set(new PluginTheme { Id = "two", Name = "Two", File = "two.xaml" });

        host.Themes.Set(new PluginTheme { Id = "one", Name = "First", File = "one.xaml" });

        Assert.Equal(["First", "Two"], manager.Themes.Snapshot().Select(t => t.Name));
    }

    [Theory]
    [InlineData("../other/theme.xaml")]
    [InlineData("themes/../../b/theme.xaml")]
    [InlineData("theme.json")]
    [InlineData("")]
    public void A_file_outside_the_plugin_folder_or_that_is_not_xaml_is_refused(string file)
    {
        using PluginManager manager = Manager();
        IPluginHost host = Start(manager, "a");

        Assert.Throws<ArgumentException>(() => host.Themes.Set(new PluginTheme { Id = "x", Name = "X", File = file }));
        Assert.Empty(manager.Themes.Snapshot());
    }

    [Fact]
    public void An_absolute_path_and_an_id_with_a_slash_are_refused()
    {
        using PluginManager manager = Manager();
        IPluginHost host = Start(manager, "a");

        Assert.Throws<ArgumentException>(() => host.Themes.Set(new PluginTheme { Id = "x", Name = "X", File = Path.Combine(_root, "elsewhere.xaml") }));
        Assert.Throws<ArgumentException>(() => host.Themes.Set(new PluginTheme { Id = "a/b", Name = "X", File = "x.xaml" }));
        Assert.Throws<ArgumentException>(() => host.Themes.Set(new PluginTheme { Id = "x", Name = " ", File = "x.xaml" }));
        Assert.Empty(manager.Themes.Snapshot());
    }

    [Fact]
    public void Themes_of_two_plugins_do_not_collide_and_go_when_their_plugin_stops()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a", "b"]);
        _probes["a"].Host!.Themes.Set(new PluginTheme { Id = "dark", Name = "A dark", File = "t.xaml" });
        _probes["b"].Host!.Themes.Set(new PluginTheme { Id = "dark", Name = "B dark", File = "t.xaml" });
        Assert.Equal(["a/dark", "b/dark"], manager.Themes.Snapshot().Select(t => t.Key));

        int changes = 0;
        manager.Themes.Changed += (_, _) => changes++;
        IPluginHost stopped = _probes["a"].Host!;
        manager.SetEnabled(["b"]);

        Assert.Equal(["b/dark"], manager.Themes.Snapshot().Select(t => t.Key));
        Assert.Equal(1, changes);

        // A call from a plugin that was switched off is ignored.
        stopped.Themes.Set(new PluginTheme { Id = "late", Name = "Late", File = "t.xaml" });
        Assert.Equal(["b/dark"], manager.Themes.Snapshot().Select(t => t.Key));
    }

    [Fact]
    public void Removing_a_theme_reports_whether_it_existed()
    {
        using PluginManager manager = Manager();
        IPluginHost host = Start(manager, "a");
        host.Themes.Set(new PluginTheme { Id = "one", Name = "One", File = "one.xaml" });

        Assert.True(host.Themes.Remove("one"));
        Assert.False(host.Themes.Remove("one"));
        Assert.Empty(manager.Themes.Snapshot());
    }

    private sealed class Probe : INotchPlugin
    {
        public IPluginHost? Host { get; private set; }

        public void Start(IPluginHost host) => Host = host;

        public void Stop()
        {
        }
    }
}
