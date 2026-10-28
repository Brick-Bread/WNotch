//! Finds the time zone a clock widget was given. Ported from `ClockZones` in
//! `src/Notch.Core/Widgets/HomeWidgetData.cs`.
//!
//! C# searches the zones Windows has (`TimeZoneInfo.GetSystemTimeZones`). Here the same list comes
//! from [`super::windows_zones`]: each Windows id with the display name Windows shows for it, and
//! the IANA zone (from CLDR) whose rules `chrono-tz` applies. So "Tokyo" finds "Tokyo Standard
//! Time" through its display name "(UTC+09:00) Osaka, Sapporo, Tokyo", and "UTC+09" through the
//! same display name, as on Windows.

use std::sync::LazyLock;

use chrono_tz::Tz;

use super::windows_zones::WINDOWS_ZONES;

/// A time zone as Windows lists it, with the rules to tell its time by.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClockZone {
    /// The Windows id, such as "Tokyo Standard Time".
    pub id: String,
    /// What English Windows calls its standard time. For the built-in list this is the id, which it
    /// is on English Windows for all but a few zones, and "Coordinated Universal Time" for "UTC".
    pub standard_name: String,
    /// Such as "(UTC+09:00) Osaka, Sapporo, Tokyo".
    pub display_name: String,
    pub tz: Tz,
}

impl ClockZone {
    /// What C# returns as `TimeZoneInfo.Utc`.
    pub fn utc() -> Self {
        Self {
            id: "UTC".into(),
            standard_name: "Coordinated Universal Time".into(),
            display_name: "(UTC) Coordinated Universal Time".into(),
            tz: Tz::UTC,
        }
    }
}

static SYSTEM_ZONES: LazyLock<Vec<ClockZone>> = LazyLock::new(|| {
    WINDOWS_ZONES
        .iter()
        .filter_map(|&(id, iana, display_name)| {
            Some(ClockZone {
                id: id.to_owned(),
                standard_name: if id == "UTC" { "Coordinated Universal Time".into() } else { id.to_owned() },
                display_name: display_name.to_owned(),
                tz: iana.parse().ok()?,
            })
        })
        .collect()
});

/// The zones Windows has, in the order it lists them.
pub fn system_zones() -> &'static [ClockZone] {
    &SYSTEM_ZONES
}

/// The zone the text names: "UTC" or "GMT", then a zone whose id is the text, whose standard name
/// is the text, whose display name contains it, or whose id contains it, all ignoring case.
/// `None` for empty text, which means this computer's zone, and for text no zone matches.
/// `zones` defaults to [`system_zones`].
pub fn find(text: &str, zones: Option<&[ClockZone]>) -> Option<ClockZone> {
    let text = text.trim();
    if text.is_empty() {
        return None;
    }

    if text.eq_ignore_ascii_case("UTC") || text.eq_ignore_ascii_case("GMT") {
        return Some(ClockZone::utc());
    }

    let all = zones.unwrap_or(system_zones());
    let lower = text.to_lowercase();
    let contains = |haystack: &str| haystack.to_lowercase().contains(&lower);
    all.iter()
        .find(|z| z.id.to_lowercase() == lower)
        .or_else(|| all.iter().find(|z| z.standard_name.to_lowercase() == lower))
        .or_else(|| all.iter().find(|z| contains(&z.display_name)))
        .or_else(|| all.iter().find(|z| contains(&z.id)))
        .cloned()
}

/// The zone a Windows id stands for, such as "W. Europe Standard Time" for Europe/Berlin.
pub fn from_windows_id(id: &str) -> Option<Tz> {
    let id = super::windows_zones::RENAMED.iter().find(|(new, _)| *new == id).map_or(id, |(_, old)| old);
    WINDOWS_ZONES.iter().find(|(windows, _, _)| *windows == id).and_then(|(_, iana, _)| iana.parse().ok())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn zone(id: &str, display_name: &str, tz: Tz) -> ClockZone {
        ClockZone { id: id.into(), standard_name: id.into(), display_name: display_name.into(), tz }
    }

    #[test]
    fn clock_zones_are_found_by_id_or_city() {
        let tokyo = zone("Tokyo Standard Time", "(UTC+09:00) Osaka, Sapporo, Tokyo", Tz::Asia__Tokyo);
        let london = zone("GMT Standard Time", "(UTC+00:00) Dublin, Edinburgh, Lisbon, London", Tz::Europe__London);
        let zones = [tokyo.clone(), london.clone()];

        assert_eq!(find(" ", Some(&zones)), None);
        assert_eq!(find("utc", Some(&zones)), Some(ClockZone::utc()));
        assert_eq!(find("tokyo", Some(&zones)).map(|z| z.id), Some(tokyo.id));
        assert_eq!(find("GMT Standard Time", Some(&zones)).map(|z| z.id), Some(london.id));
        assert_eq!(find("Atlantis", Some(&zones)), None);
    }

    #[test]
    fn every_windows_zone_has_rules() {
        assert_eq!(system_zones().len(), WINDOWS_ZONES.len());
    }

    #[test]
    fn the_built_in_list_finds_cities_ids_and_offsets() {
        let id = |text| find(text, None).map(|z| z.id);
        assert_eq!(id("Tokyo").as_deref(), Some("Tokyo Standard Time"));
        assert_eq!(id("berlin").as_deref(), Some("W. Europe Standard Time"));
        assert_eq!(id("Pacific Standard Time").as_deref(), Some("Pacific Standard Time"));
        assert_eq!(id("UTC+09:30").as_deref(), Some("Cen. Australia Standard Time"));
        assert_eq!(find("New York", None), None);
        assert_eq!(find("Eastern Time", None).map(|z| z.tz), Some(Tz::America__New_York));
    }

    #[test]
    fn windows_ids_map_to_iana_zones() {
        assert_eq!(from_windows_id("W. Europe Standard Time"), Some(Tz::Europe__Berlin));
        assert_eq!(from_windows_id("Türkiye Standard Time"), Some(Tz::Europe__Istanbul));
        assert_eq!(from_windows_id("Europe/Berlin"), None);
    }
}
