using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public class PluginManifestTests
{
    private const string Valid = """
        {
          // Comments and trailing commas are tolerated.
          "id": "acme.build-status",
          "name": "Build status",
          "version": "1.2.0",
          "assembly": "Acme.BuildStatus.dll",
          "apiVersion": 1,
        }
        """;

    [Fact]
    public void Parses_a_valid_manifest()
    {
        PluginManifest manifest = PluginManifest.Parse(Valid);

        Assert.Equal("acme.build-status", manifest.Id);
        Assert.Equal("Build status", manifest.Name);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("Acme.BuildStatus.dll", manifest.Assembly);
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Null(manifest.Author);
    }

    [Theory]
    [InlineData("not json", "could not be read")]
    [InlineData("null", "empty")]
    [InlineData("""{ "name": "X", "assembly": "x.dll", "apiVersion": 1 }""", "\"id\"")]
    [InlineData("""{ "id": "x", "assembly": "x.dll", "apiVersion": 1 }""", "\"name\"")]
    [InlineData("""{ "id": "x", "name": "X", "apiVersion": 1 }""", "\"assembly\"")]
    [InlineData("""{ "id": "x", "name": "X", "assembly": "x.dll" }""", "\"apiVersion\"")]
    [InlineData("""{ "id": "Has Spaces", "name": "X", "assembly": "x.dll", "apiVersion": 1 }""", "not valid")]
    [InlineData("""{ "id": "..", "name": "X", "assembly": "x.dll", "apiVersion": 1 }""", "not valid")]
    [InlineData("""{ "id": "x", "name": "X", "assembly": "..\\x.dll", "apiVersion": 1 }""", "file name")]
    [InlineData("""{ "id": "x", "name": "X", "assembly": "x.exe", "apiVersion": 1 }""", "file name")]
    [InlineData("""{ "id": "x", "name": "X", "assembly": "x.dll", "apiVersion": 0 }""", "1 or higher")]
    public void Rejects_an_unusable_manifest(string json, string expectedInMessage)
    {
        var e = Assert.Throws<PluginLoadException>(() => PluginManifest.Parse(json));
        Assert.Contains(expectedInMessage, e.Message);
    }

    [Fact]
    public void Rejects_a_plugin_written_for_a_newer_api()
    {
        string json = $$"""{ "id": "x", "name": "X", "assembly": "x.dll", "apiVersion": {{PluginApi.Version + 1}} }""";

        var e = Assert.Throws<PluginLoadException>(() => PluginManifest.Parse(json));
        Assert.Contains("Update Notch", e.Message);
    }
}

public class PluginCardBoardTests
{
    [Fact]
    public void Updating_a_card_keeps_its_place()
    {
        var board = new PluginCardBoard();
        int changes = 0;
        board.Changed += (_, _) => changes++;

        board.Set("a", new PluginCard { Id = "one", Label = "One" });
        board.Set("b", new PluginCard { Id = "one", Label = "Other plugin, same id" });
        board.Set("a", new PluginCard { Id = "one", Label = "Updated" });

        Assert.Equal(["Updated", "Other plugin, same id"], board.Snapshot().Select(e => e.Card.Label));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void RemoveAll_only_touches_one_plugin()
    {
        var board = new PluginCardBoard();
        board.Set("a", new PluginCard { Id = "one", Label = "A1" });
        board.Set("a", new PluginCard { Id = "two", Label = "A2" });
        board.Set("b", new PluginCard { Id = "one", Label = "B1" });

        board.RemoveAll("a");

        Assert.Equal(["B1"], board.Snapshot().Select(e => e.Card.Label));
        Assert.False(board.Remove("a", "one"));
        Assert.True(board.Remove("b", "one"));
    }
}

public sealed class PluginManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-plugin-tests", Guid.NewGuid().ToString("N"));
    private readonly ActivityManager _activities = new(new FakeTimeProvider());
    private readonly PluginCardBoard _cards = new();
    private readonly Dictionary<string, FakePlugin> _fakes = [];

    private string PluginsDirectory => Path.Combine(_root, "plugins");

