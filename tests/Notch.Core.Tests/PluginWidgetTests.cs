using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Plugins;
using Notch.Core.Widgets;

namespace Notch.Core.Tests;

public sealed class PluginWidgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-widget-tests", Guid.NewGuid().ToString("N"));
    private readonly ActivityManager _activities = new(new FakeTimeProvider());
    private readonly Dictionary<string, WidgetPlugin> _plugins = [];

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
            File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""{ "id": "{{id}}", "name": "{{id}}", "assembly": "{{id}}.dll", "apiVersion": 8 }""");
            File.WriteAllText(Path.Combine(folder, $"{id}.dll"), "x");
        }

        var manager = new PluginManager(
            plugins,
            Path.Combine(_root, "data"),
            _activities,
            new PluginCardBoard(),
            new PluginLog(Path.Combine(_root, "plugins.log")),
            (manifest, _) => () => _plugins[manifest.Id] = new WidgetPlugin());
        manager.Discover();
        return manager;
    }

    private static HomeWidget OnHome(string source, string id = "w1") =>
        new() { Id = id, Kind = WidgetKinds.PluginWidget, Source = source };

    /// <summary>Callbacks run on the thread pool; waits for one to have happened.</summary>
    private static void WaitFor(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "The callback never came.");
    }

    [Fact]
    public void A_registered_type_is_offered_and_hears_about_its_widgets()
    {
        using PluginManager manager = Manager();
        manager.SetEnabled(["a"]);
        PluginWidgetBoard board = manager.Widgets;

        PluginWidgetTypeEntry entry = Assert.Single(board.Types);
        Assert.Equal("a/counter", entry.Source);
        Assert.Equal("Counter", WidgetCatalog.ForPlugin(entry.Type).Name);

        HomeWidget widget = OnHome(entry.Source);
        widget.Options["step"] = "5";
        widget.Options[WidgetCatalog.NoteTextKey] = "not the plugin's";
        Assert.True(board.Place(widget));

        WidgetPlugin plugin = _plugins["a"];
        WaitFor(() => plugin.Events.Contains("placed w1 step=5 label=Count"));
        Assert.DoesNotContain(plugin.Events, e => e.Contains("not the plugin's"));

        // The plugin answered by showing something for it.
        WaitFor(() => board.ContentFor("w1") is not null);
        Assert.Equal("Count", board.ContentFor("w1")!.Title);

        board.SetVisible(true);
        WaitFor(() => plugin.Events.Contains("visible w1 True"));

        widget.Options["step"] = "2";
        board.UpdateOptions(widget);
        WaitFor(() => plugin.Events.Contains("options w1 step=2"));

        board.Unplace("w1");
        WaitFor(() => plugin.Events.Contains("removed w1"));
        Assert.Null(board.ContentFor("w1"));
        Assert.Empty(plugin.Host!.Widgets.Placed);
    }

    [Fact]
    public void A_widget_of_a_plugin_that_is_off_waits_and_one_plugin_cannot_fill_anothers()
    {
        using PluginManager manager = Manager();
        PluginWidgetBoard board = manager.Widgets;
        HomeWidget widget = OnHome("a/counter");

        Assert.False(board.Place(widget));

        manager.SetEnabled(["a", "b"]);
        Assert.True(board.Place(widget));
        WaitFor(() => board.ContentFor("w1") is not null);

        _plugins["b"].Host!.Widgets.Show("w1", new PluginWidgetContent { Title = "Hijacked" });
        Assert.Equal("Count", board.ContentFor("w1")!.Title);

        // Switching the plugin off forgets its types and widgets, without calling it back.
        int events = _plugins["a"].Events.Count;
        manager.SetEnabled(["b"]);
        Assert.DoesNotContain(board.Types, t => t.PluginId == "a");
        Assert.False(board.IsPlaced("w1"));
        Assert.Equal(events, _plugins["a"].Events.Count(e => !e.StartsWith("stop", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_callbacks_of_a_widgets_blocks_run_off_the_callers_thread_and_cannot_throw_into_it()
    {
        using PluginManager manager = Manager();
        manager.SetEnabled(["a"]);
        manager.Widgets.Place(OnHome("a/counter"));
        WaitFor(() => manager.Widgets.ContentFor("w1") is not null);
        var typed = new TaskCompletionSource<string>();

        _plugins["a"].Host!.Widgets.Show("w1", new PluginWidgetContent
        {
            Blocks =
            [
                new PluginToggle { Label = "T", Changed = _ => throw new InvalidOperationException("boom") },
                new PluginTextField { Label = "Find", Changed = typed.SetResult },
            ],
        });

        IReadOnlyList<PluginBlock> blocks = manager.Widgets.ContentFor("w1")!.Blocks;
        ((PluginToggle)blocks[0]).Changed!(true);
        ((PluginTextField)blocks[1]).Changed!("ban");

        Assert.Equal("ban", await typed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        WaitFor(() => LogContains("widget 'w1'"));
    }

    /// <summary>The log may be in the middle of being written; that read just counts as not yet.</summary>
    private bool LogContains(string text)
    {
        try
        {
            string log = Path.Combine(_root, "plugins.log");
            return File.Exists(log) && File.ReadAllText(log).Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public void A_throwing_callback_is_logged_not_raised()
    {
        using PluginManager manager = Manager();
        manager.SetEnabled(["a"]);
        _plugins["a"].Throw = true;

        HomeWidget widget = OnHome("a/counter");
        manager.Widgets.Place(widget);
        manager.Widgets.SetVisible(true);
        manager.Widgets.Unplace("w1");

        WaitFor(() => File.Exists(Path.Combine(_root, "plugins.log"))
            && File.ReadAllText(Path.Combine(_root, "plugins.log")).Contains("widget type 'counter'", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_widgets_survive_normalizing_only_with_a_source()
    {
        List<HomeWidget> widgets = HomeLayout.Normalize([OnHome("a/counter", "x"), new HomeWidget { Kind = WidgetKinds.PluginWidget, Id = "y" }]);

        Assert.Equal(["x"], widgets.Select(w => w.Id));
        Assert.DoesNotContain(HomeLayout.Addable(widgets), t => t.Kind == WidgetKinds.PluginWidget);

        var single = new PluginWidgetTypeEntry("a", new PluginWidgetType { Id = "counter", Name = "Counter", Single = true });
        var many = new PluginWidgetTypeEntry("a", new PluginWidgetType { Id = "other", Name = "Other" });
        Assert.Equal([many], HomeLayout.AddableFromPlugins(widgets, [single, many]));
    }

    private sealed class WidgetPlugin : INotchPlugin
    {
        public IPluginHost? Host { get; private set; }

        public ConcurrentQueue<string> Events { get; } = new();

        public bool Throw { get; set; }

        public void Start(IPluginHost host)
        {
            Host = host;
            host.Widgets.Register(new PluginWidgetType
            {
                Id = "counter",
                Name = "Counter",
                Options =
                [
                    new WidgetOption("step", "Step", WidgetOptionType.Text, "1"),
                    new WidgetOption("label", "Label", WidgetOptionType.Text, "Count"),
                ],
                Placed = widget =>
                {
                    Check();
                    Events.Enqueue($"placed {widget.Id} step={widget.Option("step")} label={widget.Option("label")}" + string.Concat(widget.Options.Values.Where(v => v.Contains("not the plugin's"))));
                    host.Widgets.Show(widget.Id, new PluginWidgetContent
                    {
                        Title = widget.Option("label"),
                        Blocks = [new PluginText { Text = "0" }],
                    });
                },
                Removed = widget =>
                {
                    Check();
                    Events.Enqueue($"removed {widget.Id}");
                },
                OptionsChanged = widget => Events.Enqueue($"options {widget.Id} step={widget.Option("step")}"),
                VisibleChanged = (widget, visible) =>
                {
                    Check();
                    Events.Enqueue($"visible {widget.Id} {visible}");
                },
            });
        }

        public void Stop() => Events.Enqueue("stop");

        private void Check()
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }
}
