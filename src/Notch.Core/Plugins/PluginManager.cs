using System.Text.Json;
using System.Reflection;
using Notch.Core.Activities;
using Notch.Core.Plugins.Checks;

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
    string? InstalledTag = null,
    IReadOnlyList<PluginSettingField>? Settings = null,
    IReadOnlyList<string>? Permissions = null);

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
    private readonly PluginChecks? _checks;
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
        Func<PluginManifest, string, Func<INotchPlugin>>? loader = null,
        PluginChecks? checks = null)
    {
        PluginsDirectory = pluginsDirectory;
        _dataDirectory = dataDirectory;
        _activities = activities;
        _cards = cards;
        _log = log;
        _checks = checks;
        _loader = loader ?? PluginLoader.Load;
    }

    /// <summary>When true, no plugin is started: the way back into Notch after a plugin broke it.</summary>
    public bool SafeMode { get; set; }

    /// <summary>Raised when the list of plugins or a plugin's status changed, on whichever thread caused it.</summary>
    public event EventHandler? Changed;

    public string PluginsDirectory { get; }

    /// <summary>The pages plugins show as tabs, and their consoles.</summary>
    public PluginPageBoard Pages { get; } = new();

    /// <summary>The themes plugins offer.</summary>
    public PluginThemeBoard Themes { get; } = new();

    /// <summary>The frames plugins draw the glow with.</summary>
    public PluginGlowBoard Glow { get; } = new();

    /// <summary>The loudness of the sound output, for plugins that read it. The app supplies the analysis.</summary>
    public PluginAudioHub Audio { get; } = new();

    /// <summary>The shell writes the notch's state here for plugins to read.</summary>
    public PluginShellState ShellState { get; } = new();

    /// <summary>Messages between plugins.</summary>
    public PluginBus Bus { get; } = new();

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
                if (SafeMode && wanted && slot.Status == PluginStatus.Disabled)
                {
                    slot.Error = "Safe mode: plugins are switched off until Notch is started normally.";
                    continue;
                }

                switch (slot.Status)
                {
                    case PluginStatus.Disabled when wanted:
                        slot.Error = null;
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

    /// <summary>
    /// The current value of each of a plugin's declared options, as JSON, for the Settings window.
    /// Secret options are never returned. Values that are not set are missing from the result.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> SettingValues(string pluginId)
    {
        PluginSettingsStore store = StoreOf(pluginId, out PluginManifest? manifest);
        Dictionary<string, JsonElement> values = [];
        foreach (PluginSettingField field in manifest?.Settings ?? [])
        {
            if (field.Type != PluginSettingType.Secret && store.Raw(field.Key) is { } value)
            {
                values[field.Key] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Saves options the user changed in the Settings window. A running plugin is told (when it
    /// listens for <see cref="IPluginSettings.Changed"/>) or restarted so it starts from the new values.
    /// </summary>
    /// <param name="values">Key to new value, as JSON. Keys the manifest does not list are ignored.</param>
    public void ApplySettings(string pluginId, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        PluginSettingsStore store = StoreOf(pluginId, out PluginManifest? manifest);
        string[] known = [.. (manifest?.Settings ?? []).Select(f => f.Key)];
        bool restart = false;
        foreach ((string key, JsonElement value) in values.Where(v => known.Contains(v.Key)))
        {
            store.SetFromUser(key, value);
            restart |= !store.HandlesChanges;
        }

        if (restart)
        {
            Restart(pluginId);
        }
    }

    /// <summary>Stops a running plugin and starts a fresh instance of it. Does nothing for one that is not running.</summary>
    public void Restart(string pluginId)
    {
        lock (_gate)
        {
            if (_disposed || _slots.FirstOrDefault(s => s.Manifest?.Id == pluginId && s.Status == PluginStatus.Running) is not { } slot)
            {
                return;
            }

            Stop(slot);
            Start(slot, slot.Manifest!);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The store a running plugin is using, so two copies of the file do not disagree; a new one for a plugin that is not running.</summary>
    private PluginSettingsStore StoreOf(string pluginId, out PluginManifest? manifest)
    {
        lock (_gate)
        {
            Slot? slot = _slots.FirstOrDefault(s => s.Manifest?.Id == pluginId);
            manifest = slot?.Manifest;
            return slot?.Host?.SettingsStore ?? new PluginSettingsStore(Path.Combine(_dataDirectory, pluginId, "settings.json"));
        }
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
            if (Refusal(slot, manifest) is { } refusal)
            {
                slot.Status = PluginStatus.Failed;
                slot.Error = refusal;
                _log.Write(LogSource, "warn", $"{manifest.Id} was not started. {refusal}");
                return;
            }

            slot.Factory ??= _loader(manifest, slot.Directory);
            slot.Host = new PluginHost(
                manifest, slot.Directory, Path.Combine(_dataDirectory, manifest.Id), _activities, _cards, Pages, Themes, Glow, Audio, ShellState, Bus, _log);
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

    /// <summary>Why a plugin must not be started now, or null when it may be.</summary>
    private string? Refusal(Slot slot, PluginManifest manifest)
    {
        if (_checks is null)
        {
            return null;
        }

        string hash = PluginTrustStore.TreeHash(slot.Directory);
        if (_checks.Revocations is { } revocations
            && PluginRevocations.Find(revocations(), manifest.Id, hash) is { } revoked)
        {
            _checks.Audit.Record(manifest.Id, PluginAuditKinds.Revoked, revoked.Reason);
            return "Withdrawn by the plugin list" + (revoked.Reason.Length > 0 ? ": " + revoked.Reason : ".");
        }

        ScanReport scan = PluginScanner.Scan(slot.Directory);
        if (scan.Blocked)
        {
            _checks.Audit.Record(manifest.Id, PluginAuditKinds.Blocked, string.Join("; ", scan.Findings.Where(f => f.Severity == ScanSeverity.Block).Select(f => f.Detail)));
            return scan.BlockReason;
        }

        // A plugin run with --plugin= is the developer's own build, which changes all the time.
        if (!slot.Pinned && _checks.Trust is { } trust && !trust.IsApproved(manifest.Id, hash))
        {
            _checks.Audit.Record(manifest.Id, PluginAuditKinds.Quarantined, trust.HasRecord(manifest.Id) ? "files changed" : "not approved");
            return trust.HasRecord(manifest.Id)
                ? "Its files changed since you approved it. Switch it off and on again to review it."
                : "Not approved yet. Switch it on in Settings to review and approve it.";
        }

        _checks.Audit.Record(manifest.Id, PluginAuditKinds.Started, scan.Findings.Count == 0 ? "no findings" : $"{scan.Findings.Count} findings");
        return null;
    }

    /// <summary>What the user is shown before switching a plugin on. Null when there is no plugin with this id.</summary>
    public PluginReview? Review(string pluginId)
    {
        Slot? slot;
        lock (_gate)
        {
            slot = _slots.FirstOrDefault(s => s.Manifest?.Id == pluginId);
        }

        if (slot?.Manifest is not { } manifest)
        {
            return null;
        }

        string hash = PluginTrustStore.TreeHash(slot.Directory);
        bool approved = _checks?.Trust?.IsApproved(pluginId, hash) ?? true;
        return new PluginReview(
            slot.ToInfo(),
            manifest.Permissions,
            PluginScanner.Scan(slot.Directory),
            approved,
            !approved && (_checks?.Trust?.HasRecord(pluginId) ?? false),
            _checks?.Revocations is { } revocations ? PluginRevocations.Find(revocations(), pluginId, hash) : null);
    }

    /// <summary>Records that the user approved the plugin's files as they are now. False when there is no such plugin.</summary>
    public bool Approve(string pluginId, string detail = "approved by the user")
    {
        Slot? slot;
        lock (_gate)
        {
            slot = _slots.FirstOrDefault(s => s.Manifest?.Id == pluginId);
        }

        if (slot?.Manifest is null || _checks?.Trust is not { } trust)
        {
            return false;
        }

        trust.Approve(pluginId, PluginTrustStore.TreeHash(slot.Directory));
        _checks.Audit.Record(pluginId, PluginAuditKinds.Approved, detail);
        return true;
    }

    /// <summary>
    /// Records that the user approved the files in <paramref name="directory"/> for a plugin, e.g. an
    /// update that waits for a restart. Moving the folder does not change what is approved: the
    /// record is of the files' names and contents.
    /// </summary>
    public bool ApproveFiles(string pluginId, string directory, string detail)
    {
        if (_checks?.Trust is not { } trust || !Directory.Exists(directory))
        {
            return false;
        }

        trust.Approve(pluginId, PluginTrustStore.TreeHash(directory));
        _checks.Audit.Record(pluginId, PluginAuditKinds.Approved, detail);
        return true;
    }

    /// <summary>
    /// Stops running plugins that the registry has withdrawn since they started. Call after the
    /// registry was loaded. Returns the ids of the plugins that were stopped.
    /// </summary>
    public IReadOnlyList<string> EnforceRevocations()
    {
        if (_checks?.Revocations is not { } revocations)
        {
            return [];
        }

        IReadOnlyList<Revocation> list = revocations();
        List<string> stopped = [];
        lock (_gate)
        {
            foreach (Slot slot in _slots.Where(s => s.Status == PluginStatus.Running && s.Manifest is not null).ToList())
            {
                if (PluginRevocations.Find(list, slot.Manifest!.Id, PluginTrustStore.TreeHash(slot.Directory)) is not { } revoked)
                {
                    continue;
                }

                stopped.Add(slot.Manifest.Id);
                _checks.Audit.Record(slot.Manifest.Id, PluginAuditKinds.Revoked, revoked.Reason);
                Stop(slot);
                slot.Status = PluginStatus.Failed;
                slot.Error = "Withdrawn by the plugin list" + (revoked.Reason.Length > 0 ? ": " + revoked.Reason : ".");
            }
        }

        if (stopped.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return stopped;
    }

    /// <summary>
    /// Approves plugins that were switched on before approval existed, so updating Notch does not
    /// switch them off. Plugins that already have a record are left alone.
    /// </summary>
    public void ApproveExisting(IEnumerable<string> pluginIds)
    {
        foreach (string id in pluginIds)
        {
            if (_checks?.Trust is { } trust && !trust.HasRecord(id))
            {
                Approve(id, "carried over from an earlier version of Notch");
            }
        }
    }

    /// <summary>
    /// The kill switch: stops every running plugin and leaves them switched off. A plugin runs
    /// inside Notch, so one that ignores <see cref="INotchPlugin.Stop"/> cannot be interrupted;
    /// everything it was showing is removed regardless, and it is left switched off.
    /// Returns the ids of the plugins that were running.
    /// </summary>
    public IReadOnlyList<string> KillAll(string reason)
    {
        List<string> stopped = [];
        lock (_gate)
        {
            foreach (Slot slot in _slots.Where(s => s.Status == PluginStatus.Running && s.Manifest is not null))
            {
                stopped.Add(slot.Manifest!.Id);
                _checks?.Audit.Record(slot.Manifest.Id, PluginAuditKinds.Killed, reason);
                Stop(slot);
            }
        }

        _log.Write(LogSource, "warn", $"All plugins were stopped: {reason}");
        Changed?.Invoke(this, EventArgs.Empty);
        return stopped;
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
            PluginOrigin.Read(Directory)?.Tag,
            Manifest?.Settings,
            Manifest?.Permissions);
    }
}
