namespace Notch.Core.Activities;

public readonly record struct GlowColor(byte R, byte G, byte B)
{
    public static GlowColor White { get; } = new(0xF2, 0xF2, 0xF2);
    public static GlowColor Blue { get; } = new(0x3D, 0x8B, 0xFF);
    public static GlowColor Cyan { get; } = new(0x2E, 0xC4, 0xFF);
    public static GlowColor Green { get; } = new(0x3F, 0xD8, 0x6B);
    public static GlowColor Amber { get; } = new(0xFF, 0xB0, 0x1F);
    public static GlowColor Orange { get; } = new(0xFF, 0x7A, 0x2E);
    public static GlowColor Red { get; } = new(0xFF, 0x3B, 0x3B);
    public static GlowColor Yellow { get; } = new(0xFF, 0xD8, 0x4A);
    public static GlowColor Violet { get; } = new(0xA8, 0x6B, 0xFF);

    /// <summary>Linear blend: 0 gives this colour, 1 gives <paramref name="other"/>.</summary>
    public GlowColor Lerp(GlowColor other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new GlowColor(Mix(R, other.R), Mix(G, other.G), Mix(B, other.B));

        byte Mix(byte from, byte to) => (byte)Math.Round(from + ((to - from) * amount));
    }
}

public enum GlowPattern
{
    /// <summary>Constant light.</summary>
    Steady,

    /// <summary>Slow rise and fall: something is in progress.</summary>
    Breathe,

    /// <summary>Quick, strong beats: something wants the user.</summary>
    Pulse,

    /// <summary>A bright burst that settles to a low glow: a one-off event.</summary>
    Flash,

    /// <summary>Follows the loudness of what is playing.</summary>
    Audio,
}

/// <summary>The light around the notch while an activity is on top.</summary>
/// <param name="Strength">Overall brightness, 0..1.</param>
public sealed record Glow(GlowColor Color, GlowPattern Pattern, double Strength = 1)
{
    private const double BreathePeriod = 3.2;
    private const double PulsePeriod = 1.1;
    private const double FlashDecay = 1.2;

    /// <summary>Brightness (0..1) at <paramref name="seconds"/> since the glow started.</summary>
    /// <param name="audioLevel">Smoothed output loudness, 0..1; only used by <see cref="GlowPattern.Audio"/>.</param>
    public double IntensityAt(double seconds, double audioLevel = 0)
    {
        seconds = Math.Max(0, seconds);
        double intensity = Pattern switch
        {
            GlowPattern.Breathe => 0.45 + (0.55 * Wave(seconds, BreathePeriod)),
            GlowPattern.Pulse => 0.25 + (0.75 * Wave(seconds, PulsePeriod)),
            GlowPattern.Flash => 0.35 + (0.65 * Math.Max(0, 1 - (seconds / FlashDecay))),
            GlowPattern.Audio => 0.2 + (0.8 * Math.Clamp(audioLevel, 0, 1)),
            _ => 0.8,
        };

        return Math.Clamp(intensity * Strength, 0, 1);
    }

    /// <summary>True when the brightness changes over time and needs a per-frame update.</summary>
    public bool IsAnimated => Pattern != GlowPattern.Steady;

    // 0 at the start of each period, 1 half way through, smooth in between.
    private static double Wave(double seconds, double period)
    {
        double phase = Math.Sin(Math.PI * (seconds % period) / period);
        return phase * phase;
    }
}

/// <summary>Picks a glow colour that represents an image, such as album art.</summary>
public static class AccentColor
{
    /// <summary>Weighted average of the image's colourful pixels, brightened to read as light. Null for greyscale images.</summary>
    /// <param name="bgra">32-bit pixels in B, G, R, A order.</param>
    public static GlowColor? FromPixels(ReadOnlySpan<byte> bgra)
    {
        double r = 0, g = 0, b = 0, weight = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
            {
                continue;
            }

            double pb = bgra[i] / 255.0, pg = bgra[i + 1] / 255.0, pr = bgra[i + 2] / 255.0;
            double max = Math.Max(pr, Math.Max(pg, pb));
            double min = Math.Min(pr, Math.Min(pg, pb));
            double saturation = max <= 0 ? 0 : (max - min) / max;

            // Vivid, reasonably bright pixels describe an image better than its greys and shadows.
            double w = saturation * saturation * max;
            r += pr * w;
            g += pg * w;
            b += pb * w;
            weight += w;
        }

        if (weight < 1e-3)
        {
            return null;
        }

        r /= weight;
        g /= weight;
        b /= weight;

        // Scale up so the brightest channel is at full strength: a glow should never be murky.
        double peak = Math.Max(r, Math.Max(g, b));
        double scale = peak > 0 ? 1 / peak : 1;
        return new GlowColor(ToByte(r * scale), ToByte(g * scale), ToByte(b * scale));

        static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }
}
