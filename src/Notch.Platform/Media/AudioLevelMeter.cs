using NAudio.CoreAudioApi;

namespace Notch.Platform.Media;

/// <summary>
/// Reads how loud the default output is right now (the peak meter the volume mixer shows).
/// Cheap enough to call every frame; it does not capture any audio.
/// </summary>
public sealed class AudioLevelMeter : IDisposable
{
    // The default device can change at any time; re-resolving it occasionally is simpler than watching for it.
    private static readonly TimeSpan DeviceRefresh = TimeSpan.FromSeconds(5);

    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private DateTime _refreshAt;

    /// <summary>Peak level since the last call, 0..1. Zero when there is no output device.</summary>
    public float Read()
    {
        try
        {
            if (_device is null || DateTime.UtcNow >= _refreshAt)
            {
                _device?.Dispose();
                _device = _enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice device) ? device : null;
                _refreshAt = DateTime.UtcNow + DeviceRefresh;
            }

            return _device?.AudioMeterInformation.MasterPeakValue ?? 0;
        }
        catch (Exception)
        {
            // The device disappeared mid-read; try again on the next refresh.
            _device = null;
            return 0;
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
