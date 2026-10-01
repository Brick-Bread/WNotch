using System.Text.Json.Serialization;

namespace Notch.Core.Widgets;

/// <summary>A timer the user starts with one click from the timer card.</summary>
/// <param name="Name">Shown on the button and in the pill while it runs; empty to show the length instead.</param>
/// <param name="Seconds">How long it counts.</param>
public sealed record TimerPreset(string Name, int Seconds)
{
    public const int MaxNameLength = 10;

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(Seconds);

    /// <summary>What the button says: the name, or the length ("15m") when there is none.</summary>
    [JsonIgnore]
    public string Label => Name.Length > 0 ? Name : DurationParser.Describe(Duration);

    [JsonIgnore]
    public bool IsValid => Duration >= DurationParser.Shortest && Duration <= DurationParser.Longest;

    /// <summary>The preset as one line of text, which <see cref="TryParse"/> reads back: "Tea 3m" or "15m".</summary>
    public string ToLine() => Name.Length > 0 ? $"{Name} {DurationParser.Describe(Duration)}" : DurationParser.Describe(Duration);

    /// <summary>
    /// Reads a line such as "15m", "Tea 3m" or "Long walk 1h 20m": a length (see
    /// <see cref="DurationParser"/>), optionally with a name in front of it.
    /// </summary>
    public static bool TryParse(string? line, out TimerPreset preset)
    {
        line = line?.Trim() ?? "";
        if (DurationParser.TryParse(line, out TimeSpan whole))
        {
            preset = new TimerPreset("", (int)whole.TotalSeconds);
            return true;
        }

        // The name ends at the first space after which the rest reads as a length.
        for (int space = line.IndexOf(' '); space >= 0; space = line.IndexOf(' ', space + 1))
        {
            if (DurationParser.TryParse(line[space..], out TimeSpan duration))
            {
                string name = line[..space].Trim();
                preset = new TimerPreset(name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name, (int)duration.TotalSeconds);
                return true;
            }
        }

        preset = new TimerPreset("", 0);
        return false;
    }
}
