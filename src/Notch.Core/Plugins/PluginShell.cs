using Notch.Core.Activities;

namespace Notch.Core.Plugins;

/// <summary>
/// What a plugin may ask about the notch itself, and the events when that changes. Everything
/// is safe to call from any thread. API version 5.
/// </summary>
public interface IPluginShell
{
    /// <summary>True while the notch is expanded.</summary>
    bool IsExpanded { get; }

    /// <summary>True while the notch is expanded on one of this plugin's pages. Use it to poll faster only while someone is looking.</summary>
    bool IsPageVisible(string pageId);

    /// <summary>True while the notch is expanded on the Plugins tab, where this plugin's cards are.</summary>
    bool AreCardsVisible { get; }

    /// <summary>True when the notch is drawn dark. Colours given to Notch adapt by themselves; this is for plugins that draw their own, such as images.</summary>
    bool IsDark { get; }

    /// <summary>The accent colour the user chose, or null when they switched it off.</summary>
    GlowColor? Accent { get; }

    /// <summary>Opens Notch's Settings window, where the plugin's options are. Does nothing when it is already open.</summary>
    void OpenSettings();

    /// <summary>Raised, on a background thread, when any of the above changed.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// A short notice in the pill: the same as publishing a transient activity, without the
    /// bookkeeping. Replaces the previous notice from this plugin.
    /// </summary>
    void Notify(string title, string? detail = null, string? glyph = null, GlowColor? color = null, TimeSpan? lifetime = null);
}

/// <summary>A message one plugin sent to the others.</summary>
/// <param name="Sender">The id of the plugin that sent it.</param>
/// <param name="Payload">Whatever the sender chose to put there, usually JSON; null when it sent nothing.</param>
public sealed record PluginMessage(string Sender, string Topic, string? Payload);

/// <summary>
/// Lets plugins talk to each other: one publishes to a topic, every plugin subscribed to it
/// hears about it. Topics are plain strings; prefix yours with your plugin's id. API version 5.
/// </summary>
public interface IPluginBus
{
    /// <summary>Delivers a message to every subscriber of the topic, including the sender's own. Returns at once.</summary>
    void Publish(string topic, string? payload = null);

    /// <summary>
    /// Calls <paramref name="handler"/> for each message on the topic, on a background thread.
    /// Dispose the result to stop; Notch also stops it when the plugin does. Exceptions the handler throws are logged.
    /// </summary>
    IDisposable Subscribe(string topic, Action<PluginMessage> handler);
}

/// <summary>The notch's state as the shell reports it. The shell writes; plugins read through <see cref="IPluginShell"/>.</summary>
public sealed class PluginShellState
{
    private readonly Lock _gate = new();
    private bool _expanded;
    private (string PluginId, string PageId)? _page;
    private bool _cards;
    private bool _dark = true;
    private GlowColor? _accent;

    public event EventHandler? Changed;

    /// <summary>Set by the app: shows the Settings window.</summary>
    public Action? OpenSettings { get; set; }

    public bool IsExpanded
    {
        get { lock (_gate) { return _expanded; } }
    }

    public bool CardsVisible
    {
        get { lock (_gate) { return _expanded && _cards; } }
    }

    public bool IsDark
    {
        get { lock (_gate) { return _dark; } }
    }

    public GlowColor? Accent
    {
        get { lock (_gate) { return _accent; } }
    }

    public bool IsPageVisible(string pluginId, string pageId)
    {
        lock (_gate)
        {
            return _expanded && _page is { } page && page.PluginId == pluginId && page.PageId == pageId;
        }
    }

    /// <summary>Called by the shell. <paramref name="page"/> is the plugin page the tab shows, or null.</summary>
    public void Update(bool expanded, (string PluginId, string PageId)? page, bool cardsTab, bool dark, GlowColor? accent)
    {
        lock (_gate)
        {
            if (_expanded == expanded && _page == page && _cards == cardsTab && _dark == dark && _accent == accent)
            {
                return;
            }

            _expanded = expanded;
            _page = page;
            _cards = cardsTab;
            _dark = dark;
            _accent = accent;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Routes <see cref="IPluginBus"/> messages between plugins.</summary>
public sealed class PluginBus
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Publish(string senderId, string topic, string? payload, Action<string, Exception> onError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        Subscription[] targets;
        lock (_gate)
        {
            targets = [.. _subscriptions.Where(s => s.Topic == topic)];
        }

        var message = new PluginMessage(senderId, topic, payload);
        foreach (Subscription target in targets)
        {
            // Off the sender's thread, and one slow or failing subscriber cannot hold up or break the others.
            _ = Task.Run(() =>
            {
                try
                {
                    target.Handler(message);
                }
                catch (Exception e)
                {
                    onError(target.OwnerId, e);
                }
            });
        }
    }

    public IDisposable Subscribe(string ownerId, string topic, Action<PluginMessage> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(ownerId, topic, handler);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return new Releaser(() =>
        {
            lock (_gate)
            {
                _subscriptions.Remove(subscription);
            }
        });
    }

    private sealed record Subscription(string OwnerId, string Topic, Action<PluginMessage> Handler);

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
