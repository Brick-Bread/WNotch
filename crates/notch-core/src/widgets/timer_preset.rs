//! A timer the user starts with one click from the timer card.

use std::time::Duration;

use serde::Serialize;

use super::duration::{self, LONGEST, SHORTEST};

/// How many presets the timer card shows.
pub const MAX_TIMER_PRESETS: usize = 6;
/// Longest name a preset keeps.
pub const MAX_NAME_LENGTH: usize = 10;

/// A one-click timer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct TimerPreset {
    /// Shown on the button and in the pill while it runs; empty to show the length instead.
    pub name: String,
    /// How long it counts.
    pub seconds: u32,
}

impl TimerPreset {
    /// A preset of the given name and length.
    pub fn new(name: impl Into<String>, seconds: u32) -> Self {
        Self { name: name.into(), seconds }
    }

    /// How long the timer counts.
    pub fn duration(&self) -> Duration {
        Duration::from_secs(u64::from(self.seconds))
    }

    /// What the button says: the name, or the length ("15m") when there is none.
    pub fn label(&self) -> String {
        if self.name.is_empty() {
            duration::describe(self.duration())
        } else {
            self.name.clone()
        }
    }

    /// Whether the length is one a timer can have.
    pub fn is_valid(&self) -> bool {
        (SHORTEST..=LONGEST).contains(&self.duration())
    }

    /// The preset as one line of text, which [`TimerPreset::parse`] reads back: "Tea 3m" or "15m".
    pub fn to_line(&self) -> String {
        let length = duration::describe(self.duration());
        if self.name.is_empty() {
            length
        } else {
            format!("{} {length}", self.name)
        }
    }

    /// Reads a line such as "15m", "Tea 3m" or "Long walk 1h 20m": a length (see
    /// [`duration::parse`]), optionally with a name in front of it.
    pub fn parse(line: &str) -> Option<TimerPreset> {
        let line = line.trim();
        if let Some(whole) = duration::parse(line) {
            return Some(TimerPreset::new("", whole.as_secs() as u32));
        }

        // The name ends at the first space after which the rest reads as a length.
        for (space, _) in line.match_indices(' ') {
            if let Some(length) = duration::parse(&line[space..]) {
                return Some(TimerPreset::new(shorten(line[..space].trim()), length.as_secs() as u32));
            }
        }
        None
    }
}

fn shorten(name: &str) -> String {
    match name.char_indices().nth(MAX_NAME_LENGTH) {
        Some((end, _)) => name[..end].trim_end().to_owned(),
        None => name.to_owned(),
    }
}

/// The presets from the settings that can be used: lines that read as a length, no more than fit.
pub fn from_lines(lines: &[String]) -> Vec<TimerPreset> {
    lines
        .iter()
        .filter_map(|line| TimerPreset::parse(line))
        .filter(TimerPreset::is_valid)
        .take(MAX_TIMER_PRESETS)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preset_lines_have_an_optional_name() {
        for (line, name, seconds, label) in [
            ("15m", "", 900, "15m"),
            ("Tea 3m", "Tea", 180, "Tea"),
            ("Long walk 1h 20m", "Long walk", 4800, "Long walk"),
            ("Round 2 1:30", "Round 2", 90, "Round 2"),
            ("A very long preset name 10m", "A very lon", 600, "A very lon"),
        ] {
            let preset = TimerPreset::parse(line).expect(line);
            assert_eq!(preset, TimerPreset::new(name, seconds), "{line}");
            assert_eq!(preset.label(), label);
            assert_eq!(TimerPreset::parse(&preset.to_line()), Some(preset));
        }
    }

    #[test]
    fn preset_lines_without_a_length_are_rejected() {
        for line in ["Tea", "Tea soon", ""] {
            assert_eq!(TimerPreset::parse(line), None, "{line:?}");
        }
    }

    #[test]
    fn from_lines_skips_unreadable_lines_and_keeps_six() {
        let lines: Vec<String> = ["5m", "nonsense", "Tea 3m", "1m", "2m", "3m", "4m", "6m"].iter().map(|l| (*l).to_owned()).collect();
        let presets = from_lines(&lines);
        assert_eq!(presets.len(), MAX_TIMER_PRESETS);
        assert_eq!(presets[1].label(), "Tea");
        assert_eq!(presets[5].label(), "4m");
    }

    #[test]
    fn unlabeled_presets_show_their_length() {
        assert_eq!(TimerPreset::new("", 5400).label(), "1h30m");
    }
}
