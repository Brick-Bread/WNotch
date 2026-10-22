using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notch.Core.Widgets;

/// <summary>A button on a launcher or commands widget.</summary>
/// <param name="Target">What a launcher opens (a program, file, folder or link), or the command a commands widget runs.</param>
public sealed record WidgetButton(string Label, string Target);

/// <summary>Reads the buttons of a launcher or commands widget, one per line as <c>Name = target</c>.</summary>
public static class WidgetButtons
{
    public const int MaxButtons = 8;

    /// <summary>
    /// Lines without a name are given one from the target: a file's or folder's name, or a link's host.
    /// Empty lines and lines starting with # are skipped.
    /// </summary>
    public static IReadOnlyList<WidgetButton> Parse(string? text)
    {
        var buttons = new List<WidgetButton>();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('='))
            {
                continue;
            }

            // A target such as https://x/?a=b has an = of its own; only one before it names the button.
            int equals = line.IndexOf('=');
            int scheme = line.IndexOf("://", StringComparison.Ordinal);
            string label;
            string target;
            if (equals > 0 && (scheme < 0 || equals < scheme))
            {
                label = line[..equals].Trim();
                target = line[(equals + 1)..].Trim();
            }
            else
            {
                label = "";
                target = line;
            }

            if (target.Length == 0)
            {
                continue;
            }

            buttons.Add(new WidgetButton(label.Length > 0 ? label : NameFor(target), target));
            if (buttons.Count == MaxButtons)
            {
                break;
            }
        }

        return buttons;
    }

    private static string NameFor(string target)
    {
        string unquoted = target.Trim('"');
        if (Uri.TryCreate(unquoted, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            return uri.Host.Length > 0 ? uri.Host : uri.Scheme;
        }

        string trimmed = unquoted.TrimEnd('\\', '/');
        string name = Path.GetFileNameWithoutExtension(trimmed);
        return name.Length > 0 ? name : trimmed;
    }
}

/// <summary>Turns what a web feed widget fetched into the text it shows.</summary>
public static partial class FeedText
{
    public const int MaxLength = 600;

    /// <summary>
    /// With a <paramref name="field"/>, the body is read as JSON and the field is followed by its
    /// dotted path (numbers index arrays: <c>items.0.title</c>). Without one, HTML loses its tags and
    /// anything else is shown as it is. Either way the result is cut to <see cref="MaxLength"/>.
    /// </summary>
    /// <exception cref="FormatException">The body is not JSON, or has no such field.</exception>
    public static string Extract(string body, string? field)
    {
        string text;
        if (!string.IsNullOrWhiteSpace(field))
        {
            text = ReadField(body, field.Trim());
        }
        else if (body.TrimStart().StartsWith('<'))
        {
            string withoutScripts = ScriptOrStyle().Replace(body, " ");
            text = WebUtility.HtmlDecode(Tag().Replace(withoutScripts, " "));
            text = Spaces().Replace(text, " ");
        }
        else
        {
            text = body;
        }

        text = text.Trim();
        return text.Length > MaxLength ? text[..MaxLength].TrimEnd() + "…" : text;
    }

    private static string ReadField(string body, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new FormatException("The feed did not return JSON.", e);
        }

        using (document)
        {
            JsonElement element = document.RootElement;
            foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (element.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index < element.GetArrayLength())
                {
                    element = element[index];
                }
                else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out JsonElement child))
                {
                    element = child;
                }
                else
                {
                    throw new FormatException($"The feed has no \"{path}\".");
                }
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Null => "",
                JsonValueKind.Array => string.Join(", ", element.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                _ => element.GetRawText(),
            };
        }
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Finds the time zone a clock widget was given, by id or by a city or name in its description.</summary>
public static class ClockZones
{
    /// <summary>Null for empty text, which means this computer's zone, and for text no zone matches.</summary>
    public static TimeZoneInfo? Find(string? text, IEnumerable<TimeZoneInfo>? zones = null)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Equals("UTC", StringComparison.OrdinalIgnoreCase) || text.Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        IReadOnlyList<TimeZoneInfo> all = [.. zones ?? TimeZoneInfo.GetSystemTimeZones()];
        return all.FirstOrDefault(z => z.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(z => z.StandardName.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(z => z.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(z => z.Id.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record WeatherPlace(string Name, double Latitude, double Longitude);

/// <param name="Code">The WMO weather code Open-Meteo reports; see <see cref="OpenMeteo.Describe"/>.</param>
public sealed record WeatherReport(double Temperature, double High, double Low, int Code, double WindSpeed, bool IsDay);

/// <summary>The free Open-Meteo service, which needs no key: a city's place, and the weather there.</summary>
public static class OpenMeteo
{
    public static string GeocodeUrl(string city) =>
        $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={Uri.EscapeDataString(city.Trim())}";

    public static string ForecastUrl(WeatherPlace place, bool fahrenheit) =>
        string.Create(CultureInfo.InvariantCulture, $"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude:0.####}&longitude={place.Longitude:0.####}")
        + "&current=temperature_2m,weather_code,wind_speed_10m,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto"
        + (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "");

    /// <summary>The first place the geocoder found, or null when it found none.</summary>
    /// <exception cref="FormatException">The answer is not what Open-Meteo sends.</exception>
    public static WeatherPlace? ReadPlace(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = results[0];
            string name = first.GetProperty("name").GetString() ?? "";
            if (first.TryGetProperty("country_code", out JsonElement country) && country.GetString() is { Length: > 0 } code)
            {
                name = $"{name}, {code}";
            }

            return new WeatherPlace(name, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Open-Meteo sent something unexpected.", e);
        }
    }

    /// <exception cref="FormatException">The answer is not what Open-Meteo sends.</exception>
    public static WeatherReport ReadReport(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement current = document.RootElement.GetProperty("current");
            JsonElement daily = document.RootElement.GetProperty("daily");
            return new WeatherReport(
                current.GetProperty("temperature_2m").GetDouble(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.GetProperty("wind_speed_10m").GetDouble(),
                !current.TryGetProperty("is_day", out JsonElement day) || day.GetInt32() != 0);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new FormatException("Open-Meteo sent something unexpected.", e);
        }
    }

    /// <summary>A WMO weather code in words.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 or 73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unknown",
    };

    /// <summary>Clear skies get the sun; everything else the cloud.</summary>
    public static bool IsClear(int code) => code is 0 or 1;
}

/// <summary>Fetches the weather for a city, remembering where each city is so it is only looked up once.</summary>
public sealed class WeatherService(HttpClient http)
{
    private readonly Dictionary<string, WeatherPlace> _places = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="FormatException">No such city, or an answer that could not be read.</exception>
    /// <exception cref="HttpRequestException">Open-Meteo could not be reached.</exception>
    public async Task<(WeatherPlace Place, WeatherReport Report)> FetchAsync(string city, bool fahrenheit, CancellationToken cancellation = default)
    {
        city = city.Trim();
        if (!_places.TryGetValue(city, out WeatherPlace? place))
        {
            place = OpenMeteo.ReadPlace(await http.GetStringAsync(OpenMeteo.GeocodeUrl(city), cancellation))
                ?? throw new FormatException($"No place called \"{city}\" was found.");
            _places[city] = place;
        }

        WeatherReport report = OpenMeteo.ReadReport(await http.GetStringAsync(OpenMeteo.ForecastUrl(place, fahrenheit), cancellation));
        return (place, report);
    }
}
