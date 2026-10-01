using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Notch.Core.Activities;
using Notch.Platform.Media;

namespace Notch.App.Shell;

/// <summary>
/// Animates the light around the notch. Two layers sit exactly behind the island, so only
/// their blurred shadows show: a wide soft halo and a tight bright core. Runs per frame
/// only while lit.
/// </summary>
internal sealed class GlowController : IDisposable
{
    private const double HaloBlur = 18;
    private const double HaloExtraBlur = 14;
    private const double CoreBlur = 7;

    // A soft glow looks the same at 24 fps, and every frame redraws the whole transparent window.
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(40);

    private readonly Border _halo;
    private readonly Border _core;
    private readonly DropShadowEffect _haloShadow = CreateShadow(HaloBlur);
    private readonly DropShadowEffect _coreShadow = CreateShadow(CoreBlur);
    private readonly SolidColorBrush _rim = new(Colors.Transparent);
    private readonly Lazy<AudioLevelMeter> _meter = new(() => new AudioLevelMeter());

    private Glow? _glow;
    private GlowColor _targetColor;
    private GlowColor _color;
    private TimeSpan _startedAt;
    private TimeSpan? _lastFrame;
    private double _intensity;
    private double _audioLevel;
    private bool _running;

    public GlowController(Border halo, Border core)
    {
        _halo = halo;
        _core = core;
        _halo.Background = _rim;
        _core.Background = _rim;
        _halo.Effect = _haloShadow;
        _core.Effect = _coreShadow;
        SetVisible(false);
    }

    /// <summary>Lights the notch for <paramref name="glow"/>, or fades it out when null.</summary>
    /// <param name="color">Replaces the glow's own colour, e.g. with one taken from album art.</param>
    public void Show(Glow? glow, GlowColor? color = null)
    {
        if (glow is not null && color is { } custom)
        {
            glow = glow with { Color = custom };
        }

        if (glow == _glow)
        {
            return;
        }

        // A new pattern starts from its beginning; the colour blends over from the old one.
        bool wasDark = _glow is null && _intensity < 0.01;
        _glow = glow;
        _startedAt = CurrentTime();
        if (glow is not null)
        {
            _targetColor = glow.Color;
            if (wasDark)
            {
                _color = glow.Color;
            }
        }

        Start();
    }

    public void Dispose()
    {
        Stop();
        if (_meter.IsValueCreated)
        {
            _meter.Value.Dispose();
        }
    }

    private static DropShadowEffect CreateShadow(double blur) => new()
    {
        ShadowDepth = 0,
        BlurRadius = blur,
        Opacity = 0,
        RenderingBias = RenderingBias.Performance,
    };

    private static TimeSpan CurrentTime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void SetVisible(bool visible)
    {
        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _halo.Visibility = visibility;
        _core.Visibility = visibility;
    }

    private void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _lastFrame = null;
        SetVisible(true);
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        SetVisible(false);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = CurrentTime();
        if (_lastFrame is { } previous && now - previous < MinFrameInterval)
        {
            return;
        }

        double dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrame = now;

        double target = 0;
        if (_glow is { } glow)
        {
            if (glow.Pattern == GlowPattern.Audio)
            {
                // Square root lifts quiet passages; quick attack and slow release keeps it from flickering.
                double peak = Math.Sqrt(_meter.Value.Read());
                _audioLevel = peak > _audioLevel
                    ? _audioLevel + ((peak - _audioLevel) * 0.6)
                    : _audioLevel * Math.Pow(0.08, dt);
            }

            target = glow.IntensityAt((now - _startedAt).TotalSeconds, _audioLevel);
        }

        // Ease towards the target so pattern changes and fade-outs never jump.
        _intensity += (target - _intensity) * Math.Min(1, dt * 14);
        _color = _color.Lerp(_targetColor, Math.Min(1, dt * 6));

        var color = Color.FromRgb(_color.R, _color.G, _color.B);
        _haloShadow.Color = color;
        _haloShadow.Opacity = _intensity;
        _haloShadow.BlurRadius = HaloBlur + (HaloExtraBlur * _intensity);
        _coreShadow.Color = color;
        _coreShadow.Opacity = Math.Min(1, _intensity * 1.1);
        _rim.Color = Color.FromArgb((byte)(255 * _intensity), color.R, color.G, color.B);

        if (_glow is null && _intensity < 0.01)
        {
            _intensity = 0;
            Stop();
        }
    }
}
