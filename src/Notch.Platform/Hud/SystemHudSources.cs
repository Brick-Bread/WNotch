using Notch.Core.Activities;

namespace Notch.Platform.Hud;

/// <summary>Owns the watchers that turn system events (volume, brightness, power, Bluetooth) into HUDs.</summary>
public sealed class SystemHudSources : IDisposable
{
    private readonly List<IDisposable> _sources = [];

    private SystemHudSources()
    {
    }

    /// <summary>
    /// Starts every watcher the machine supports. Call from a thread-pool thread: the audio
    /// COM objects must not be created on the UI thread, where their callbacks could not be delivered.
    /// </summary>
    public static SystemHudSources Start(ActivityManager activities)
    {
        var sources = new SystemHudSources();
        sources.TryAdd(() => new VolumeWatcher(activities));
        sources.TryAdd(() => new BrightnessWatcher(activities));
        sources.TryAdd(() => new PowerWatcher(activities));
        sources.TryAdd(() => new BluetoothWatcher(activities));
        return sources;
    }

    public void Dispose()
    {
        foreach (IDisposable source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _sources.Clear();
    }

    private void TryAdd(Func<IDisposable> create)
    {
        try
        {
            _sources.Add(create());
        }
        catch (Exception)
        {
            // Not every machine has every device: desktops lack a battery and a dimmable
            // panel, some have no Bluetooth radio or audio output. Skip what is missing.
        }
    }
}
