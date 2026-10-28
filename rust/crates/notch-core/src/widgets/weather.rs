//! The weather widget's data from the free Open-Meteo service. Ported from `WeatherPlace`,
//! `WeatherReport`, `WeatherHour`, `WeatherDay`, `WeatherForecast`, `OpenMeteo` and `WeatherService`
//! in `src/Notch.Core/Widgets/HomeWidgetData.cs`.

use std::collections::HashMap;

use chrono::{NaiveDate, NaiveDateTime, NaiveTime, Timelike};
use serde_json::Value;

use crate::activities::glow::round;

#[derive(Debug, Clone, PartialEq)]
pub struct WeatherPlace {
    pub name: String,
    pub latitude: f64,
    pub longitude: f64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WeatherReport {
    pub temperature: f64,
    pub high: f64,
    pub low: f64,
    /// The WMO weather code Open-Meteo reports; see [`describe`].
    pub code: i32,
    pub wind_speed: f64,
    pub is_day: bool,
}

/// One hour ahead, in the place's own time.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WeatherHour {
    pub time: NaiveDateTime,
    pub temperature: f64,
    pub code: i32,
    /// Percent; `None` when Open-Meteo has none for that hour.
    pub rain_chance: Option<i32>,
    pub is_day: bool,
}

/// One day ahead, in the place's own time.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WeatherDay {
    pub date: NaiveDate,
    pub high: f64,
    pub low: f64,
    pub code: i32,
    pub rain_chance: Option<i32>,
    pub sunrise: Option<NaiveDateTime>,
    pub sunset: Option<NaiveDateTime>,
    pub uv_index: Option<f64>,
}

/// The weather now in detail, the next hours and the next days.
#[derive(Debug, Clone, PartialEq)]
pub struct WeatherForecast {
    pub now: WeatherReport,
    pub feels_like: f64,
    pub humidity: i32,
    /// Degrees the wind comes from, 0 for north; see [`compass`].
    pub wind_direction: f64,
    pub hours: Vec<WeatherHour>,
    pub days: Vec<WeatherDay>,
}

const UNEXPECTED: &str = "Open-Meteo sent something unexpected.";

/// How many hours ahead [`read_forecast`] keeps.
pub const FORECAST_HOURS: usize = 12;

pub fn geocode_url(city: &str) -> String {
    format!(
        "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={}",
        escape_data_string(city.trim())
    )
}

pub fn forecast_url(place: &WeatherPlace, fahrenheit: bool) -> String {
    format!(
        "https://api.open-meteo.com/v1/forecast?latitude={}&longitude={}",
        four_places(place.latitude),
        four_places(place.longitude)
    ) + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,is_day"
        + "&hourly=temperature_2m,weather_code,precipitation_probability,is_day"
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max"
        + "&forecast_days=7&timezone=auto"
        + if fahrenheit { "&temperature_unit=fahrenheit&wind_speed_unit=mph" } else { "" }
}

/// The first place the geocoder found, or `None` when it found none.
///
/// Errors when the answer is not what Open-Meteo sends.
pub fn read_place(json: &str) -> Result<Option<WeatherPlace>, String> {
    let root = parse(json)?;
    let Some(results) = object(&root)?.get("results") else { return Ok(None) };
    let Some(first) = array(results)?.first() else { return Ok(None) };

    let first = object(first)?;
    let mut name = string(get(first, "name")?)?.unwrap_or_default().to_owned();
    if let Some(code) = first.get("country_code").map(string).transpose()?.flatten().filter(|c| !c.is_empty()) {
        name = format!("{name}, {code}");
    }

    Ok(Some(WeatherPlace {
        name,
        latitude: double(get(first, "latitude")?)?,
        longitude: double(get(first, "longitude")?)?,
    }))
}

/// Errors when the answer is not what Open-Meteo sends.
pub fn read_report(json: &str) -> Result<WeatherReport, String> {
    report(&parse(json)?)
}

fn report(root: &Value) -> Result<WeatherReport, String> {
    let root = object(root)?;
    let current = object(get(root, "current")?)?;
    let daily = object(get(root, "daily")?)?;
    let first = |name| array(get(daily, name)?)?.first().ok_or_else(|| UNEXPECTED.to_owned()).and_then(double);
    Ok(WeatherReport {
        temperature: double(get(current, "temperature_2m")?)?,
        high: first("temperature_2m_max")?,
        low: first("temperature_2m_min")?,
        code: int(get(current, "weather_code")?)?,
        wind_speed: double(get(current, "wind_speed_10m")?)?,
        is_day: current.get("is_day").map(int).transpose()?.is_none_or(|day| day != 0),
    })
}

