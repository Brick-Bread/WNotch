using Notch.Core.Activities;
using Notch.Core.Settings;
using Notch.Core.Shell;
using Notch.Platform.Notifications;

namespace Notch.App.Notifications;

/// <summary>
/// Shows Windows notifications in the pill while the user has switched that on, for the apps
/// they listed (all of them when the list is empty). Off until asked for.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ActivityManager _activities;
    private readonly NotificationWatcher _watcher = new();

    public NotificationService(AppSettings settings, ActivityManager activities)
    {
        _settings = settings;
        _activities = activities;
        _watcher.Received += OnReceived;
    }

    /// <summary>Starts or stops watching to match the settings. Returns why it could not start, or null.</summary>
    public async Task<string?> ApplyAsync()
    {
        if (!_settings.MirrorNotifications)
        {
            _watcher.Stop();
            return null;
        }

        return _watcher.IsRunning ? null : await _watcher.StartAsync();
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        _watcher.Dispose();
    }

    private void OnReceived(NotificationInfo notification)
    {
        if (!_settings.MirrorNotifications || !NotificationMirror.Allows(notification.App, _settings.MirroredApps))
        {
            return;
        }

        if (NotificationMirror.ToActivity(notification) is { } activity)
        {
            _activities.Publish(activity);
        }
    }
}
