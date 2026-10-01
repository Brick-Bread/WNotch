using System.Reflection;
using Notch.Core.Activities;

namespace Notch.Core.Plugins;

public enum PluginStatus
{
    /// <summary>Installed but not switched on; none of its code has run.</summary>
    Disabled,

    Running,

    /// <summary>Switched on, but loading or starting it threw. See <see cref="PluginInfo.Error"/>.</summary>
    Failed,

    /// <summary>Its folder does not hold a usable plugin. See <see cref="PluginInfo.Error"/>.</summary>
    Invalid,
}

/// <summary>A plugin as listed in settings.</summary>
/// <param name="Id">Null when the manifest could not be read.</param>
/// <param name="Name">The manifest's name, or the folder name when there is no usable manifest.</param>
/// <param name="AlwaysEnabled">Loaded with <c>--plugin=</c>, so it runs regardless of the user's settings.</param>
public sealed record PluginInfo(
    string? Id,
    string Name,
    string? Version,
    string? Author,
    string? Description,
    string Directory,
    PluginStatus Status,
    string? Error,
    bool AlwaysEnabled,
    string? Repository = null,
    string? InstalledTag = null);

/// <summary>
/// Finds plugins on disk and starts and stops them. A plugin is a folder holding a
/// <c>plugin.json</c> and an assembly; see docs/plugins.md. Nothing in a plugin runs until the
/// user has enabled it.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private const string LogSource = "notch";

    private readonly Lock _gate = new();
    private readonly List<Slot> _slots = [];
    private readonly HashSet<string> _pinnedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;
    private readonly ActivityManager _activities;
    private readonly PluginCardBoard _cards;
    private readonly PluginLog _log;
    private readonly Func<PluginManifest, string, Func<INotchPlugin>> _loader;
    private bool _disposed;

    /// <param name="pluginsDirectory">Each subfolder with a <c>plugin.json</c> is a plugin. Need not exist.</param>
    /// <param name="dataDirectory">Parent of the per-plugin data folders.</param>
    /// <param name="loader">Turns a manifest and its folder into a plugin factory; tests replace the assembly loader.</param>
    public PluginManager(
        string pluginsDirectory,
        string dataDirectory,
        ActivityManager activities,
        PluginCardBoard cards,
        PluginLog log,
        Func<PluginManifest, string, Func<INotchPlugin>>? loader = null)
    {
        PluginsDirectory = pluginsDirectory;
        _dataDirectory = dataDirectory;
        _activities = activities;
        _cards = cards;
        _log = log;
        _loader = loader ?? PluginLoader.Load;
    }

    /// <summary>Raised when the list of plugins or a plugin's status changed, on whichever thread caused it.</summary>
    public event EventHandler? Changed;

    public string PluginsDirectory { get; }

    /// <summary>The pages plugins show as tabs, and their consoles.</summary>
    public PluginPageBoard Pages { get; } = new();

    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _slots.OrderBy(s => s.Directory, StringComparer.OrdinalIgnoreCase).Select(s => s.ToInfo())];
            }
        }
    }

    /// <summary>True once the plugin's assembly is in the process, which lasts until Notch exits, running or not.</summary>
    public bool IsLoaded(string pluginId)
    {
        lock (_gate)
        {
            return _slots.Any(s => s.Manifest?.Id == pluginId && s.Factory is not null);
        }
    }

    /// <summary>The id a plugin's activity gets in the <see cref="ActivityManager"/>.</summary>
    public static string ActivityIdFor(string pluginId, string activityId) => $"plugin.{pluginId}.{activityId}";

    /// <summary>
    /// Reads the manifests of plugins that have not been loaded, so new and replaced ones show
    /// up; plugins that have run are left alone. This only lists them: <see cref="SetEnabled"/>
    /// starts them.
    /// </summary>
    /// <param name="pinnedDirectories">Plugin folders outside <see cref="PluginsDirectory"/> that run without being enabled (<c>--plugin=</c>).</param>
    public void Discover(IEnumerable<string>? pinnedDirectories = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Forget every folder no code was loaded from: it may have been fixed, replaced by
            // a newer version, or deleted since it was last read.
            _slots.RemoveAll(s => s.Factory is null && s.Status is PluginStatus.Invalid or PluginStatus.Disabled);

            _pinnedDirectories.UnionWith((pinnedDirectories ?? []).Select(Path.GetFullPath));
            foreach (string directory in _pinnedDirectories)
            {
                Add(directory, pinned: true);
            }

            foreach (string directory in Subdirectories(PluginsDirectory))
            {
                Add(directory, pinned: false);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts the listed plugins that are not running and stops running ones that are not listed.</summary>
    public void SetEnabled(IEnumerable<string> pluginIds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            HashSet<string> enabled = [.. pluginIds];
            foreach (Slot slot in _slots)
            {
                if (slot.Manifest is null)
                {
                    continue;
                }

                bool wanted = slot.Pinned || enabled.Contains(slot.Manifest.Id);
                switch (slot.Status)
                {
                    case PluginStatus.Disabled when wanted:
                        Start(slot, slot.Manifest);
                        break;

                    case PluginStatus.Running when !wanted:
                        Stop(slot);
                        break;

                    // Switching a failed plugin off and on again is how the user retries it.
                    case PluginStatus.Failed when !wanted:
                        slot.Status = PluginStatus.Disabled;
                        slot.Error = null;
                        break;
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Slot slot in _slots.Where(s => s.Status == PluginStatus.Running))
            {
                Stop(slot);
            }
        }
    }

    private static IEnumerable<string> Subdirectories(string directory)
    {
        try
        {
            // Names starting with a dot are PluginInstaller's work folders.
            return Directory.Exists(directory)
                ? [.. Directory.GetDirectories(directory)
                    .Where(d => !Path.GetFileName(d).StartsWith('.'))
                    .Order(StringComparer.OrdinalIgnoreCase)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Add(string directory, bool pinned)
    {
        if (_slots.Any(s => string.Equals(s.Directory, directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var slot = new Slot(directory, pinned);
        try
        {
            string manifestPath = Path.Combine(directory, PluginManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                // Any other folder in the plugins directory is simply not a plugin.
                if (!pinned)
                {
                    return;
                }

                throw new PluginLoadException($"There is no {PluginManifest.FileName} in this folder.");
            }

            PluginManifest manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
            if (_slots.FirstOrDefault(s => s.Manifest?.Id == manifest.Id) is { } other)
            {
                throw new PluginLoadException($"Its id '{manifest.Id}' is already used by the plugin in {other.Directory}.");
            }

            slot.Manifest = manifest;
        }
        catch (Exception e) when (e is PluginLoadException or IOException or UnauthorizedAccessException)
        {
            slot.Status = PluginStatus.Invalid;
            slot.Error = e.Message;
            _log.Write(LogSource, "error", $"Ignoring the plugin in {directory}: {e.Message}");
        }

        _slots.Add(slot);
    }

    private void Start(Slot slot, PluginManifest manifest)
    {
        try
        {
            slot.Factory ??= _loader(manifest, slot.Directory);
            slot.Host = new PluginHost(
                manifest, slot.Directory, Path.Combine(_dataDirectory, manifest.Id), _activities, _cards, Pages, _log);
            slot.Instance = slot.Factory();
            slot.Instance.Start(slot.Host);

            slot.Status = PluginStatus.Running;
            slot.Error = null;
            _log.Write(LogSource, "info", $"Started {manifest.Id} {manifest.Version}".TrimEnd());
        }
        catch (Exception e)
        {
            // Plugin code can throw anything, and none of it may take the app down.
            // Reflection wraps what the plugin's constructor threw; the cause is what the user needs.
            Exception cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;

            slot.Host?.Close();
            slot.Host = null;
            slot.Instance = null;
            slot.Status = PluginStatus.Failed;
            slot.Error = cause.Message;
            _log.Write(LogSource, "error", $"{manifest.Id} failed to start.", cause);
        }
    }

    private void Stop(Slot slot)
    {
        string id = slot.Manifest!.Id;
        try
        {
            slot.Instance?.Stop();
            _log.Write(LogSource, "info", $"Stopped {id}");
        }
        catch (Exception e)
        {
            _log.Write(LogSource, "error", $"{id} failed while stopping.", e);
        }

        slot.Host?.Close();
        slot.Host = null;
        slot.Instance = null;
        slot.Status = PluginStatus.Disabled;
    }

    private sealed class Slot(string directory, bool pinned)
    {
        public string Directory { get; } = directory;

        public bool Pinned { get; } = pinned;

        public PluginManifest? Manifest { get; set; }

        public PluginStatus Status { get; set; } = PluginStatus.Disabled;

        public string? Error { get; set; }

        /// <summary>Kept after the first load: an assembly cannot be loaded twice, but the plugin can be restarted.</summary>
        public Func<INotchPlugin>? Factory { get; set; }

        public INotchPlugin? Instance { get; set; }

        public PluginHost? Host { get; set; }

        public PluginInfo ToInfo() => new(
            Manifest?.Id,
            Manifest?.Name ?? Path.GetFileName(Directory.TrimEnd('\\', '/')),
            Manifest?.Version,
            Manifest?.Author,
            Manifest?.Description,
            Directory,
            Status,
            Error,
            Pinned,
            PluginOrigin.SourceOf(Directory, Manifest)?.ToString(),
            PluginOrigin.Read(Directory)?.Tag);
    }
}