/// Everything the forecast URL asks for. Times are the place's own, as Open-Meteo gives them with
/// `timezone=auto`. The hours start with the current one.
///
/// Errors when the answer is not what Open-Meteo sends.
pub fn read_forecast(json: &str) -> Result<WeatherForecast, String> {
    let root = parse(json)?;
    let now = report(&root)?;
    let root = object(&root)?;
    let current = object(get(root, "current")?)?;
    let current_time = time(get(current, "time")?).unwrap_or(NaiveDateTime::MIN);
    let this_hour =
        current_time.date().and_time(NaiveTime::from_hms_opt(current_time.hour(), 0, 0).unwrap_or_default());

    let mut hours = Vec::new();
    if let Some(hourly) = root.get("hourly") {
        let hourly = object(hourly)?;
        let times = array(get(hourly, "time")?)?;
        for (i, value) in times.iter().enumerate() {
            if hours.len() >= FORECAST_HOURS {
                break;
            }

            if let Some(time) = time(value).filter(|t| *t >= this_hour) {
                hours.push(WeatherHour {
                    time,
                    temperature: number(hourly, "temperature_2m", Some(i)).unwrap_or(0.0),
                    code: number(hourly, "weather_code", Some(i)).unwrap_or(0.0) as i32,
                    rain_chance: number(hourly, "precipitation_probability", Some(i)).map(|r| r as i32),
                    // A missing value is not 0, so it counts as day, as in C#.
                    is_day: number(hourly, "is_day", Some(i)) != Some(0.0),
                });
            }
        }
    }

    let mut days = Vec::new();
    let daily = object(get(root, "daily")?)?;
    for (i, value) in array(get(daily, "time")?)?.iter().enumerate() {
        let Some(date) = string(value)?.and_then(|d| NaiveDate::parse_from_str(d.trim(), "%Y-%m-%d").ok()) else {
            continue;
        };

        days.push(WeatherDay {
            date,
            high: number(daily, "temperature_2m_max", Some(i)).unwrap_or(0.0),
            low: number(daily, "temperature_2m_min", Some(i)).unwrap_or(0.0),
            code: number(daily, "weather_code", Some(i)).unwrap_or(0.0) as i32,
            rain_chance: number(daily, "precipitation_probability_max", Some(i)).map(|r| r as i32),
            sunrise: item(daily, "sunrise", i).and_then(time),
            sunset: item(daily, "sunset", i).and_then(time),
            uv_index: number(daily, "uv_index_max", Some(i)),
        });
    }

    Ok(WeatherForecast {
        now,
        feels_like: number(current, "apparent_temperature", None).unwrap_or(now.temperature),
        humidity: number(current, "relative_humidity_2m", None).unwrap_or(0.0) as i32,
        wind_direction: number(current, "wind_direction_10m", None).unwrap_or(0.0),
        hours,
        days,
    })
}

/// Where the wind comes from as one of the eight compass points, "N" to "NW".
pub fn compass(degrees: f64) -> &'static str {
    const POINTS: [&str; 8] = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    let index = round(((degrees % 360.0) + 360.0) % 360.0 / 45.0) as usize % 8;
    POINTS[index]
}

/// A WMO weather code in words.
pub fn describe(code: i32) -> &'static str {
    match code {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 | 48 => "Fog",
        51 | 53 | 55 => "Drizzle",
        56 | 57 => "Freezing drizzle",
        61 | 63 => "Rain",
        65 => "Heavy rain",
        66 | 67 => "Freezing rain",
        71 | 73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 | 81 => "Showers",
        82 => "Heavy showers",
        85 | 86 => "Snow showers",
        95 => "Thunderstorm",
        96 | 99 => "Thunderstorm with hail",
        _ => "Unknown",
    }
}

/// Clear skies get the sun; everything else the cloud.
pub fn is_clear(code: i32) -> bool {
    matches!(code, 0 | 1)
}

/// Remembers where each city is so it is only looked up once.
///
/// C#'s `WeatherService` fetches as well; here the caller does, which keeps HTTP out of
/// notch-core. To get the weather for a city: if [`Self::place`] has no place for it, fetch
/// [`geocode_url`] and hand the answer to [`Self::read_place`]; then fetch [`forecast_url`] for the
/// place and read it with [`read_forecast`]. A failed fetch means Open-Meteo could not be reached.
#[derive(Debug, Clone, Default)]
pub struct WeatherService {
    /// By city, lowercased: C# compares them ignoring case.
    places: HashMap<String, WeatherPlace>,
}

