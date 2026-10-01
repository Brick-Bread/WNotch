using Notch.Core.Activities;

namespace Notch.Core.Plugins;

// The plugin API: everything a plugin author programs against is in this file, plus
// PluginManifest and the activity types in Notch.Core.Activities. See docs/plugins.md.

/// <summary>Identifies the plugin API a build of Notch offers.</summary>
public static class PluginApi
{
    /// <summary>
    /// Raised whenever the API gains members. A plugin states the version it was written for in
    /// its manifest (<c>apiVersion</c>); Notch runs plugins written for this version or an older
    /// one, and refuses those that need a newer one.
    /// </summary>
    public const int Version = 1;
}

/// <summary>
/// A plugin's entry point. Notch creates one instance of the plugin's single public class that
/// implements this interface, using its parameterless constructor.
/// </summary>
/// <remarks>
/// <see cref="Start"/> and <see cref="Stop"/> are called on a background thread, never
/// concurrently. An exception thrown from either is logged and marks the plugin as failed; it
/// does not affect Notch or other plugins. Exceptions on threads the plugin starts itself are
/// not caught and end the app, so guard timer callbacks and background work.
/// </remarks>
public interface INotchPlugin
{
    /// <summary>
    /// Called when the plugin is switched on: at app start, or when the user enables it in
    /// settings. Set up timers and subscriptions here and return promptly; long work belongs on
    /// a background task. Keep <paramref name="host"/> for later use.
    /// </summary>
    void Start(IPluginHost host);

    /// <summary>
    /// Called when the plugin is switched off or Notch exits. Stop timers and release resources.
    /// Activities and cards the plugin left behind are removed by Notch afterwards, and
    /// <see cref="IPluginHost"/> calls made after this point are ignored.
    /// </summary>
    void Stop();
}

/// <summary>What Notch offers a plugin. Every member is safe to call from any thread.</summary>
public interface IPluginHost
{
    /// <summary>The plugin's own <c>plugin.json</c>.</summary>
    PluginManifest Manifest { get; }

    /// <summary>The folder the plugin was loaded from. Treat it as read-only: updating a plugin replaces it.</summary>
    string PluginDirectory { get; }

    /// <summary>
    /// A folder for the plugin's own files (caches, state), kept across plugin and app updates.
    /// Created on first access.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>Shows things in the pill.</summary>
    IPluginActivities Activities { get; }

    /// <summary>Shows cards on the notch's Plugins tab.</summary>
    IPluginCards Cards { get; }

    /// <summary>Small values the plugin wants to keep between runs, such as its options.</summary>
    IPluginSettings Settings { get; }

    /// <summary>Writes to the shared plugin log.</summary>
    IPluginLog Log { get; }
}

/// <summary>
/// The plugin's activities in the pill. Ids are private to the plugin: Notch prefixes them, so
/// they cannot collide with the app's own activities or another plugin's.
/// </summary>
public interface IPluginActivities
{
    /// <summary>
    /// Shows <paramref name="activity"/>, or updates the one already showing under the same
    /// <see cref="Activity.Id"/>. Which activity the pill displays is decided by
    /// <see cref="ActivityTier"/>, then by which was published most recently.
    /// <see cref="ActivityTier.Transient"/> activities disappear after their
    /// <see cref="Activity.Lifetime"/>; the other tiers stay until removed.
    /// </summary>
    void Publish(Activity activity);

    /// <summary>Removes an activity by the id it was published under. False when it was not showing.</summary>
    bool Remove(string id);

    /// <summary>Removes every activity the plugin has published.</summary>
    void Clear();
}

/// <summary>The plugin's cards on the Plugins tab of the expanded notch. The tab appears while any plugin has a card.</summary>
public interface IPluginCards
{
    /// <summary>Adds <paramref name="card"/>, or replaces the plugin's card with the same <see cref="PluginCard.Id"/> in place.</summary>
    void Set(PluginCard card);

    /// <summary>Removes a card by id. False when there was no such card.</summary>
    bool Remove(string id);

    /// <summary>Removes all of the plugin's cards.</summary>
    void Clear();
}

/// <summary>
/// A small panel on the Plugins tab: a caption, a headline value and a line of detail. Cards are
/// plain data, so plugins need no UI framework; Notch draws them in its own style.
/// </summary>
public sealed record PluginCard
{
    /// <summary>Stable per card and unique within the plugin, e.g. "status".</summary>
    public required string Id { get; init; }

    /// <summary>Small caption at the top, e.g. "Build".</summary>
    public required string Label { get; init; }

    /// <summary>The headline, shown large on one line. Keep it to a few characters.</summary>
    public string? Value { get; init; }

    /// <summary>Secondary text under the value; wraps to two lines.</summary>
    public string? Detail { get; init; }

    /// <summary>0..1 draws a bar along the bottom of the card; null for none.</summary>
    public double? Progress { get; init; }

    /// <summary>
    /// Run when the user clicks the card; null makes the card non-interactive. Called on a
    /// background thread, and exceptions it throws are logged rather than propagated.
    /// </summary>
    public Action? Clicked { get; init; }
}

/// <summary>
/// Key/value storage for a plugin, saved as JSON in <c>settings.json</c> inside
/// <see cref="IPluginHost.DataDirectory"/>. Users may edit that file by hand, so read values
/// defensively. Meant for a handful of small values, not bulk data.
/// </summary>
public interface IPluginSettings
{
    /// <summary>
    /// The value stored under <paramref name="key"/>, or <paramref name="fallback"/> when the
    /// key is missing or holds something that is not a <typeparamref name="T"/>.
    /// </summary>
    T Get<T>(string key, T fallback);

    /// <summary>Stores a JSON-serializable value and saves the file.</summary>
    void Set<T>(string key, T value);

    /// <summary>Deletes a key. False when it was not set.</summary>
    bool Remove(string key);
}

/// <summary>
/// Writes to <c>%LocalAppData%\Notch\plugins.log</c>, which all plugins share; each line carries
/// the plugin's id. Notch also records plugin starts, stops and failures there.
/// </summary>
public interface IPluginLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}
