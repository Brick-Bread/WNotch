using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.Win32;
using Notch.Core.Activities;
using Notch.Core.Settings;
using Notch.Core.Updates;
using Activity = Notch.Core.Activities.Activity;
using Glow = Notch.Core.Activities.Glow;

namespace Notch.App.Updates;

/// <summary>
/// Keeps an installed copy up to date without the user doing anything: checks GitHub for a
/// newer release, downloads its installer, and runs it silently once the app is idle. The
/// installer restarts the app when it is done.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    /// <summary>Passed by the installer when it restarts the app after an update.</summary>
    public const string UpdatedFlag = "--updated";

    private const string ActivityId = "update";
    private const string UpdateGlyph = "";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan IdleRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetrySameReleaseAfter = TimeSpan.FromHours(24);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ActivityManager _activities;
    private readonly Func<bool> _isBusy;
    private readonly Action _shutdown;
    private readonly Updater _updater;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _timer = new();
    private readonly string _downloadFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "updates");

    private ReleaseInfo? _pendingRelease;
    private string? _pendingInstaller;
    private bool _working;

    /// <param name="isBusy">True while restarting would interrupt the user (open terminal sessions, a running timer, the notch in use).</param>
    /// <param name="shutdown">Exits the app so the installer can replace its files.</param>
    public UpdateService(AppSettings settings, SettingsStore store, ActivityManager activities, Func<bool> isBusy, Action shutdown)
    {
        _settings = settings;
        _store = store;
        _activities = activities;
        _isBusy = isBusy;
        _shutdown = shutdown;
        _updater = new Updater(_http);

        _timer.Tick += (_, _) => _ = RunAsync();
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    public static Version CurrentVersion =>
        ReleaseFeed.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Only a copy that the installer put in place can be updated by the installer. Development
    /// builds and copies run from elsewhere are left alone.
    /// </summary>
    public static bool IsInstalledCopy
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Notch");
            return key?.GetValue("InstallDir") is string installDir
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)),
                    Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Shows a short confirmation in the pill after the installer restarted the app.</summary>
    public static void AnnounceUpdated(ActivityManager activities) => activities.Publish(new Activity
    {
        Id = ActivityId,
        Tier = ActivityTier.Transient,
        Title = "Notch updated",
        Detail = CurrentVersion.ToString(3),
        Glyph = UpdateGlyph,
        Glow = new Glow(GlowColor.Green, GlowPattern.Flash),
        Lifetime = TimeSpan.FromSeconds(6),
    });

    /// <summary>Brings the next check forward, e.g. right after automatic updates were switched on.</summary>
    public void CheckSoon()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Start();
    }

    /// <summary>Deletes installers left behind by earlier updates.</summary>
    public void CleanUpDownloads()
    {
        try
        {
            if (Directory.Exists(_downloadFolder))
            {
                Directory.Delete(_downloadFolder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }

    private async Task RunAsync()
    {
        if (_working)
        {
            return;
        }

        _working = true;
        try
        {
            _timer.Interval = CheckInterval;
            if (!_settings.AutoUpdate || !IsInstalledCopy)
            {
                _pendingInstaller = null;
                return;
            }

            if (_pendingInstaller is null)
            {
                ReleaseInfo? release = await _updater.CheckAsync(CurrentVersion);
                if (release is null || WasAttemptedRecently(release))
                {
                    return;
                }

                _pendingInstaller = await _updater.DownloadAsync(release, _downloadFolder);
                _pendingRelease = release;
            }

            // The download is ready; wait for a moment when restarting interrupts nothing.
            if (_isBusy())
            {
                _timer.Interval = IdleRetryInterval;
                return;
            }

            Install(_pendingRelease!, _pendingInstaller);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Offline, rate limited, or a bad download: forget it and try again at the next check.
            _pendingInstaller = null;
        }
        finally
        {
            _working = false;
        }
    }

    private bool WasAttemptedRecently(ReleaseInfo release) =>
        _settings.LastUpdateAttemptTag == release.Tag
        && _settings.LastUpdateAttemptAt is { } at
        && DateTimeOffset.UtcNow - at < RetrySameReleaseAfter;

    private void Install(ReleaseInfo release, string installer)
    {
        // Recorded first: if this release's installer fails, the app must not retry it on every start.
        _settings.LastUpdateAttemptTag = release.Tag;
        _settings.LastUpdateAttemptAt = DateTimeOffset.UtcNow;
        _store.Save(_settings);

        try
        {
            // /S installs silently; /UPDATE keeps the start-with-Windows choice and restarts the app afterwards.
            Process.Start(new ProcessStartInfo(installer, "/S /UPDATE") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _pendingInstaller = null;
            return;
        }

        _timer.Stop();
        _shutdown();
    }
}