impl WeatherService {
    pub fn new() -> Self {
        Self::default()
    }

    /// The place found for the city before, if it has been looked up.
    pub fn place(&self, city: &str) -> Option<&WeatherPlace> {
        self.places.get(&city.trim().to_lowercase())
    }

    /// Reads what [`geocode_url`] answered for the city and remembers the place.
    ///
    /// Errors when there is no such city, or the answer could not be read.
    pub fn read_place(&mut self, city: &str, json: &str) -> Result<WeatherPlace, String> {
        let city = city.trim();
        let place = read_place(json)?.ok_or_else(|| format!("No place called \"{city}\" was found."))?;
        self.places.insert(city.to_lowercase(), place.clone());
        Ok(place)
    }
}

// Reading the JSON as C#'s JsonElement does: a missing property, an index past the end or a
// value of the wrong kind is "something unexpected".

type Object = serde_json::Map<String, Value>;

fn parse(json: &str) -> Result<Value, String> {
    serde_json::from_str(json).map_err(|_| UNEXPECTED.to_owned())
}

fn object(value: &Value) -> Result<&Object, String> {
    value.as_object().ok_or_else(|| UNEXPECTED.to_owned())
}

fn array(value: &Value) -> Result<&Vec<Value>, String> {
    value.as_array().ok_or_else(|| UNEXPECTED.to_owned())
}

fn get<'a>(object: &'a Object, name: &str) -> Result<&'a Value, String> {
    object.get(name).ok_or_else(|| UNEXPECTED.to_owned())
}

/// `GetString`: a string, or `None` for null.
fn string(value: &Value) -> Result<Option<&str>, String> {
    match value {
        Value::Null => Ok(None),
        Value::String(s) => Ok(Some(s)),
        _ => Err(UNEXPECTED.to_owned()),
    }
}

fn double(value: &Value) -> Result<f64, String> {
    value.as_f64().ok_or_else(|| UNEXPECTED.to_owned())
}

/// `GetInt32`: a whole number written without a fraction or exponent, in 32 bits.
fn int(value: &Value) -> Result<i32, String> {
    crate::json::int(value).map_err(|_| UNEXPECTED.to_owned())
}

fn item<'a>(parent: &'a Object, name: &str, index: usize) -> Option<&'a Value> {
    parent.get(name)?.as_array()?.get(index)
}

/// A value, or one item of an array of them; `None` when it is missing or null, as an hour's rain chance can be.
fn number(parent: &Object, name: &str, index: Option<usize>) -> Option<f64> {
    let value = match index {
        Some(i) => item(parent, name, i),
        None => parent.get(name),
    };
    value?.as_f64()
}

/// A time as Open-Meteo writes it, "2026-10-23T14:45". C# accepts anything `DateTime.TryParse`
/// does; this takes the ISO forms, with or without seconds, and a date alone as midnight.
fn time(value: &Value) -> Option<NaiveDateTime> {
    let text = value.as_str()?.trim();
    ["%Y-%m-%dT%H:%M", "%Y-%m-%dT%H:%M:%S%.f", "%Y-%m-%d %H:%M", "%Y-%m-%d %H:%M:%S%.f"]
        .iter()
        .find_map(|format| NaiveDateTime::parse_from_str(text, format).ok())
        .or_else(|| NaiveDate::parse_from_str(text, "%Y-%m-%d").ok().map(|d| d.and_time(NaiveTime::MIN)))
}

/// `Uri.EscapeDataString`: everything but letters, digits and `-._~` as UTF-8 `%XX`.
fn escape_data_string(text: &str) -> String {
    let mut escaped = String::with_capacity(text.len());
    for byte in text.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            escaped.push(byte as char);
        } else {
            escaped.push_str(&format!("%{byte:02X}"));
        }
    }
    escaped
}

