//! A pausable countdown. Ported from `src/Notch.Core/Widgets/CountdownTimer.cs`.

use std::time::Duration;

use chrono::{DateTime, Utc};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CountdownState {
    Idle,
    Running,
    Paused,
    /// Reached zero and is waiting to be dismissed.
    Finished,
}

/// A pausable countdown. It keeps no timer of its own: read [`Self::remaining`] whenever the
/// display refreshes, and [`Self::next_deadline`] says when it will finish.
///
/// C# reads the time from a `TimeProvider`; here every call that needs it takes `now`.
#[derive(Debug, Clone, Default)]
pub struct CountdownTimer {
    ends_at: DateTime<Utc>,
    remaining_when_paused: Duration,
    running: bool,
    paused: bool,
    duration: Duration,
}

impl CountdownTimer {
    pub fn new() -> Self {
        Self::default()
    }

    /// The length the countdown was last started with.
    pub fn duration(&self) -> Duration {
        self.duration
    }

    pub fn state(&self, now: DateTime<Utc>) -> CountdownState {
        if self.paused {
            return CountdownState::Paused;
        }

        if !self.running {
            return CountdownState::Idle;
        }

        if now >= self.ends_at { CountdownState::Finished } else { CountdownState::Running }
    }

    pub fn remaining(&self, now: DateTime<Utc>) -> Duration {
        match self.state(now) {
            CountdownState::Running => (self.ends_at - now).to_std().unwrap_or_default(),
            CountdownState::Paused => self.remaining_when_paused,
            _ => Duration::ZERO,
        }
    }

    /// 0 at the start, 1 when finished.
    pub fn progress(&self, now: DateTime<Utc>) -> f64 {
        if self.duration > Duration::ZERO && self.state(now) != CountdownState::Idle {
            (1.0 - self.remaining(now).as_secs_f64() / self.duration.as_secs_f64()).clamp(0.0, 1.0)
        } else {
            0.0
        }
    }

    /// When a running countdown reaches zero; `None` while it is idle, paused or already finished.
    pub fn next_deadline(&self, now: DateTime<Utc>) -> Option<DateTime<Utc>> {
        (self.state(now) == CountdownState::Running).then_some(self.ends_at)
    }

    pub fn start(&mut self, duration: Duration, now: DateTime<Utc>) {
        if duration.is_zero() {
            self.reset();
            return;
        }

        self.duration = duration;
        self.ends_at = now + duration;
        self.running = true;
        self.paused = false;
    }

    pub fn pause(&mut self, now: DateTime<Utc>) {
        if self.state(now) == CountdownState::Running {
            self.remaining_when_paused = self.remaining(now);
            self.paused = true;
        }
    }

    pub fn resume(&mut self, now: DateTime<Utc>) {
        if self.state(now) == CountdownState::Paused {
            self.ends_at = now + self.remaining_when_paused;
            self.paused = false;
        }
    }

    /// Stops the countdown, or dismisses it once finished.
    pub fn reset(&mut self) {
        self.running = false;
        self.paused = false;
    }
}

/// "4:05", or "1:02:03" from an hour up. Rounds up so a fresh five minutes reads 5:00, not 4:59.
///
/// C# takes a `TimeSpan`, which can be negative and then reads "0:00"; a `Duration` cannot be.
pub fn format(remaining: Duration) -> String {
    let total = remaining.as_secs_f64().ceil() as u64;
    let (hours, minutes, seconds) = (total / 3600, total / 60 % 60, total % 60);
    if hours >= 1 { format!("{hours}:{minutes:02}:{seconds:02}") } else { format!("{minutes}:{seconds:02}") }
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn countdown_runs_pauses_and_finishes() {
        let mut now = Utc.with_ymd_and_hms(2000, 1, 1, 0, 0, 0).unwrap();
        let mut timer = CountdownTimer::new();
        assert_eq!(timer.state(now), CountdownState::Idle);

        timer.start(Duration::from_secs(5 * 60), now);
        now += Duration::from_secs(2 * 60);
        assert_eq!(timer.state(now), CountdownState::Running);
        assert_eq!(timer.remaining(now), Duration::from_secs(3 * 60));
        assert!((timer.progress(now) - 0.4).abs() < 1e-6);
        assert_eq!(timer.next_deadline(now), Some(now + Duration::from_secs(3 * 60)));

        timer.pause(now);
        now += Duration::from_secs(60 * 60);
        assert_eq!(timer.state(now), CountdownState::Paused);
        assert_eq!(timer.remaining(now), Duration::from_secs(3 * 60));
        assert_eq!(timer.next_deadline(now), None);

        timer.resume(now);
        now += Duration::from_secs(3 * 60);
        assert_eq!(timer.state(now), CountdownState::Finished);
        assert_eq!(timer.remaining(now), Duration::ZERO);

        timer.reset();
        assert_eq!(timer.state(now), CountdownState::Idle);
    }

    #[test]
    fn countdown_formats_remaining_time() {
        // C#'s last case is -4 s, which a Duration cannot hold; zero reads the same.
        for (seconds, expected) in
            [(300.0, "5:00"), (299.2, "5:00"), (59.0, "0:59"), (3723.0, "1:02:03"), (0.0, "0:00")]
        {
            assert_eq!(format(Duration::from_secs_f64(seconds)), expected, "{seconds}");
        }
    }
}
