using NAudio.CoreAudioApi;
using NAudio.Wave;
using Notch.Core.Plugins;

namespace Notch.Platform.Media;

/// <summary>
/// Listens to what the default output is playing (WASAPI loopback) and reports how loud it is, in total
/// and in <see cref="PluginAudio.BandCount"/> bands of pitch. Only levels leave this class; the samples
/// are analysed in place and discarded. Create it to start, dispose it to stop.
/// </summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    private const int WindowSize = 2048;
    private const double LowestHz = 40;
    private const double HighestHz = 16000;

    // A new picture at most this often; the glow redraws at 25 frames a second.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(25);

    private readonly Action<AudioFrame> _publish;
    private readonly Lock _gate = new();
    private readonly float[] _ring = new float[WindowSize];
    private readonly double[] _re = new double[WindowSize];
    private readonly double[] _im = new double[WindowSize];
    private readonly double[] _window = new double[WindowSize];
    private readonly float[] _bands = new float[PluginAudio.BandCount];
    private readonly (int From, int To)[] _binRanges = new (int, int)[PluginAudio.BandCount];
    private readonly double[] _tilt = new double[PluginAudio.BandCount];
    private readonly Timer _restart;

    private WasapiRecorder? _capture;
    private int _written;
    private DateTime _lastFrame;
    private bool _disposed;

    public SpectrumAnalyzer(Action<AudioFrame> publish)
    {
        _publish = publish;
        for (int i = 0; i < WindowSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1)));
        }

        _restart = new Timer(_ => Begin(), null, Timeout.Infinite, Timeout.Infinite);
        Begin();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Stop();
        }

        _restart.Dispose();
    }

    private void Begin()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                WasapiRecorder capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
                PrepareBands(capture.WaveFormat.SampleRate);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                _capture = capture;
                capture.StartRecording();
            }
            catch (Exception)
            {
                // No output device right now (unplugged, being switched): look again in a moment.
                Stop();
                _restart.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Stop()
    {
        WasapiRecorder? capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            // Already stopped by the device going away.
        }

        capture.Dispose();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // The default device changed or went away; follow it.
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _restart.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private void PrepareBands(int sampleRate)
    {
        double binHz = (double)sampleRate / WindowSize;
        double top = Math.Min(HighestHz, sampleRate / 2.0 * 0.95);
        double ratio = Math.Pow(top / LowestHz, 1.0 / PluginAudio.BandCount);
        int previous = 0;
        for (int i = 0; i < PluginAudio.BandCount; i++)
        {
            double low = LowestHz * Math.Pow(ratio, i);
            double high = low * ratio;
            int from = Math.Max(previous, (int)Math.Round(low / binHz));
            int to = Math.Max(from, (int)Math.Round(high / binHz) - 1);
            _binRanges[i] = (from, to);
            previous = to + 1;

            // Music has far more energy low down; lifting the highs by 3 dB an octave evens the picture out.
            _tilt[i] = 3 * Math.Log2(Math.Sqrt(low * high) / 1000);
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_gate)
        {
            if (_disposed || _capture is null)
            {
                return;
            }

            WaveFormat format = _capture.WaveFormat;
            int channels = Math.Max(1, format.Channels);
            int bytesPerSample = format.BitsPerSample / 8;
            int frames = buffer.Length / (bytesPerSample * channels);
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += ReadSample(buffer, ((f * channels) + c) * bytesPerSample, format);
                }

                _ring[_written % WindowSize] = (float)(sum / channels);
                _written++;
            }

            DateTime now = DateTime.UtcNow;
            if (_written >= WindowSize && now - _lastFrame >= Interval)
            {
                _lastFrame = now;
                _publish(Analyse());
            }
        }
    }

    private static float ReadSample(ReadOnlySpan<byte> buffer, int offset, WaveFormat format) => format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
        ? BitConverter.ToSingle(buffer.Slice(offset, 4))
        : format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer.Slice(offset, 2)) / 32768f,
            24 => (((buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24)) >> 8) / 8388608f,
            32 => BitConverter.ToInt32(buffer.Slice(offset, 4)) / 2147483648f,
            _ => 0,
        };

    private AudioFrame Analyse()
    {
        double energy = 0;
        for (int i = 0; i < WindowSize; i++)
        {
            float sample = _ring[(_written + i) % WindowSize];
            energy += sample * (double)sample;
            _re[i] = sample * _window[i];
            _im[i] = 0;
        }

        Fft(_re, _im);

        // A full-scale sine comes out as 1: the Hann window halves it, and half of the spectrum is mirrored.
        double scale = 4.0 / WindowSize;
        for (int b = 0; b < PluginAudio.BandCount; b++)
        {
            (int from, int to) = _binRanges[b];
            double peak = 0;
            for (int k = from; k <= to && k < WindowSize / 2; k++)
            {
                peak = Math.Max(peak, Math.Sqrt((_re[k] * _re[k]) + (_im[k] * _im[k])) * scale);
            }

            double db = (20 * Math.Log10(peak + 1e-9)) + _tilt[b];
            _bands[b] = (float)Math.Clamp((db + 62) / 52, 0, 1);
        }

        double rmsDb = 20 * Math.Log10(Math.Sqrt(energy / WindowSize) + 1e-9);
        double level = Math.Clamp((rmsDb + 48) / 42, 0, 1);

        return new AudioFrame(
            level,
            Mean(0, 4),
            Mean(4, 11),
            Mean(11, PluginAudio.BandCount),
            [.. _bands]);

        double Mean(int from, int to)
        {
            double sum = 0;
            for (int i = from; i < to; i++)
            {
                sum += _bands[i];
            }

            return sum / (to - from);
        }
    }

    /// <summary>In-place radix-2 FFT; the length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double wr = Math.Cos(angle);
            double wi = Math.Sin(angle);
            for (int start = 0; start < n; start += length)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < length / 2; k++)
                {
                    int a = start + k;
                    int b = a + (length / 2);
                    double tr = (re[b] * cr) - (im[b] * ci);
                    double ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    (cr, ci) = ((cr * wr) - (ci * wi), (cr * wi) + (ci * wr));
                }
            }
        }
    }
}