/// The .NET format "0.####": at most four decimals, halves away from zero, no trailing zeros.
fn four_places(value: f64) -> String {
    super::stats::fixed(value, 4).trim_end_matches('0').trim_end_matches('.').to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(date: &str, hour: u32, minute: u32) -> NaiveDateTime {
        NaiveDate::parse_from_str(date, "%Y-%m-%d").unwrap().and_hms_opt(hour, minute, 0).unwrap()
    }

    #[test]
    fn open_meteo_answers_are_read() {
        let place = read_place(
            r#"{"results":[{"name":"London","latitude":51.5085,"longitude":-0.12574,"country_code":"GB"}]}"#,
        )
        .unwrap();
        assert_eq!(place, Some(WeatherPlace { name: "London, GB".into(), latitude: 51.5085, longitude: -0.12574 }));
        assert_eq!(read_place(r#"{"generationtime_ms":0.5}"#), Ok(None));

        let report = read_report(
            r#"{"current":{"temperature_2m":12.3,"weather_code":61,"wind_speed_10m":14.0,"is_day":0},
                "daily":{"temperature_2m_max":[15.1],"temperature_2m_min":[8.4]}}"#,
        )
        .unwrap();
        assert_eq!(
            report,
            WeatherReport { temperature: 12.3, high: 15.1, low: 8.4, code: 61, wind_speed: 14.0, is_day: false }
        );
        assert_eq!(describe(report.code), "Rain");
        assert_eq!(read_report("{}"), Err(UNEXPECTED.into()));

        let forecast = read_forecast(
            r#"{"current":{"time":"2026-10-23T14:45","temperature_2m":12.3,"apparent_temperature":10.1,"relative_humidity_2m":81,
                           "weather_code":2,"wind_speed_10m":14.0,"wind_direction_10m":225,"is_day":1},
                "hourly":{"time":["2026-10-23T13:00","2026-10-23T14:00","2026-10-23T15:00"],
                          "temperature_2m":[11.0,12.0,13.5],"weather_code":[1,2,61],"precipitation_probability":[0,null,70],"is_day":[1,1,1]},
                "daily":{"time":["2026-10-23","2026-10-24"],"weather_code":[61,0],"temperature_2m_max":[15.1,17.0],"temperature_2m_min":[8.4,9.0],
                         "precipitation_probability_max":[80,5],"sunrise":["2026-10-23T07:31","2026-10-24T07:33"],
                         "sunset":["2026-10-23T17:52","2026-10-24T17:50"],"uv_index_max":[1.5,2.25]}}"#,
        )
        .unwrap();
        assert_eq!((forecast.feels_like, forecast.humidity, compass(forecast.wind_direction)), (10.1, 81, "SW"));
        assert_eq!(
            forecast.hours.iter().map(|h| h.time).collect::<Vec<_>>(),
            [at("2026-10-23", 14, 0), at("2026-10-23", 15, 0)]
        );
        assert_eq!(forecast.hours.iter().map(|h| h.rain_chance).collect::<Vec<_>>(), [None, Some(70)]);
        assert_eq!(forecast.days.len(), 2);
        assert_eq!(
            forecast.days[0],
            WeatherDay {
                date: NaiveDate::from_ymd_opt(2026, 10, 23).unwrap(),
                high: 15.1,
                low: 8.4,
                code: 61,
                rain_chance: Some(80),
                sunrise: Some(at("2026-10-23", 7, 31)),
                sunset: Some(at("2026-10-23", 17, 52)),
                uv_index: Some(1.5),
            }
        );
        assert_eq!((compass(350.0), compass(-10.0), compass(95.0)), ("N", "N", "E"));

        let url = forecast_url(place.as_ref().unwrap(), true);
        assert!(url.contains("latitude=51.5085&longitude=-0.1257"), "{url}");
        assert!(url.contains("temperature_unit=fahrenheit"));
        assert!(geocode_url(" San Francisco ").contains("name=San%20Francisco"));
    }

    #[test]
    fn the_service_remembers_places_ignoring_case() {
        let mut service = WeatherService::new();
        assert_eq!(service.place("London"), None);
        let json = r#"{"results":[{"name":"London","latitude":51.5,"longitude":0.0}]}"#;
        assert_eq!(service.read_place(" London ", json).unwrap().name, "London");
        assert_eq!(service.place("LONDON").map(|p| p.name.as_str()), Some("London"));
        assert_eq!(service.read_place("Atlantis", "{}"), Err("No place called \"Atlantis\" was found.".into()));
    }

    #[test]
    fn values_of_the_wrong_kind_are_unexpected() {
        assert_eq!(read_place("[]"), Err(UNEXPECTED.into()));
        assert_eq!(read_place(r#"{"results":{}}"#), Err(UNEXPECTED.into()));
        let report = |code: &str| {
            read_report(&format!(
                r#"{{"current":{{"temperature_2m":1,"weather_code":{code},"wind_speed_10m":1}},"daily":{{"temperature_2m_max":[1],"temperature_2m_min":[1]}}}}"#
            ))
        };
        assert!(report("61").unwrap().is_day);
        assert_eq!(report("61.0"), Err(UNEXPECTED.into()));
        assert_eq!(four_places(-0.12575), "-0.1258");
        assert_eq!(four_places(2.0), "2");
    }
}
