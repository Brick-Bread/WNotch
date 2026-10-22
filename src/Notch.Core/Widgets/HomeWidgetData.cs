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

/// <summary>One hour ahead, in the place's own time.</summary>
/// <param name="RainChance">Percent; null when Open-Meteo has none for that hour.</param>
public sealed record WeatherHour(DateTime Time, double Temperature, int Code, int? RainChance, bool IsDay);

/// <summary>One day ahead, in the place's own time.</summary>
public sealed record WeatherDay(DateOnly Date, double High, double Low, int Code, int? RainChance, DateTime? Sunrise, DateTime? Sunset, double? UvIndex);

/// <summary>The weather now in detail, the next hours and the next days.</summary>
/// <param name="WindDirection">Degrees the wind comes from, 0 for north; see <see cref="OpenMeteo.Compass"/>.</param>
public sealed record WeatherForecast(
    WeatherReport Now,
    double FeelsLike,
    int Humidity,
    double WindDirection,
    IReadOnlyList<WeatherHour> Hours,
    IReadOnlyList<WeatherDay> Days);

/// <summary>The free Open-Meteo service, which needs no key: a city's place, and the weather there.</summary>
public static class OpenMeteo
{
    public static string GeocodeUrl(string city) =>
        $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={Uri.EscapeDataString(city.Trim())}";

    public static string ForecastUrl(WeatherPlace place, bool fahrenheit) =>
        string.Create(CultureInfo.InvariantCulture, $"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude:0.####}&longitude={place.Longitude:0.####}")
        + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,is_day"
        + "&hourly=temperature_2m,weather_code,precipitation_probability,is_day"
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max"
        + "&forecast_days=7&timezone=auto"
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

    /// <summary>How many hours ahead <see cref="ReadForecast"/> keeps.</summary>
    public const int ForecastHours = 12;

    /// <summary>
    /// Everything the forecast URL asks for. Times are the place's own, as Open-Meteo gives them with
    /// <c>timezone=auto</c>. The hours start with the current one.
    /// </summary>
    /// <exception cref="FormatException">The answer is not what Open-Meteo sends.</exception>
    public static WeatherForecast ReadForecast(string json)
    {
        WeatherReport now = ReadReport(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonElement current = root.GetProperty("current");
            DateTime currentTime = Time(current.GetProperty("time")) ?? DateTime.MinValue;
            DateTime thisHour = new(currentTime.Year, currentTime.Month, currentTime.Day, currentTime.Hour, 0, 0);

            var hours = new List<WeatherHour>();
            if (root.TryGetProperty("hourly", out JsonElement hourly))
            {
                JsonElement times = hourly.GetProperty("time");
                for (int i = 0; i < times.GetArrayLength() && hours.Count < ForecastHours; i++)
                {
                    if (Time(times[i]) is { } time && time >= thisHour)
                    {
                        hours.Add(new WeatherHour(
                            time,
                            Number(hourly, "temperature_2m", i) ?? 0,
                            (int)(Number(hourly, "weather_code", i) ?? 0),
                            (int?)Number(hourly, "precipitation_probability", i),
                            Number(hourly, "is_day", i) != 0));
                    }
                }
            }

            var days = new List<WeatherDay>();
            JsonElement daily = root.GetProperty("daily");
            JsonElement dates = daily.GetProperty("time");
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                if (!DateOnly.TryParse(dates[i].GetString(), CultureInfo.InvariantCulture, out DateOnly date))
                {
                    continue;
                }

                days.Add(new WeatherDay(
                    date,
                    Number(daily, "temperature_2m_max", i) ?? 0,
                    Number(daily, "temperature_2m_min", i) ?? 0,
                    (int)(Number(daily, "weather_code", i) ?? 0),
                    (int?)Number(daily, "precipitation_probability_max", i),
                    Item(daily, "sunrise", i) is { } sunrise ? Time(sunrise) : null,
                    Item(daily, "sunset", i) is { } sunset ? Time(sunset) : null,
                    Number(daily, "uv_index_max", i)));
            }

            return new WeatherForecast(
                now,
                Number(current, "apparent_temperature") ?? now.Temperature,
                (int)(Number(current, "relative_humidity_2m") ?? 0),
                Number(current, "wind_direction_10m") ?? 0,
                hours,
                days);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new FormatException("Open-Meteo sent something unexpected.", e);
        }

        static JsonElement? Item(JsonElement parent, string name, int index) =>
            parent.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength()
                ? array[index]
                : null;

        // A value, or one item of an array of them; null when it is missing or null, as an hour's rain chance can be.
        static double? Number(JsonElement parent, string name, int? index = null)
        {
            JsonElement? value = index is { } i ? Item(parent, name, i) : parent.TryGetProperty(name, out JsonElement single) ? single : null;
            return value is { ValueKind: JsonValueKind.Number } number ? number.GetDouble() : null;
        }

        static DateTime? Time(JsonElement value) =>
            value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)
                ? time
                : null;
    }

    /// <summary>Where the wind comes from as one of the eight compass points, "N" to "NW".</summary>
    public static string Compass(double degrees)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int index = (int)Math.Round((((degrees % 360) + 360) % 360) / 45) % 8;
        return points[index];
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
    public async Task<(WeatherPlace Place, WeatherForecast Forecast)> FetchAsync(string city, bool fahrenheit, CancellationToken cancellation = default)
    {
        city = city.Trim();
        if (!_places.TryGetValue(city, out WeatherPlace? place))
        {
            place = OpenMeteo.ReadPlace(await http.GetStringAsync(OpenMeteo.GeocodeUrl(city), cancellation))
                ?? throw new FormatException($"No place called \"{city}\" was found.");
            _places[city] = place;
        }

        WeatherForecast forecast = OpenMeteo.ReadForecast(await http.GetStringAsync(OpenMeteo.ForecastUrl(place, fahrenheit), cancellation));
        return (place, forecast);
    }
}
