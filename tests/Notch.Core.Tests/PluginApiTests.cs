using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace Notch.Core.Tests;

public sealed class PluginApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-api-tests", Guid.NewGuid().ToString("N"));
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

    private PluginManager Manager(bool listenForChanges = false)
    {
        string plugins = Path.Combine(_root, "plugins");
        foreach (string id in new[] { "a", "b" })
        {
            string folder = Path.Combine(plugins, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
                {
                  "id": "{{id}}", "name": "{{id}}", "assembly": "{{id}}.dll", "apiVersion": 5,
                  "settings": [
                    { "key": "url", "label": "Address", "type": "text" },
                    { "key": "poll", "label": "Seconds", "type": "number", "min": 5, "max": 60 },
                    { "key": "key", "type": "secret" },
                    { "key": "mode", "type": "choice", "options": ["fast", "slow"] },
                    { "key": "pick", "type": "choice" },
                    { "key": "names", "type": "list" },
                    { "key": "on", "type": "bool" }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(folder, $"{id}.dll"), "x");
        }

        return new PluginManager(
            plugins,
            Path.Combine(_root, "data"),
            _activities,
            new PluginCardBoard(),
            new PluginLog(Path.Combine(_root, "plugins.log")),
            (manifest, _) => () => _probes[manifest.Id] = new Probe(listenForChanges));
    }

    [Fact]
    public void A_manifest_lists_options_and_skips_the_unusable_ones()
    {
        using PluginManager manager = Manager();
        manager.Discover();

        IReadOnlyList<PluginSettingField> fields = manager.Plugins[0].Settings!;

        // "pick" is a choice without options, so it is dropped.
        Assert.Equal(["url", "poll", "key", "mode", "names", "on"], fields.Select(f => f.Key));
        Assert.Equal(PluginSettingType.Number, fields[1].Type);
        Assert.Equal((5, 60), (fields[1].Min, fields[1].Max));
        Assert.Equal("Address", fields[0].Label);
        Assert.Equal("key", fields[2].Label);
    }

    [Fact]
    public void Changed_options_restart_a_plugin_that_does_not_listen()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a"]);
        Probe first = _probes["a"];

        manager.ApplySettings("a", new Dictionary<string, JsonElement> { ["url"] = JsonSerializer.SerializeToElement("https://panel") });

        Assert.True(first.Stopped);
        Assert.NotSame(first, _probes["a"]);
        Assert.Equal("https://panel", _probes["a"].Host!.Settings.Get("url", ""));
        Assert.Equal(PluginStatus.Running, manager.Plugins[0].Status);
    }

    [Fact]
    public async Task A_plugin_that_listens_is_told_instead_of_restarted()
    {
        using PluginManager manager = Manager(listenForChanges: true);
        manager.Discover();
        manager.SetEnabled(["a"]);
        Probe probe = _probes["a"];

        manager.ApplySettings("a", new Dictionary<string, JsonElement> { ["poll"] = JsonSerializer.SerializeToElement(30) });

        Assert.Equal("poll", await probe.Changes.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(probe.Stopped);
        Assert.Same(probe, _probes["a"]);
        Assert.Equal(30, probe.Host!.Settings.Get("poll", 0));
    }

    [Fact]
    public void Options_of_a_plugin_that_is_not_running_are_saved_for_its_next_start_and_unlisted_keys_ignored()
    {
        using (PluginManager manager = Manager())
        {
            manager.Discover();
            manager.ApplySettings("a", new Dictionary<string, JsonElement>
            {
                ["url"] = JsonSerializer.SerializeToElement("https://later"),
                ["not-in-the-manifest"] = JsonSerializer.SerializeToElement(1),
            });

            Assert.Equal("https://later", manager.SettingValues("a")["url"].GetString());
            Assert.DoesNotContain("not-in-the-manifest", manager.SettingValues("a").Keys);
            manager.SetEnabled(["a"]);
        }

        Assert.Equal("https://later", _probes["a"].Host!.Settings.Get("url", ""));
    }

    [Fact]
    public void Secrets_are_never_handed_back_to_the_settings_window()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.ApplySettings("a", new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement("hunter2") });

        Assert.DoesNotContain("key", manager.SettingValues("a").Keys);
    }

    [Fact]
    public async Task Messages_reach_subscribers_of_other_plugins_and_stop_when_a_plugin_does()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a", "b"]);
        var heard = new TaskCompletionSource<PluginMessage>();
        int heardCount = 0;
        _probes["b"].Host!.Bus.Subscribe("a.ping", m =>
        {
            Interlocked.Increment(ref heardCount);
            heard.TrySetResult(m);
        });

        _probes["a"].Host!.Bus.Publish("a.ping", "hello");

        PluginMessage message = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new PluginMessage("a", "a.ping", "hello"), message);

        manager.SetEnabled(["a"]);

        IPluginHost closedB = _probes["b"].Host!;
        _probes["a"].Host!.Bus.Publish("a.ping", "b has stopped and must not hear this");
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref heardCount));
        closedB.Bus.Publish("a.ping", "a closed host sends nothing");
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref heardCount));
    }

    [Fact]
    public async Task A_failing_subscriber_does_not_stop_the_others()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a", "b"]);
        var heard = new TaskCompletionSource<string?>();
        _probes["a"].Host!.Bus.Subscribe("t", _ => throw new InvalidOperationException("boom"));
        _probes["b"].Host!.Bus.Subscribe("t", m => heard.SetResult(m.Payload));

        _probes["a"].Host!.Bus.Publish("t", "still delivered");

        Assert.Equal("still delivered", await heard.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Page_callbacks_are_run_off_the_callers_thread_and_cannot_throw_into_it()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a"]);
        var ran = new TaskCompletionSource<bool>();
        _probes["a"].Host!.Pages.Set(new PluginPage
        {
            Id = "p",
            Title = "P",
            Actions = [new PluginAction { Label = "Boom", Clicked = () => throw new InvalidOperationException("boom") }],
            Blocks =
            [
                new PluginToggle { Label = "T", Changed = on => ran.SetResult(on) },
                new PluginButtons { Actions = [new PluginAction { Label = "B", Clicked = () => throw new InvalidOperationException("boom") }] },
            ],
        });

        PluginPage page = manager.Pages.Snapshot().Single().Page;
        page.Actions[0].Clicked!();
        ((PluginButtons)page.Blocks[1]).Actions[0].Clicked!();
        ((PluginToggle)page.Blocks[0]).Changed!(true);

        Assert.True(await ran.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_shell_reports_what_the_notch_is_showing()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a", "b"]);
        IPluginShell shell = _probes["a"].Host!.Shell;

        manager.ShellState.Update(expanded: true, ("a", "server"), cardsTab: false, dark: false, accent: GlowColor.Green);

        Assert.True(shell.IsExpanded);
        Assert.True(shell.IsPageVisible("server"));
        Assert.False(_probes["b"].Host!.Shell.IsPageVisible("server"));
        Assert.False(shell.AreCardsVisible);
        Assert.False(shell.IsDark);
        Assert.Equal(GlowColor.Green, shell.Accent);

        manager.ShellState.Update(expanded: false, ("a", "server"), cardsTab: true, dark: true, accent: null);
        Assert.False(shell.IsPageVisible("server"));
        Assert.False(shell.AreCardsVisible);
    }

    [Fact]
    public void Notify_shows_a_transient_notice_in_the_pill()
    {
        using PluginManager manager = Manager();
        manager.Discover();
        manager.SetEnabled(["a"]);

        _probes["a"].Host!.Shell.Notify("Done", "All good", color: GlowColor.Green);

        Activity notice = Assert.Single(_activities.Snapshot());
        Assert.Equal(PluginManager.ActivityIdFor("a", "notice"), notice.Id);
        Assert.Equal(ActivityTier.Transient, notice.Tier);
        Assert.Equal("Done", notice.Title);
    }

    private sealed class Probe(bool listen) : INotchPlugin
    {
        public IPluginHost? Host { get; private set; }

        public bool Stopped { get; private set; }

        public TaskCompletionSource<string> Changes { get; } = new();

        public void Start(IPluginHost host)
        {
            Host = host;
            if (listen)
            {
                host.Settings.Changed += (_, key) => Changes.TrySetResult(key);
            }
        }

        public void Stop() => Stopped = true;
    }
}
