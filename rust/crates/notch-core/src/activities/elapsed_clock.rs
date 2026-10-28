//! How a clock that counts up from a moment reads: "4:05", or "1:23:45" from an hour on. Ported
//! from `src/Notch.Core/Activities/ElapsedClock.cs`.

use std::time::Duration;

use chrono::{DateTime, Utc};

pub fn format(elapsed: Duration) -> String {
    let total = elapsed.as_secs();
    let (hours, minutes, seconds) = (total / 3600, total / 60 % 60, total % 60);
    if hours >= 1 { format!("{hours}:{minutes:02}:{seconds:02}") } else { format!("{minutes}:{seconds:02}") }
}

/// The clock from `from` to `now`; a `from` in the future reads "0:00".
pub fn since(from: DateTime<Utc>, now: DateTime<Utc>) -> String {
    format((now - from).to_std().unwrap_or_default())
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn counts_minutes_and_seconds_then_hours() {
        for (seconds, expected) in [(0, "0:00"), (245, "4:05"), (3599, "59:59"), (5025, "1:23:45"), (90000, "25:00:00")]
        {
            assert_eq!(format(Duration::from_secs(seconds)), expected, "{seconds}");
        }

        // A negative span cannot be a `Duration`; it reaches the clock through `since`.
        let start = Utc.with_ymd_and_hms(2026, 10, 23, 12, 0, 0).unwrap();
        assert_eq!(since(start, start - chrono::TimeDelta::seconds(5)), "0:00");
    }

    #[test]
    fn since_counts_from_the_moment_given() {
        let start = Utc.with_ymd_and_hms(2026, 10, 23, 12, 0, 0).unwrap();
        assert_eq!(since(start, start + chrono::TimeDelta::seconds(3630)), "1:00:30");
    }
}
