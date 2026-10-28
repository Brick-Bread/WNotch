//! Reads timer lengths the way people type them: "12", "1:30", "90s", "1h20m". Ported from
//! `src/Notch.Core/Widgets/DurationParser.cs`.

use std::time::Duration;

use crate::activities::glow::round;

pub const SHORTEST: Duration = Duration::from_secs(1);
pub const LONGEST: Duration = Duration::from_secs(24 * 60 * 60);

/// A bare number is minutes ("12", "1.5"); colons read as a clock ("1:30" is a minute and a half,
/// "1:02:03" has hours); otherwise numbers carry units ("90s", "45 min", "1h 20m"). `None` for
/// anything else and for lengths outside [`SHORTEST`]..[`LONGEST`]. Rounded to whole seconds.
pub fn parse(text: &str) -> Option<Duration> {
    let text = text.trim();
    if text.is_empty() {
        return None;
    }

    let seconds = if let Some(minutes) = number(text) {
        minutes * 60.0
    } else if text.contains(':') {
        clock(text)?
    } else {
        units(text)?
    };

    if seconds.is_nan() || seconds < SHORTEST.as_secs_f64() || seconds > LONGEST.as_secs_f64() {
        return None;
    }

    Some(Duration::from_secs(round(seconds) as u64))
}

/// The shortest spelling [`parse`] reads back: "5m", "1h30m", "1m30s", "45s".
pub fn describe(duration: Duration) -> String {
    let total = round(duration.as_secs_f64()) as u64;
    let (hours, minutes, seconds) = (total / 3600, total / 60 % 60, total % 60);

    let mut text = String::new();
    if hours > 0 {
        text += &format!("{hours}h");
    }
    if minutes > 0 {
        text += &format!("{minutes}m");
    }
    if seconds > 0 || text.is_empty() {
        text += &format!("{seconds}s");
    }
    text
}

/// Digits with at most one decimal point, as `double.TryParse` with only `AllowDecimalPoint` reads them.
fn number(text: &str) -> Option<f64> {
    let digits = text.chars().filter(char::is_ascii_digit).count();
    let points = text.chars().filter(|&c| c == '.').count();
    if digits == 0 || points > 1 || digits + points != text.len() {
        return None;
    }
    text.parse().ok()
}

fn clock(text: &str) -> Option<f64> {
    let parts: Vec<&str> = text.split(':').collect();
    if !matches!(parts.len(), 2 | 3) {
        return None;
    }

    let mut seconds = 0.0;
    for part in parts {
        if part.is_empty() || !part.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        let value: i32 = part.parse().ok()?;
        seconds = seconds * 60.0 + f64::from(value);
    }
    Some(seconds)
}

/// One or more `<number> <unit>` pairs, spaces optional: "90s", "1h 20m", "2 hours 10 min".
fn units(text: &str) -> Option<f64> {
    let mut rest = text;
    let mut seconds = 0.0;
    let mut any = false;
    loop {
        rest = rest.trim_start();
        if rest.is_empty() {
            return any.then_some(seconds);
        }

        // A number: digits, optionally a point and more digits.
        let whole = rest.bytes().take_while(u8::is_ascii_digit).count();
        if whole == 0 {
            return None;
        }
        let mut end = whole;
        if rest[end..].starts_with('.') {
            let fraction = rest[end + 1..].bytes().take_while(u8::is_ascii_digit).count();
            if fraction > 0 {
                end += 1 + fraction;
            }
        }
        let value: f64 = rest[..end].parse().ok()?;
        rest = rest[end..].trim_start();

        // A unit: the longest of the spellings that matches, ignoring case.
        let letters = rest.bytes().take_while(u8::is_ascii_alphabetic).count();
        let unit = rest[..letters].to_ascii_lowercase();
        let scale = match unit.as_str() {
            "h" | "hr" | "hrs" | "hour" | "hours" => 3600.0,
            "m" | "min" | "mins" | "minute" | "minutes" => 60.0,
            "s" | "sec" | "secs" | "second" | "seconds" => 1.0,
            _ => return None,
        };
        rest = &rest[letters..];
        seconds += value * scale;
        any = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn secs(text: &str) -> Option<u64> {
        parse(text).map(|d| d.as_secs())
    }

    #[test]
    fn a_bare_number_is_minutes() {
        assert_eq!(secs("12"), Some(720));
        assert_eq!(secs("1.5"), Some(90));
        assert_eq!(secs(".5"), Some(30));
        assert_eq!(secs("5."), Some(300));
        assert_eq!(secs("-5"), None);
        assert_eq!(secs("1e2"), None);
    }

    #[test]
    fn colons_read_as_a_clock() {
        assert_eq!(secs("1:30"), Some(90));
        assert_eq!(secs("1:02:03"), Some(3723));
        assert_eq!(secs("1:75"), Some(135));
        assert_eq!(secs("1:"), None);
        assert_eq!(secs("1:2:3:4"), None);
        assert_eq!(secs("1: 30"), None);
    }

    #[test]
    fn units_can_repeat_and_ignore_case() {
        assert_eq!(secs("90s"), Some(90));
        assert_eq!(secs("1h20m"), Some(4800));
        assert_eq!(secs("2 HOURS 10 min"), Some(7800));
        assert_eq!(secs(" 45 Secs "), Some(45));
        assert_eq!(secs("1.5h"), Some(5400));
        assert_eq!(secs("1h fish"), None);
        assert_eq!(secs("h"), None);
        assert_eq!(secs("5 parsecs"), None);
    }

    #[test]
    fn lengths_outside_a_second_to_a_day_are_refused() {
        assert_eq!(secs("0"), None);
        assert_eq!(secs("0.5s"), None);
        assert_eq!(secs("24h"), Some(86400));
        assert_eq!(secs("24h1s"), None);
    }

    #[test]
    fn halves_round_to_even() {
        assert_eq!(secs("1.5s"), Some(2));
        assert_eq!(secs("2.5s"), Some(2));
    }

    #[test]
    fn describe_writes_the_shortest_spelling() {
        assert_eq!(describe(Duration::from_secs(300)), "5m");
        assert_eq!(describe(Duration::from_secs(5400)), "1h30m");
        assert_eq!(describe(Duration::from_secs(90)), "1m30s");
        assert_eq!(describe(Duration::from_secs(45)), "45s");
        assert_eq!(describe(Duration::ZERO), "0s");
        assert_eq!(describe(Duration::from_secs(3601)), "1h1s");
    }
}