    public void Dispose()
    {
        _activities.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An assembly loaded from here stays locked until the test process exits.
        }
    }

    [Fact]
    public void Installed_plugins_do_not_run_until_enabled()
    {
        Install("a");
        using PluginManager manager = CreateManager();

        manager.Discover();
        manager.SetEnabled([]);

        PluginInfo info = Assert.Single(manager.Plugins);
        Assert.Equal("a", info.Id);
        Assert.Equal(PluginStatus.Disabled, info.Status);
        Assert.Empty(_fakes);
    }

    [Fact]
    public void Enabled_plugin_starts_and_its_activity_ids_are_scoped()
    {
        Install("a");
        using PluginManager manager = CreateManager();
        manager.Discover();

        manager.SetEnabled(["a"]);

        Assert.Equal(PluginStatus.Running, Assert.Single(manager.Plugins).Status);
        Assert.Equal([PluginManager.ActivityIdFor("a", "hello")], _activities.Snapshot().Select(a => a.Id));
        Assert.Equal("a", Assert.Single(_cards.Snapshot()).PluginId);
    }

    [Fact]
    public void Disabling_stops_the_plugin_and_removes_what_it_showed()
    {
        Install("a");
        using PluginManager manager = CreateManager();
        manager.Discover();
        manager.SetEnabled(["a"]);
        FakePlugin plugin = _fakes["a"];

        manager.SetEnabled([]);

        Assert.True(plugin.Stopped);
        Assert.Empty(_activities.Snapshot());
        Assert.Empty(_cards.Snapshot());

        // A timer the plugin forgot to stop must not bring anything back.
        plugin.Host!.Activities.Publish(new Activity { Id = "late", Tier = ActivityTier.Ongoing, Title = "Late" });
        plugin.Host.Cards.Set(new PluginCard { Id = "late", Label = "Late" });
        Assert.Empty(_activities.Snapshot());
        Assert.Empty(_cards.Snapshot());
    }

    [Fact]
    public void A_plugin_that_throws_is_marked_failed_and_leaves_nothing_behind()
    {
        Install("bad");
        Install("good");
        using PluginManager manager = CreateManager(failing: "bad");
        manager.Discover();

        manager.SetEnabled(["bad", "good"]);

        PluginInfo bad = manager.Plugins.Single(p => p.Id == "bad");
        Assert.Equal(PluginStatus.Failed, bad.Status);
        Assert.Equal("boom", bad.Error);
        Assert.Equal(PluginStatus.Running, manager.Plugins.Single(p => p.Id == "good").Status);
        Assert.Equal([PluginManager.ActivityIdFor("good", "hello")], _activities.Snapshot().Select(a => a.Id));
    }

    [Fact]
    public void Unusable_folders_are_listed_with_the_reason()
    {
        Install("a", folder: "first");
        Install("a", folder: "second");
        Directory.CreateDirectory(Path.Combine(PluginsDirectory, "not-a-plugin"));
        string broken = Path.Combine(PluginsDirectory, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, PluginManifest.FileName), "{");
        using PluginManager manager = CreateManager();

        manager.Discover();

        Assert.Equal(["broken", "a", "second"], manager.Plugins.Select(p => p.Id ?? p.Name));
        Assert.Equal(
            [PluginStatus.Invalid, PluginStatus.Disabled, PluginStatus.Invalid],
            manager.Plugins.Select(p => p.Status));
        Assert.Contains("already used", manager.Plugins[2].Error);
    }

    [Fact]
    public void Discover_again_finds_new_plugins_and_leaves_running_ones_alone()
    {
        Install("a");
        using PluginManager manager = CreateManager();
        manager.Discover();
        manager.SetEnabled(["a"]);
        FakePlugin running = _fakes["a"];

        Install("b");
        manager.Discover();

        Assert.Equal(["a", "b"], manager.Plugins.Select(p => p.Id));
        Assert.Same(running, _fakes["a"]);
        Assert.False(running.Stopped);
    }

    [Fact]
    public void Pinned_plugins_run_without_being_enabled()
    {
        string folder = Install("dev", parent: Path.Combine(_root, "elsewhere"));
        using PluginManager manager = CreateManager();

        manager.Discover([folder]);
        manager.SetEnabled([]);

        PluginInfo info = Assert.Single(manager.Plugins);
        Assert.True(info.AlwaysEnabled);
        Assert.Equal(PluginStatus.Running, info.Status);
    }

    [Fact]
    public void Settings_survive_a_restart_and_tolerate_wrong_types()
    {
        Install("a");
        using (PluginManager first = CreateManager())
        {
            first.Discover();
            first.SetEnabled(["a"]);
            _fakes["a"].Host!.Settings.Set("minutes", 20);
            _fakes["a"].Host!.Settings.Set("name", "desk");
        }

        using PluginManager second = CreateManager();
        second.Discover();
        second.SetEnabled(["a"]);
        IPluginSettings settings = _fakes["a"].Host!.Settings;

        Assert.Equal(20, settings.Get("minutes", 50));
        Assert.Equal(50, settings.Get("name", 50));
        Assert.Equal(50, settings.Get("missing", 50));
        Assert.True(settings.Remove("minutes"));
        Assert.Equal(50, settings.Get("minutes", 50));
    }

    [Fact]
    public void Loads_a_plugin_from_its_assembly()
    {
        // This test assembly doubles as the plugin: LoadedFromDiskPlugin below is its entry point.
        string folder = Install("disk", assembly: "TestPlugin.dll");
        File.Copy(typeof(LoadedFromDiskPlugin).Assembly.Location, Path.Combine(folder, "TestPlugin.dll"));
        using var manager = new PluginManager(
            PluginsDirectory, Path.Combine(_root, "data"), _activities, _cards, new PluginLog(Path.Combine(_root, "plugins.log")));

        manager.Discover();
        manager.SetEnabled(["disk"]);

        PluginInfo info = Assert.Single(manager.Plugins);
        Assert.Null(info.Error);
        Assert.Equal(PluginStatus.Running, info.Status);
        Assert.Equal([PluginManager.ActivityIdFor("disk", "loaded")], _activities.Snapshot().Select(a => a.Id));
    }

    [Fact]
    public void A_missing_assembly_is_reported()
    {
        Install("disk", assembly: "Missing.dll");
        using var manager = new PluginManager(
            PluginsDirectory, Path.Combine(_root, "data"), _activities, _cards, new PluginLog(Path.Combine(_root, "plugins.log")));

        manager.Discover();
        manager.SetEnabled(["disk"]);

        PluginInfo info = Assert.Single(manager.Plugins);
        Assert.Equal(PluginStatus.Failed, info.Status);
        Assert.Contains("Missing.dll", info.Error);
        Assert.Contains("failed to start", File.ReadAllText(Path.Combine(_root, "plugins.log")));
    }

    private PluginManager CreateManager(string? failing = null) => new(
        PluginsDirectory,
        Path.Combine(_root, "data"),
        _activities,
        _cards,
        new PluginLog(Path.Combine(_root, "plugins.log")),
        (manifest, _) => () => _fakes[manifest.Id] = new FakePlugin(fail: manifest.Id == failing));

    /// <summary>Writes a plugin folder holding only a manifest and returns its path.</summary>
    private string Install(string id, string? folder = null, string? parent = null, string assembly = "Plugin.dll")
    {
        string directory = Path.Combine(parent ?? PluginsDirectory, folder ?? id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, PluginManifest.FileName),
            $$"""{ "id": "{{id}}", "name": "{{id}}", "assembly": "{{assembly}}", "apiVersion": 1 }""");
        return directory;
    }

    private sealed class FakePlugin(bool fail) : INotchPlugin
    {
        public IPluginHost? Host { get; private set; }

        public bool Stopped { get; private set; }

        public void Start(IPluginHost host)
        {
            Host = host;
            host.Activities.Publish(new Activity { Id = "hello", Tier = ActivityTier.Ongoing, Title = "Hello" });
            host.Cards.Set(new PluginCard { Id = "card", Label = "Card" });
            if (fail)
            {
                throw new InvalidOperationException("boom");
            }
        }

        public void Stop() => Stopped = true;
    }
}

/// <summary>The one public <see cref="INotchPlugin"/> in this assembly, found by the real loader in <see cref="PluginManagerTests"/>.</summary>
public sealed class LoadedFromDiskPlugin : INotchPlugin
{
    public void Start(IPluginHost host) =>
        host.Activities.Publish(new Activity { Id = "loaded", Tier = ActivityTier.Ongoing, Title = "Loaded" });

    public void Stop()
    {
    }
}
