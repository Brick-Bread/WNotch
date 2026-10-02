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
/// Tells the user when a newer release exists: checks GitHub in the background and shows a notice
/// in the pill, once per release. Nothing is downloaded or installed until the user asks for it
/// with the "Update Notch now" button, which runs the installer; the installer restarts the app.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    /// <summary>Passed by the installer when it restarts the app after an update.</summary>
    public const string UpdatedFlag = "--updated";

    private const string ActivityId = "update";
    private const string UpdateGlyph = "";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ActivityManager _activities;
    private readonly Action _shutdown;
    private readonly Updater _updater;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _timer = new();
    private readonly string _downloadFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "updates");

    private ReleaseInfo? _pendingRelease;
    private string? _pendingInstaller;
    private bool _working;

    /// <param name="shutdown">Exits the app so the installer can replace its files.</param>
    public UpdateService(AppSettings settings, SettingsStore store, ActivityManager activities, Action shutdown)
    {
        _settings = settings;
        _store = store;
        _activities = activities;
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

    /// <summary>
    /// After a failed update the installer puts the previous version back on screen and leaves a note
    /// (<c>update-error.txt</c>) saying what went wrong. Shows it once and deletes it.
    /// </summary>
    public static void AnnounceFailedUpdateIfAny(ActivityManager activities)
    {
        string file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch", "update-error.txt");
        try
        {
            if (!File.Exists(file))
            {
                return;
            }

            string reason = File.ReadAllText(file).Trim();
            File.Delete(file);
            activities.Publish(new Activity
            {
                Id = ActivityId + ".failed",
                Tier = ActivityTier.Attention,
                Title = "The update did not install",
                Detail = reason.Length == 0 ? "Notch is unchanged. See setup.log in %LocalAppData%\\Notch" :"Notch is unchanged. " + reason,
                Glyph = UpdateGlyph,
                Glow = new Glow(GlowColor.Red, GlowPattern.Pulse),
            });

            // An Attention notice stays until removed; this one only needs to be seen.
            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => activities.Remove(ActivityId + ".failed"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Brings the next check forward, e.g. right after update notices were switched on.</summary>
    public void CheckSoon()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Start();
    }

    /// <summary>
    /// Updates now, for the "Update Notch now" button: the only way an update is installed. It
    /// ignores the update-notice setting. The installer restarts the app.
    /// </summary>
    /// <param name="confirmRestart">Asked once the installer is downloaded; false leaves it unused.</param>
    /// <returns>What to tell the user. When an update starts, the app is shutting down.</returns>
    public async Task<string> ForceUpdateAsync(Func<bool> confirmRestart)
    {
        if (!IsInstalledCopy)
        {
            return "This copy of Notch was not set up by its installer, so it cannot update itself.";
        }

        if (_working)
        {
            return "An update is already being downloaded. Try again in a moment.";
        }

        _working = true;
        try
        {
            ReleaseInfo? release = await _updater.CheckAsync(CurrentVersion);
            if (release is null)
            {
                return $"Notch {CurrentVersion.ToString(3)} is the latest version.";
            }

            string installer = _pendingInstaller is not null && _pendingRelease?.Tag == release.Tag
                ? _pendingInstaller
                : await _updater.DownloadAsync(release, _downloadFolder);
            _pendingInstaller = installer;
            _pendingRelease = release;

            if (!confirmRestart())
            {
                return $"Notch {release.Tag} is downloaded. Press the button again when you are ready to restart.";
            }

            return Install(release, installer)
                ? $"Installing Notch {release.Tag}…"
                : "The installer could not be started.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _pendingInstaller = null;
            return "Could not update: " + e.Message;
        }
        finally
        {
            _working = false;
        }
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
            if (!_settings.NotifyOfUpdates || !IsInstalledCopy)
            {
                return;
            }

            ReleaseInfo? release = await _updater.CheckAsync(CurrentVersion);
            if (release is null || _settings.LastNotifiedUpdateTag == release.Tag)
            {
                return;
            }

            // Recorded first: a notice is shown once per release, not on every start.
            _settings.LastNotifiedUpdateTag = release.Tag;
            _store.Save(_settings);
            AnnounceAvailable(release);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Offline or rate limited: try again at the next check.
        }
        finally
        {
            _working = false;
        }
    }

    private void AnnounceAvailable(ReleaseInfo release) => _activities.Publish(new Activity
    {
        Id = ActivityId,
        Tier = ActivityTier.Transient,
        Title = $"Notch {release.Tag} is ready",
        Detail = "Install it from Settings",
        Glyph = UpdateGlyph,
        Glow = new Glow(GlowColor.Blue, GlowPattern.Flash),
        Lifetime = TimeSpan.FromSeconds(10),
    });

    /// <returns>False when the installer could not be started; the app is then still running.</returns>
    private bool Install(ReleaseInfo release, string installer)
    {
        try
        {
            // /S installs silently; /UPDATE keeps the start-with-Windows choice and restarts the app afterwards.
            Process.Start(new ProcessStartInfo(installer, "/S /UPDATE") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _pendingInstaller = null;
            return false;
        }

        _timer.Stop();
        _shutdown();
        return true;
    }
}
