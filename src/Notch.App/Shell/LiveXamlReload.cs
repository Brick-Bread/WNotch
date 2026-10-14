using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Notch.Core.Activities;

namespace Notch.App.Shell;

/// <summary>
/// A development aid: watches the app's own look files (<c>Themes/Design.xaml</c>, <c>Styles.xaml</c>,
/// <c>Dark.xaml</c> and <c>Light.xaml</c>) on disk and re-applies each one the moment it changes, so the
/// notch can be restyled live while an agent or a person edits the XAML. Because everything in the UI
/// refers to these dictionaries' keys with <c>DynamicResource</c>, swapping a dictionary restyles the
/// running notch without rebuilding or restarting it.
///
/// Switched on with <c>--dev-reload</c> (or <c>NOTCH_DEV_RELOAD</c>); it needs the source on disk, which
/// an installed copy does not have, so it does nothing unless the source folder is found or given with
/// <c>--reload-from=</c>. A file that does not parse mid-edit is reported and the old look is kept.
/// </summary>
internal static class LiveXamlReload
{
    private static readonly string[] Watched = ["Design.xaml", "Styles.xaml", "Dark.xaml", "Light.xaml"];

    // The dictionary currently merged for each file, so a second reload replaces the first, not the original.
    private static readonly Dictionary<string, ResourceDictionary> _live = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _debounce;
    private static ActivityManager? _activities;
    private static string _themesDir = "";

    /// <summary>
    /// Begins watching. <paramref name="reloadFrom"/> is the value of <c>--reload-from=</c>: the source
    /// checkout, the <c>Notch.App</c> folder or its <c>Themes</c> folder; null to find it next to this build.
    /// </summary>
    public static void Start(string? reloadFrom, ActivityManager activities)
    {
        if (_watcher is not null)
        {
            return;
        }

        _activities = activities;
        if (FindThemesDir(reloadFrom) is not { } dir)
        {
            Notice("live-reload", "Live reload is off", "The app's XAML source was not found; pass --reload-from=<source folder>.");
            return;
        }

        _themesDir = dir;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => Flush();

        _watcher = new FileSystemWatcher(dir, "*.xaml")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += (_, e) => OnChanged(_watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, dir, Path.GetFileName(e.FullPath)));

        Notice("live-reload", "Live reload is on", $"Editing the look under {dir} restyles the notch as you save.");
    }

    public static void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Stop();
    }

    private static void OnChanged(object? sender, FileSystemEventArgs e)
    {
        if (!Watched.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // The watcher raises on a background thread, often several times per save; gather and act once.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _pending.Add(e.Name!);
            _debounce?.Stop();
            _debounce?.Start();
        });
    }

    private static void Flush()
    {
        _debounce?.Stop();
        string[] names = [.. _pending];
        _pending.Clear();
        foreach (string name in names)
        {
            Reload(name);
        }
    }

    /// <summary>Loads one file from disk and swaps it for the dictionary of the same name in the app's resources.</summary>
    private static void Reload(string name)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        int index = _live.TryGetValue(name, out ResourceDictionary? current) && merged.IndexOf(current) is var at and >= 0
            ? at
            : IndexByFileName(merged, name);

        // Not currently merged — e.g. the colour file for the theme that is not showing. It applies when chosen.
        if (index < 0)
        {
            return;
        }

        ResourceDictionary fresh;
        try
        {
            fresh = LoadFromDisk(Path.Combine(_themesDir, name));
        }
        catch (Exception ex)
        {
            // Mid-edit the file often does not parse yet; keep the working look and say what is wrong.
            Notice("live-reload-error", $"{name} did not reload", FirstLine(ex.Message));
            return;
        }

        // Remove then re-insert at the same spot: order is kept (theme colours stay layered over the styles),
        // and both steps run on the UI thread between layout passes, so no lookup ever finds the keys missing.
        merged.RemoveAt(index);
        merged.Insert(index, fresh);
        _live[name] = fresh;
        Notice("live-reload-ok", $"Reloaded {name}", "The notch was restyled from your edit.");
    }

    private static ResourceDictionary LoadFromDisk(string path)
    {
        // An editor may still hold the file for a moment after it writes; a couple of quick retries ride that out.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var context = new ParserContext { BaseUri = new Uri(path) };
                return XamlReader.Load(stream, context) as ResourceDictionary
                    ?? throw new InvalidOperationException($"{Path.GetFileName(path)} must be a ResourceDictionary.");
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static int IndexByFileName(IList<ResourceDictionary> merged, string name)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is not null && Path.GetFileName(source).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Resolves the <c>Themes</c> source folder from <c>--reload-from=</c>, or finds it above this build.</summary>
    private static string? FindThemesDir(string? reloadFrom)
    {
        if (!string.IsNullOrWhiteSpace(reloadFrom))
        {
            string[] candidates =
            [
                reloadFrom,
                Path.Combine(reloadFrom, "Themes"),
                Path.Combine(reloadFrom, "src", "Notch.App", "Themes"),
            ];
            return candidates.FirstOrDefault(IsThemesDir);
        }

        // A dev build runs out of src/Notch.App/bin/<cfg>/<tfm>; the source Themes folder is a few levels up.
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string themes = Path.Combine(dir.FullName, "Themes");
            if (IsThemesDir(themes))
            {
                return themes;
            }
        }

        return null;
    }

    private static bool IsThemesDir(string dir) => File.Exists(Path.Combine(dir, "Design.xaml"));

    private static string FirstLine(string text) => text.Split('\n', '\r')[0].Trim();

    private static void Notice(string id, string title, string detail) => _activities?.Publish(new Activity
    {
        Id = "notice." + id,
        Tier = ActivityTier.Transient,
        Title = title,
        Detail = detail,
        Lifetime = TimeSpan.FromSeconds(3),
    });
}
