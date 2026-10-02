using System.Runtime.InteropServices;
using Microsoft.Extensions.Time.Testing;
using Notch.Core.Activities;
using Notch.Core.Plugins;
using Notch.Core.Plugins.Checks;

namespace Notch.Core.Tests;

public sealed class PluginCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notch-checker-tests", Guid.NewGuid().ToString("N"));
    private readonly ActivityManager _activities = new(new FakeTimeProvider());
    private readonly PluginCardBoard _cards = new();
    private readonly RecordingAudit _audit = new();
    private readonly List<string> _started = [];

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
            // Left for the temp folder cleanup.
        }
    }

    [Fact]
    public void A_plugin_must_be_approved_and_stops_being_trusted_when_its_files_change()
    {
        string folder = Install("a");
        var trust = new PluginTrustStore(Path.Combine(_root, "trust.json"));
        using PluginManager manager = CreateManager(trust: trust);
        manager.Discover();

        manager.SetEnabled(["a"]);
        Assert.Equal(PluginStatus.Failed, manager.Plugins[0].Status);
        Assert.Contains("Not approved", manager.Plugins[0].Error);
        Assert.Empty(_started);

        // Off and on again, after the user approved it.
        manager.SetEnabled([]);
        Assert.True(manager.Approve("a"));
        manager.SetEnabled(["a"]);
        Assert.Equal(PluginStatus.Running, manager.Plugins[0].Status);
        Assert.Equal(["a"], _started);

        // The same plugin with different files is not what was approved.
        manager.SetEnabled([]);
        File.WriteAllText(Path.Combine(folder, "Plugin.dll"), "swapped");
        manager.SetEnabled(["a"]);
        Assert.Equal(PluginStatus.Failed, manager.Plugins[0].Status);
        Assert.Contains("files changed", manager.Plugins[0].Error);
        Assert.Contains(_audit.Entries, e => e.Plugin == "a" && e.Kind == PluginAuditKinds.Quarantined);
    }

    [Fact]
    public void Review_tells_the_user_what_they_are_approving()
    {
        Install("a", permissions: """["network"]""");
        var trust = new PluginTrustStore(Path.Combine(_root, "trust.json"));
        using PluginManager manager = CreateManager(trust: trust);
        manager.Discover();

        PluginReview review = manager.Review("a")!;

        Assert.False(review.Approved);
        Assert.False(review.FilesChanged);
        Assert.Equal(["network"], review.Permissions);
        Assert.Null(review.Revoked);
        Assert.Null(manager.Review("nope"));

        manager.Approve("a");
        Assert.True(manager.Review("a")!.Approved);
    }

    [Fact]
    public void Plugins_enabled_before_approval_existed_are_carried_over_once()
    {
        Install("a");
        Install("b");
        var trust = new PluginTrustStore(Path.Combine(_root, "trust.json"));
        using PluginManager manager = CreateManager(trust: trust);
        manager.Discover();

        manager.ApproveExisting(["a"]);

        Assert.True(trust.HasRecord("a"));
        Assert.False(trust.HasRecord("b"));
        manager.SetEnabled(["a", "b"]);
        Assert.Equal(PluginStatus.Running, manager.Plugins.Single(p => p.Id == "a").Status);
        Assert.Equal(PluginStatus.Failed, manager.Plugins.Single(p => p.Id == "b").Status);
    }

    [Fact]
    public void A_revoked_plugin_is_not_started()
    {
        Install("a");
        using PluginManager manager = CreateManager(revocations: [new Revocation("a", null, "steals keys")]);
        manager.Discover();

        manager.SetEnabled(["a"]);

        Assert.Equal(PluginStatus.Failed, manager.Plugins[0].Status);
        Assert.Contains("steals keys", manager.Plugins[0].Error);
        Assert.Empty(_started);
        Assert.Contains(_audit.Entries, e => e.Kind == PluginAuditKinds.Revoked);
    }

    [Fact]
    public void A_plugin_that_logs_keys_is_blocked_before_it_runs()
    {
        // This test assembly contains imports of SetWindowsHookEx, BitBlt, SendInput and CreateRemoteThread (HostileImports below).
        string folder = Install("hostile", assembly: "Hostile.dll");
        File.Copy(typeof(PluginCheckerTests).Assembly.Location, Path.Combine(folder, "Hostile.dll"));
        using PluginManager manager = CreateManager();
        manager.Discover();

        manager.SetEnabled(["hostile"]);

        PluginInfo info = manager.Plugins[0];
        Assert.Equal(PluginStatus.Failed, info.Status);
        Assert.StartsWith("Blocked:", info.Error);
        Assert.Empty(_started);
        Assert.Contains(_audit.Entries, e => e.Kind == PluginAuditKinds.Blocked);
    }

    [Fact]
    public void Approving_an_update_that_waits_for_a_restart_still_holds_once_it_is_moved_into_place()
    {
        // An update is unpacked beside the plugin and moved over it at the next start.
        string waiting = Install("a", parent: Path.Combine(_root, "waiting"));
        var trust = new PluginTrustStore(Path.Combine(_root, "trust.json"));
        using PluginManager manager = CreateManager(trust: trust);

        Assert.True(manager.ApproveFiles("a", waiting, "update"));
        Assert.False(manager.ApproveFiles("a", Path.Combine(_root, "nowhere"), "update"));
        Directory.CreateDirectory(PluginsDirectory);
        Directory.Move(waiting, Path.Combine(PluginsDirectory, "a"));
        manager.Discover();
        manager.SetEnabled(["a"]);

        Assert.Equal(PluginStatus.Running, manager.Plugins[0].Status);
    }

    [Fact]
    public void A_plugin_that_is_running_when_it_is_revoked_is_stopped()
    {
        Install("a");
        Install("b");
        List<Revocation> revoked = [];
        using PluginManager manager = CreateManager(revocations: revoked);
        manager.Discover();
        manager.SetEnabled(["a", "b"]);
        Assert.Equal(2, _activities.Snapshot().Count);
        Assert.Empty(manager.EnforceRevocations());

        revoked.Add(new Revocation("a", null, "keylogger"));
        IReadOnlyList<string> stopped = manager.EnforceRevocations();

        Assert.Equal(["a"], stopped);
        Assert.Equal(PluginStatus.Failed, manager.Plugins.Single(p => p.Id == "a").Status);
        Assert.Contains("keylogger", manager.Plugins.Single(p => p.Id == "a").Error);
        Assert.Equal(PluginStatus.Running, manager.Plugins.Single(p => p.Id == "b").Status);
        Assert.Equal(["b"], _activities.Snapshot().Select(a => a.Title));
    }

    [Fact]
    public void A_developers_own_build_skips_approval_but_not_the_scan()
    {
        string folder = Install("dev", parent: Path.Combine(_root, "elsewhere"));
        var trust = new PluginTrustStore(Path.Combine(_root, "trust.json"));
        using PluginManager manager = CreateManager(trust: trust);
        manager.Discover([folder]);

        manager.SetEnabled([]);

        Assert.Equal(PluginStatus.Running, manager.Plugins[0].Status);
    }

    [Fact]
    public void The_kill_switch_stops_every_plugin_and_leaves_them_off()
    {
        Install("a");
        Install("b");
        using PluginManager manager = CreateManager();
        manager.Discover();
        manager.SetEnabled(["a", "b"]);
        Assert.Equal(2, _activities.Snapshot().Count);

        IReadOnlyList<string> stopped = manager.KillAll("test");

        Assert.Equal(["a", "b"], stopped.Order());
        Assert.All(manager.Plugins, p => Assert.Equal(PluginStatus.Disabled, p.Status));
        Assert.Empty(_activities.Snapshot());
        Assert.Contains(_audit.Entries, e => e.Plugin == "a" && e.Kind == PluginAuditKinds.Killed);
    }

    [Fact]
    public void Safe_mode_starts_no_plugins()
    {
        Install("a");
        using PluginManager manager = CreateManager();
        manager.SafeMode = true;
        manager.Discover();

        manager.SetEnabled(["a"]);

        Assert.Equal(PluginStatus.Disabled, manager.Plugins[0].Status);
        Assert.Contains("Safe mode", manager.Plugins[0].Error);
        Assert.Empty(_started);
    }

    [Fact]
    public void The_scanner_names_what_a_plugin_does()
    {
        string folder = Path.Combine(_root, "scan");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(PluginCheckerTests).Assembly.Location, Path.Combine(folder, "Hostile.dll"));

        ScanReport report = PluginScanner.Scan(folder);

        Assert.True(report.Blocked);
        string[] categories = [.. report.Findings.Where(f => f.Severity == ScanSeverity.Block).Select(f => f.Category).Distinct()];
        Assert.Contains("keylogging", categories);
        Assert.Contains("screen-capture", categories);
        Assert.Contains("input-injection", categories);
        Assert.Contains("process-injection", categories);
        Assert.Contains("SetWindowsHookEx", string.Join(' ', report.Findings.Select(f => f.Detail)));
        Assert.StartsWith("Blocked: it uses", report.BlockReason);
    }

    [Fact]
    public void The_scanner_lets_an_ordinary_plugin_through()
    {
        string folder = Path.Combine(_root, "clean");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(PluginManifest).Assembly.Location, Path.Combine(folder, "Notch.Core.dll"));

        ScanReport report = PluginScanner.Scan(folder);

        Assert.False(report.Blocked);
        Assert.Null(report.BlockReason);
    }

    [Theory]
    [InlineData("BreakReminder", "BreakReminder.dll")]
    [InlineData("NeonTheme", "NeonTheme.dll")]
    public void The_sample_plugins_in_this_repository_are_not_blocked(string sample, string assembly)
    {
        // The samples are built as part of the solution; where they were not built there is nothing to scan.
        string? output = FindSampleOutput(sample, assembly);
        if (output is null)
        {
            return;
        }

        ScanReport report = PluginScanner.Scan(output);

        Assert.False(report.Blocked, report.BlockReason);
        Assert.False(report.HasNativeCode);
    }

    [Fact]
    public void A_real_plugin_assembly_runs_only_after_it_is_approved()
    {
        string? output = FindSampleOutput("BreakReminder", "BreakReminder.dll");
        if (output is null)
        {
            return;
        }

        string folder = Path.Combine(PluginsDirectory, "break");
        Directory.CreateDirectory(folder);
        foreach (string file in Directory.EnumerateFiles(output))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        var trust = new PluginTrustStore(Path.Combine(_root, "trust.json"));
        using var manager = new PluginManager(
            PluginsDirectory,
            Path.Combine(_root, "data"),
            _activities,
            _cards,
            new PluginLog(Path.Combine(_root, "plugins.log")),
            checks: new PluginChecks { Audit = _audit, Trust = trust });
        manager.Discover();

        manager.SetEnabled(["notch.break-reminder"]);
        Assert.Equal(PluginStatus.Failed, manager.Plugins[0].Status);
        Assert.Contains("Not approved", manager.Plugins[0].Error);

        manager.SetEnabled([]);
        Assert.True(manager.Approve("notch.break-reminder"));
        manager.SetEnabled(["notch.break-reminder"]);
        Assert.Equal(PluginStatus.Running, manager.Plugins[0].Status);
        Assert.Null(manager.Plugins[0].Error);
    }

    private static string? FindSampleOutput(string sample, string assembly)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string samples = Path.Combine(directory.FullName, "samples", sample, "bin");
            if (Directory.Exists(samples))
            {
                return Directory.EnumerateFiles(samples, assembly, SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, PluginManifest.FileName)));
            }
        }

        return null;
    }

    [Fact]
    public void Native_code_is_flagged_because_it_cannot_be_inspected()
    {
        string folder = Path.Combine(_root, "native");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "thing.dll"), [0x4D, 0x5A, 0x90, 0x00, 0x03]);

        ScanReport report = PluginScanner.Scan(folder);

        Assert.True(report.HasNativeCode);
        Assert.False(report.Blocked);
    }

    [Fact]
    public void A_missing_folder_scans_clean()
    {
        Assert.Empty(PluginScanner.Scan(Path.Combine(_root, "nothing")).Findings);
    }

    [Fact]
    public void Revocations_are_read_from_the_registry_and_can_target_one_version()
    {
        IReadOnlyList<Revocation> list = PluginRevocations.Parse("""
            { "plugins": [], "revoked": [
              { "id": "bad.one", "reason": "keylogger" },
              { "id": "bad.two", "sha256": "ABCDEF", "reason": "one release" },
              { "reason": "no id" }, 7 ] }
            """);

        Assert.Equal(2, list.Count);
        Assert.NotNull(PluginRevocations.Find(list, "bad.one", "anything"));
        Assert.Null(PluginRevocations.Find(list, "bad.two", "other"));
        Assert.NotNull(PluginRevocations.Find(list, "bad.two", "abcdef"));
        Assert.Empty(PluginRevocations.Parse("not json"));
        Assert.Empty(PluginRevocations.Parse("""{ "plugins": [] }"""));
        Assert.Empty(PluginRevocations.Read(Path.Combine(_root, "missing.json")));
    }

    [Fact]
    public void The_registry_on_the_website_still_parses_and_names_no_revocations()
    {
        string? registry = null;
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null && registry is null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "site", "plugins", "registry.json");
            registry = File.Exists(candidate) ? candidate : null;
        }

        if (registry is null)
        {
            return;
        }

        string json = File.ReadAllText(registry);

        Assert.NotEmpty(PluginRegistry.Parse(json).Entries);
        Assert.Empty(PluginRevocations.Parse(json));
    }

    [Fact]
    public void The_trust_hash_changes_when_any_file_changes_and_not_otherwise()
    {
        string folder = Path.Combine(_root, "hash");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.dll"), "one");
        File.WriteAllText(Path.Combine(folder, "sub", "b.json"), "two");

        string first = PluginTrustStore.TreeHash(folder);
        Assert.Equal(first, PluginTrustStore.TreeHash(folder));

        File.WriteAllText(Path.Combine(folder, "sub", "b.json"), "three");
        Assert.NotEqual(first, PluginTrustStore.TreeHash(folder));
    }

    [Fact]
    public void Approvals_are_remembered_between_runs()
    {
        string path = Path.Combine(_root, "trust.json");
        new PluginTrustStore(path).Approve("a", "abc");

        var reopened = new PluginTrustStore(path);

        Assert.True(reopened.IsApproved("a", "abc"));
        Assert.False(reopened.IsApproved("a", "different"));
        reopened.Forget("a");
        Assert.False(new PluginTrustStore(path).HasRecord("a"));
    }

    [Fact]
    public void A_corrupt_approval_file_approves_nothing()
    {
        string path = Path.Combine(_root, "trust.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{ not json");

        Assert.False(new PluginTrustStore(path).HasRecord("a"));
    }

    [Fact]
    public void The_audit_log_is_one_json_object_per_line()
    {
        string path = Path.Combine(_root, "audit.log");
        var audit = new FilePluginAudit(path);

        audit.Record("a", PluginAuditKinds.Denied, "tried the clipboard");
        audit.Record("b", PluginAuditKinds.Killed, "kill switch");

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"denied\"", lines[0]);
        Assert.Contains("tried the clipboard", lines[0]);
    }

    private PluginManager CreateManager(PluginTrustStore? trust = null, IReadOnlyList<Revocation>? revocations = null) => new(
        PluginsDirectory,
        Path.Combine(_root, "data"),
        _activities,
        _cards,
        new PluginLog(Path.Combine(_root, "plugins.log")),
        (manifest, _) => () => new CountingPlugin(manifest.Id, _started),
        new PluginChecks
        {
            Audit = _audit,
            Trust = trust,
            Revocations = revocations is null ? null : () => revocations,
        });

    private string Install(string id, string assembly = "Plugin.dll", string? permissions = null, string? parent = null)
    {
        string directory = Path.Combine(parent ?? PluginsDirectory, id);
        Directory.CreateDirectory(directory);
        string extra = permissions is null ? "" : $", \"permissions\": {permissions}";
        File.WriteAllText(
            Path.Combine(directory, PluginManifest.FileName),
            $$"""{ "id": "{{id}}", "name": "{{id}}", "assembly": "{{assembly}}", "apiVersion": 1{{extra}} }""");
        File.WriteAllText(Path.Combine(directory, "Plugin.dll"), "placeholder");
        return directory;
    }

    private sealed class CountingPlugin(string id, List<string> started) : INotchPlugin
    {
        public void Start(IPluginHost host)
        {
            started.Add(id);
            host.Activities.Publish(new Activity { Id = "x", Tier = ActivityTier.Ongoing, Title = id });
        }

        public void Stop()
        {
        }
    }

    private sealed class RecordingAudit : IPluginAudit
    {
        private readonly Lock _gate = new();
        private readonly List<(string Plugin, string Kind, string Detail)> _entries = [];

        public IReadOnlyList<(string Plugin, string Kind, string Detail)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public void Record(string pluginId, string kind, string detail)
        {
            lock (_gate)
            {
                _entries.Add((pluginId, kind, detail));
            }
        }
    }
}

/// <summary>
/// Declarations only, never called: gives the scanner tests an assembly that imports what a
/// key logger, screen recorder and input faker would.
/// </summary>
internal static class HostileImports
{
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    internal static extern nint SetWindowsHookEx(int id, nint proc, nint module, uint thread);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);

    [DllImport("user32.dll")]
    internal static extern uint SendInput(uint count, nint inputs, int size);

    [DllImport("kernel32.dll")]
    internal static extern nint CreateRemoteThread(nint process, nint attributes, nuint stack, nint start, nint parameter, uint flags, out uint id);
}
