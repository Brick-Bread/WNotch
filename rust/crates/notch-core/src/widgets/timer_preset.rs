//! A timer the user starts with one click from the timer card. Ported from
//! `src/Notch.Core/Widgets/TimerPreset.cs`.

use std::time::Duration;

use serde_json::{Map, Value};

use super::duration;
use crate::json::{self, Read};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TimerPreset {
    /// Shown on the button and in the pill while it runs; empty to show the length instead.
    pub name: String,
    /// How long it counts.
    pub seconds: i32,
}

impl TimerPreset {
    pub const MAX_NAME_LENGTH: usize = 10;

    pub fn new(name: impl Into<String>, seconds: i32) -> Self {
        Self { name: name.into(), seconds }
    }

    pub fn duration(&self) -> Duration {
        Duration::from_secs(u64::try_from(self.seconds).unwrap_or(0))
    }

    /// What the button says: the name, or the length ("15m") when there is none.
    pub fn label(&self) -> String {
        if self.name.is_empty() { duration::describe(self.duration()) } else { self.name.clone() }
    }

    pub fn is_valid(&self) -> bool {
        self.seconds > 0 && self.duration() >= duration::SHORTEST && self.duration() <= duration::LONGEST
    }

    /// The preset as one line of text, which [`Self::parse`] reads back: "Tea 3m" or "15m".
    pub fn to_line(&self) -> String {
        let length = duration::describe(self.duration());
        if self.name.is_empty() { length } else { format!("{} {length}", self.name) }
    }

    /// Reads a line such as "15m", "Tea 3m" or "Long walk 1h 20m": a length (see
    /// [`duration::parse`]), optionally with a name in front of it.
    pub fn parse(line: &str) -> Option<Self> {
        let line = line.trim();
        if let Some(whole) = duration::parse(line) {
            return Some(Self::new("", whole.as_secs() as i32));
        }

        // The name ends at the first space after which the rest reads as a length.
        for (space, _) in line.match_indices(' ') {
            if let Some(length) = duration::parse(&line[space..]) {
                return Some(Self::new(clip_name(&line[..space]), length.as_secs() as i32));
            }
        }
        None
    }

    pub(crate) fn read(value: &Value) -> Read<Self> {
        let Value::Object(map) = value else { return Err(json::Mismatch) };
        let mut preset = Self::new("", 0);
        for (key, value) in map {
            match key.as_str() {
                "Name" => preset.name = json::string(value)?,
                "Seconds" => preset.seconds = json::int(value)?,
                _ => {}
            }
        }
        Ok(preset)
    }

    pub(crate) fn to_json(&self) -> Value {
        let mut map = Map::new();
        map.insert("Name".into(), Value::String(self.name.clone()));
        map.insert("Seconds".into(), Value::from(self.seconds));
        Value::Object(map)
    }
}

/// A name trimmed and cut to [`TimerPreset::MAX_NAME_LENGTH`] UTF-16 units, as C# cuts it.
pub(crate) fn clip_name(name: &str) -> String {
    let name = name.trim();
    let units: Vec<u16> = name.encode_utf16().collect();
    if units.len() <= TimerPreset::MAX_NAME_LENGTH {
        return name.to_owned();
    }
    String::from_utf16_lossy(&units[..TimerPreset::MAX_NAME_LENGTH]).trim_end().to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_a_length_with_an_optional_name() {
        assert_eq!(TimerPreset::parse("15m"), Some(TimerPreset::new("", 900)));
        assert_eq!(TimerPreset::parse("Tea 3m"), Some(TimerPreset::new("Tea", 180)));
        assert_eq!(TimerPreset::parse("Long walk 1h 20m"), Some(TimerPreset::new("Long walk", 4800)));
        assert_eq!(TimerPreset::parse("A very long name indeed 5m"), Some(TimerPreset::new("A very lon", 300)));
        assert_eq!(TimerPreset::parse("Tea"), None);
    }

    #[test]
    fn writes_a_line_it_reads_back() {
        for preset in [TimerPreset::new("", 900), TimerPreset::new("Tea", 180)] {
            assert_eq!(TimerPreset::parse(&preset.to_line()), Some(preset));
        }
    }

    #[test]
    fn a_label_falls_back_to_the_length() {
        assert_eq!(TimerPreset::new("", 300).label(), "5m");
        assert_eq!(TimerPreset::new("Tea", 300).label(), "Tea");
    }

    #[test]
    fn valid_lengths_are_a_second_to_a_day() {
        assert!(!TimerPreset::new("", 0).is_valid());
        assert!(!TimerPreset::new("", -5).is_valid());
        assert!(TimerPreset::new("", 86400).is_valid());
        assert!(!TimerPreset::new("", 86401).is_valid());
    }
}
