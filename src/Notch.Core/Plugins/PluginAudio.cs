namespace Notch.Core.Plugins;

/// <summary>
/// How loud what the PC is playing is right now, in total and by pitch. Levels only: a plugin never
/// receives the sound itself. API version 7.
/// </summary>
/// <param name="Level">Overall loudness, 0..1.</param>
/// <param name="Bass">Loudness of the low end (below about 250 Hz), 0..1.</param>
/// <param name="Mid">Loudness of the middle, 0..1.</param>
/// <param name="Treble">Loudness of the high end (above about 4 kHz), 0..1.</param>
/// <param name="Bands">
/// <see cref="PluginAudio.BandCount"/> loudness values, 0..1, from the lowest pitch to the highest, spaced
/// the way hearing is (each covers the same musical interval). Already scaled to a useful range.
/// </param>
public sealed record AudioFrame(double Level, double Bass, double Mid, double Treble, IReadOnlyList<float> Bands)
{
    /// <summary>What a frame looks like when nothing is playing.</summary>
    public static AudioFrame Silent { get; } = new(0, 0, 0, 0, new float[PluginAudio.BandCount]);
}

/// <summary>Constants of <see cref="IPluginAudio"/>.</summary>
public static class PluginAudio
{
    /// <summary>The number of values in <see cref="AudioFrame.Bands"/>.</summary>
    public const int BandCount = 16;
}

/// <summary>
/// The loudness of the PC's sound output, for visualisers. Notch listens to the output only while at
/// least one running plugin has asked for a frame, and stops when the last one stops. API version 7.
/// </summary>
/// <remarks>Plugins that use this should list <c>"audio"</c> under <c>permissions</c> in their manifest.</remarks>
public interface IPluginAudio
{
    /// <summary>
    /// The newest frame; <see cref="AudioFrame.Silent"/> when nothing is playing or the output cannot
    /// be read. The first read starts the listening, so poll it on a timer (30 times a second suits a
    /// visualiser) rather than reading it once.
    /// </summary>
    AudioFrame Latest { get; }
}

/// <summary>
/// Shares one analysis of the output between all plugins that read it, and starts it only while
/// someone does. The app supplies <see cref="Starter"/>; <see cref="Latest"/> is what plugins see.
/// </summary>
public sealed class PluginAudioHub(TimeProvider? time = null)
{
    // A loopback capture delivers nothing while the output is silent, so a frame this old means silence.
    private static readonly TimeSpan Staleness = TimeSpan.FromMilliseconds(150);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private AudioFrame _latest = AudioFrame.Silent;
    private long _at;
    private int _users;
    private IDisposable? _running;

    /// <summary>
    /// Starts an analysis that calls the given function with each new frame, from any thread. Dispose
    /// the result to stop it. Null (the default) means this machine cannot be listened to.
    /// </summary>
    public Func<Action<AudioFrame>, IDisposable>? Starter { get; set; }

    public AudioFrame Latest
    {
        get
        {
            lock (_gate)
            {
                return _time.GetElapsedTime(_at) <= Staleness ? _latest : AudioFrame.Silent;
            }
        }
    }

    /// <summary>Called by the analysis with each new frame.</summary>
    public void Publish(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            _latest = frame;
            _at = _time.GetTimestamp();
        }
    }

    /// <summary>One more reader. The listening runs until every one has been disposed.</summary>
    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_users++ == 0 && Starter is { } start)
            {
                try
                {
                    _running = start(Publish);
                }
                catch (Exception)
                {
                    // No output device, or no permission: plugins see silence, which is true enough.
                    _running = null;
                }
            }
        }

        return new Reader(this);
    }

    private void Release()
    {
        IDisposable? stop = null;
        lock (_gate)
        {
            if (--_users == 0)
            {
                stop = _running;
                _running = null;
                _latest = AudioFrame.Silent;
            }
        }

        stop?.Dispose();
    }

    private sealed class Reader(PluginAudioHub hub) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                hub.Release();
            }
        }
    }
}
