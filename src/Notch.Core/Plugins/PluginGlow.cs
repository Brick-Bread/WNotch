using Notch.Core.Activities;

namespace Notch.Core.Plugins;

/// <summary>One lit stretch of the glow; see <see cref="GlowFrame.Segments"/>.</summary>
/// <param name="Color">The colour of the light.</param>
/// <param name="Intensity">Brightness, 0..1.</param>
public readonly record struct GlowSegment(GlowColor Color, double Intensity);

/// <summary>
/// One picture of the light around the pill. A plugin that draws the glow itself sends a new
/// frame whenever it changes, up to about 30 times a second. API version 7.
/// </summary>
public sealed record GlowFrame
{
    /// <summary>The colour of the whole glow. Ignored when <see cref="Segments"/> is set.</summary>
    public GlowColor Color { get; init; } = GlowColor.White;

    /// <summary>Brightness of the whole glow, 0..1. Ignored when <see cref="Segments"/> is set.</summary>
    public double Intensity { get; init; } = 1;

    /// <summary>
    /// How far the light spreads, as a multiple of the usual size: 1 is normal, 2 twice as far.
    /// Clamped to 0.25..3.
    /// </summary>
    public double Reach { get; init; } = 1;

    /// <summary>
    /// Colours and brightnesses laid out evenly from the left edge of the pill to the right, blended
    /// smoothly into each other: a spectrum, a travelling wave. At most <see cref="PluginGlowBoard.MaxSegments"/>
    /// are used. Null or empty means one uniform glow from <see cref="Color"/> and <see cref="Intensity"/>.
    /// </summary>
    public IReadOnlyList<GlowSegment>? Segments { get; init; }
}

/// <summary>
/// Lets a plugin draw the light around the pill itself, in place of the glow of the activity that is
/// showing. The user's brightness setting and "glow effects" switch still apply, and nothing is
/// drawn while the notch is expanded. API version 7.
/// </summary>
/// <remarks>
/// A frame stays for about a second, so a plugin that wants a steady glow resends it, and one that
/// stops or hangs does not leave the pill lit. When several plugins set frames, the most recent wins.
/// </remarks>
public interface IPluginGlow
{
    /// <summary>Shows <paramref name="frame"/> until the next one arrives, <see cref="Clear"/> is called or about a second passes.</summary>
    void Set(GlowFrame frame);

    /// <summary>Gives the glow back to the activities. Does nothing when this plugin has no frame showing.</summary>
    void Clear();
}

/// <summary>The frames plugins have set. The shell reads <see cref="Current"/> for every picture it draws.</summary>
public sealed class PluginGlowBoard(TimeProvider? time = null)
{
    /// <summary>How many segments of a frame are used; any more are dropped.</summary>
    public const int MaxSegments = 64;

    /// <summary>How long a frame stays after its plugin last set it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private long _sequence;

    /// <summary>Raised, on the thread that caused it, when a frame appears after there was none or the last one is removed. Not raised when a frame merely expires.</summary>
    public event EventHandler? ActiveChanged;

    /// <summary>The frame to draw, or null when no plugin has a current one.</summary>
    public GlowFrame? Current
    {
        get
        {
            long now = _time.GetTimestamp();
            lock (_gate)
            {
                Entry? newest = null;
                foreach (Entry entry in _entries.Values)
                {
                    if (_time.GetElapsedTime(entry.At, now) <= Lifetime && (newest is null || entry.Sequence > newest.Sequence))
                    {
                        newest = entry;
                    }
                }

                return newest?.Frame;
            }
        }
    }

    public void Set(string pluginId, GlowFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        GlowFrame clean = Sanitize(frame);
        bool appeared;
        lock (_gate)
        {
            appeared = _entries.Count == 0;
            _entries[pluginId] = new Entry(clean, _time.GetTimestamp(), ++_sequence);
        }

        if (appeared)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Remove(string pluginId)
    {
        bool emptied;
        lock (_gate)
        {
            emptied = _entries.Remove(pluginId) && _entries.Count == 0;
        }

        if (emptied)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A copy with every number in range, so the shell never has to check.</summary>
    private static GlowFrame Sanitize(GlowFrame frame) => new()
    {
        Color = frame.Color,
        Intensity = Unit(frame.Intensity),
        Reach = double.IsFinite(frame.Reach) ? Math.Clamp(frame.Reach, 0.25, 3) : 1,
        Segments = frame.Segments is { Count: > 0 } segments
            ? [.. segments.Take(MaxSegments).Select(s => s with { Intensity = Unit(s.Intensity) })]
            : null,
    };

    private static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private sealed record Entry(GlowFrame Frame, long At, long Sequence);
}
