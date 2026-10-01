using NAudio.CoreAudioApi;
using Notch.Core.Activities;
using Notch.Core.Hud;

namespace Notch.Platform.Hud;

/// <summary>Shows a HUD when the default output's volume changes or a different output becomes the default.</summary>
internal sealed class VolumeWatcher : IDisposable
{
    private readonly ActivityManager _activities;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly Lock _gate = new();
    private MMDevice? _device;
    private bool _disposed;

    public VolumeWatcher(ActivityManager activities)
    {
        _activities = activities;
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
        Attach(announce: false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Detach();
        }

        _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
        {
            // Raised on the audio worker thread, which must not call back into the audio APIs.
            Task.Run(() => Attach(announce: true));
        }
    }

    private void Attach(bool announce)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Detach();
            try
            {
                if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice device))
                {
                    return;
                }

                _device = device;
                device.AudioEndpointVolume.OnVolumeNotification += OnVolumeChanged;

                if (announce)
                {
                    _activities.Publish(HudActivities.AudioOutput(device.FriendlyName));
                }
            }
            catch (Exception)
            {
                // The device went away between the notification and the query.
                Detach();
            }
        }
    }

    private void Detach()
    {
        if (_device is { } device)
        {
            try
            {
                device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeChanged;
                device.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _device = null;
    }

    private void OnVolumeChanged(AudioVolumeNotificationData data) =>
        _activities.Publish(HudActivities.Volume(data.MasterVolume, data.Muted));
}
