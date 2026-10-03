//! Reads timer lengths the way people type them: "12", "1:30", "90s", "1h20m".

use std::time::Duration;

/// The shortest length a timer can have.
pub const SHORTEST: Duration = Duration::from_secs(1);
/// The longest length a timer can have.
pub const LONGEST: Duration = Duration::from_secs(24 * 3600);

/// A bare number is minutes ("12", "1.5"); colons read as a clock ("1:30" is a minute and a
/// half, "1:02:03" has hours); otherwise numbers carry units ("90s", "45 min", "1h 20m").
/// `None` for anything else and for lengths outside [`SHORTEST`]..=[`LONGEST`].
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
    Some(Duration::from_secs(seconds.round_ties_even() as u64))
}

/// The shortest spelling [`parse`] reads back: "5m", "1h30m", "1m30s", "45s".
pub fn describe(duration: Duration) -> String {
    let total = duration.as_secs_f64().round_ties_even() as u64;
    let (hours, minutes, seconds) = (total / 3600, total / 60 % 60, total % 60);

    let mut text = String::new();
    if hours > 0 {
        text.push_str(&format!("{hours}h"));
    }
    if minutes > 0 {
        text.push_str(&format!("{minutes}m"));
    }
    if seconds > 0 || text.is_empty() {
        text.push_str(&format!("{seconds}s"));
    }
    text
}

/// A plain decimal number: digits with at most one point, no sign or exponent.
fn number(text: &str) -> Option<f64> {
    let digits = text.bytes().filter(u8::is_ascii_digit).count();
    let points = text.bytes().filter(|&b| b == b'.').count();
    let clean = text.bytes().all(|b| b.is_ascii_digit() || b == b'.');
    if clean && digits > 0 && points <= 1 {
        text.parse().ok()
    } else {
        None
    }
}

fn clock(text: &str) -> Option<f64> {
    let parts: Vec<&str> = text.split(':').collect();
    if !(2..=3).contains(&parts.len()) {
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

/// One or more `number unit` pairs, with optional spaces: "1h 20m", "90 secs".
fn units(text: &str) -> Option<f64> {
    let bytes = text.as_bytes();
    let mut at = 0;
    let mut seconds = 0.0;
    let mut pairs = 0;
    let skip_spaces = |at: &mut usize| {
        while bytes.get(*at).is_some_and(u8::is_ascii_whitespace) {
            *at += 1;
        }
    };

    loop {
        skip_spaces(&mut at);
        if at == bytes.len() {
            break;
        }
        let start = at;
        while bytes.get(at).is_some_and(u8::is_ascii_digit) {
            at += 1;
        }
        if at == start {
            return None;
        }
        if bytes.get(at) == Some(&b'.') {
            let fraction = at + 1;
            at = fraction;
            while bytes.get(at).is_some_and(u8::is_ascii_digit) {
                at += 1;
            }
            if at == fraction {
                return None;
            }
        }
        let value: f64 = text[start..at].parse().ok()?;
        skip_spaces(&mut at);
        let word_start = at;
        while bytes.get(at).is_some_and(u8::is_ascii_alphabetic) {
            at += 1;
        }
        seconds += value * unit_seconds(&text[word_start..at].to_ascii_lowercase())?;
        pairs += 1;
    }
    (pairs > 0).then_some(seconds)
}

fn unit_seconds(word: &str) -> Option<f64> {
    match word {
        "h" | "hr" | "hrs" | "hour" | "hours" => Some(3600.0),
        "m" | "min" | "mins" | "minute" | "minutes" => Some(60.0),
        "s" | "sec" | "secs" | "second" | "seconds" => Some(1.0),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_are_read_as_typed() {
        for (text, seconds) in [
            ("12", 720),
            ("1.5", 90),
            (" 1:30 ", 90),
            ("1:02:03", 3723),
            ("90s", 90),
            ("45m", 2700),
            ("1h20m", 4800),
            ("1 hr 20 min", 4800),
            ("2 Hours", 7200),
            ("1m30s", 90),
            ("24h", 86400),
        ] {
            assert_eq!(parse(text), Some(Duration::from_secs(seconds)), "{text}");
        }
    }

    #[test]
    fn durations_that_make_no_sense_are_rejected() {
        for text in ["", "soon", "0", "-5", "1h 20", "1:2:3:4", "1:xx", "25h", "0.001s", "1:", "5 5", "1x", "+5", "1e3"] {
            assert_eq!(parse(text), None, "{text:?}");
        }
    }

    #[test]
    fn durations_are_described_in_a_form_that_reads_back() {
        for (seconds, expected) in [(300, "5m"), (5400, "1h30m"), (90, "1m30s"), (45, "45s"), (3600, "1h")] {
            let duration = Duration::from_secs(seconds);
            assert_eq!(describe(duration), expected);
            assert_eq!(parse(expected), Some(duration));
        }
    }
}
