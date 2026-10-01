using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Notch.Core.Widgets;

/// <summary>Reads timer lengths the way people type them: "12", "1:30", "90s", "1h20m".</summary>
public static partial class DurationParser
{
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    /// <summary>
    /// A bare number is minutes ("12", "1.5"); colons read as a clock ("1:30" is a minute and a
    /// half, "1:02:03" has hours); otherwise numbers carry units ("90s", "45 min", "1h 20m").
    /// False for anything else and for lengths outside <see cref="Shortest"/>..<see cref="Longest"/>.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        double seconds;
        if (TryNumber(text, out double minutes))
        {
            seconds = minutes * 60;
        }
        else if (text.Contains(':'))
        {
            if (!TryClock(text, out seconds))
            {
                return false;
            }
        }
        else if (!TryUnits(text, out seconds))
        {
            return false;
        }

        if (double.IsNaN(seconds) || seconds < Shortest.TotalSeconds || seconds > Longest.TotalSeconds)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(Math.Round(seconds));
        return true;
    }

    /// <summary>The shortest spelling <see cref="TryParse"/> reads back: "5m", "1h30m", "1m30s", "45s".</summary>
    public static string Describe(TimeSpan duration)
    {
        long total = Math.Max(0, (long)Math.Round(duration.TotalSeconds));
        long hours = total / 3600, minutes = total / 60 % 60, seconds = total % 60;

        var text = new StringBuilder();
        if (hours > 0)
        {
            text.Append(hours).Append('h');
        }

        if (minutes > 0)
        {
            text.Append(minutes).Append('m');
        }

        if (seconds > 0 || text.Length == 0)
        {
            text.Append(seconds).Append('s');
        }

        return text.ToString();
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static bool TryClock(string text, out double seconds)
    {
        seconds = 0;
        string[] parts = text.Split(':');
        if (parts.Length is not (2 or 3))
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }

            seconds = (seconds * 60) + value;
        }

        return true;
    }

    private static bool TryUnits(string text, out double seconds)
    {
        seconds = 0;
        Match match = UnitsPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        CaptureCollection numbers = match.Groups["number"].Captures;
        CaptureCollection units = match.Groups["unit"].Captures;
        for (int i = 0; i < numbers.Count; i++)
        {
            double value = double.Parse(numbers[i].Value, CultureInfo.InvariantCulture);
            seconds += value * char.ToLowerInvariant(units[i].Value[0]) switch
            {
                'h' => 3600,
                'm' => 60,
                _ => 1,
            };
        }

        return true;
    }

    [GeneratedRegex(
        @"^(?:\s*(?<number>\d+(?:\.\d+)?)\s*(?<unit>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s))+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitsPattern();
}
