//! A pausable countdown. It keeps no timer of its own: read [`CountdownTimer::remaining`]
//! whenever the display refreshes.

use std::time::{Duration, Instant};

use serde::Serialize;

/// Where a countdown is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum CountdownState {
    Idle,
    Running,
    Paused,
    /// Reached zero and is waiting to be dismissed.
    Finished,
}

/// A countdown whose clock is passed in.
#[derive(Debug, Default)]
pub struct CountdownTimer {
    duration: Duration,
    ends_at: Option<Instant>,
    remaining_when_paused: Duration,
    running: bool,
    paused: bool,
}

impl CountdownTimer {
    /// An idle countdown.
    pub fn new() -> Self {
        Self::default()
    }

    /// The length the countdown was last started with.
    pub fn duration(&self) -> Duration {
        self.duration
    }

    /// Where the countdown is at `now`.
    pub fn state(&self, now: Instant) -> CountdownState {
        if self.paused {
            return CountdownState::Paused;
        }
        if !self.running {
            return CountdownState::Idle;
        }
        match self.ends_at {
            Some(end) if now < end => CountdownState::Running,
            _ => CountdownState::Finished,
        }
    }

    /// Time left; zero when idle or finished.
    pub fn remaining(&self, now: Instant) -> Duration {
        match self.state(now) {
            CountdownState::Running => self.ends_at.map_or(Duration::ZERO, |end| end.saturating_duration_since(now)),
            CountdownState::Paused => self.remaining_when_paused,
            _ => Duration::ZERO,
        }
    }

    /// 0 at the start, 1 when finished.
    pub fn progress(&self, now: Instant) -> f64 {
        if self.duration.is_zero() || self.state(now) == CountdownState::Idle {
            return 0.0;
        }
        (1.0 - self.remaining(now).as_secs_f64() / self.duration.as_secs_f64()).clamp(0.0, 1.0)
    }

    /// Starts counting down `duration`; a zero length just resets.
    pub fn start(&mut self, now: Instant, duration: Duration) {
        if duration.is_zero() {
            self.reset();
            return;
        }
        self.duration = duration;
        self.ends_at = Some(now + duration);
        self.running = true;
        self.paused = false;
    }

    /// Holds the countdown where it is. Does nothing unless it is running.
    pub fn pause(&mut self, now: Instant) {
        if self.state(now) == CountdownState::Running {
            self.remaining_when_paused = self.remaining(now);
            self.paused = true;
        }
    }

    /// Carries on from where [`pause`](Self::pause) stopped it.
    pub fn resume(&mut self, now: Instant) {
        if self.state(now) == CountdownState::Paused {
            self.ends_at = Some(now + self.remaining_when_paused);
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
pub fn format(remaining: Duration) -> String {
    let total = remaining.as_secs_f64().ceil() as u64;
    let (hours, minutes, seconds) = (total / 3600, total / 60 % 60, total % 60);
    if hours > 0 {
        format!("{hours}:{minutes:02}:{seconds:02}")
    } else {
        format!("{minutes}:{seconds:02}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MINUTE: Duration = Duration::from_secs(60);

    #[test]
    fn countdown_runs_pauses_and_finishes() {
        let start = Instant::now();
        let mut timer = CountdownTimer::new();
        assert_eq!(timer.state(start), CountdownState::Idle);

        timer.start(start, 5 * MINUTE);
        let now = start + 2 * MINUTE;
        assert_eq!(timer.state(now), CountdownState::Running);
        assert_eq!(timer.remaining(now), 3 * MINUTE);
        assert!((timer.progress(now) - 0.4).abs() < 1e-6);

        timer.pause(now);
        let later = now + Duration::from_secs(3600);
        assert_eq!(timer.state(later), CountdownState::Paused);
        assert_eq!(timer.remaining(later), 3 * MINUTE);

        timer.resume(later);
        let end = later + 3 * MINUTE;
        assert_eq!(timer.state(end), CountdownState::Finished);
        assert_eq!(timer.remaining(end), Duration::ZERO);

        timer.reset();
        assert_eq!(timer.state(end), CountdownState::Idle);
    }

    #[test]
    fn countdown_formats_remaining_time() {
        for (seconds, expected) in [(300.0, "5:00"), (299.2, "5:00"), (59.0, "0:59"), (3723.0, "1:02:03"), (0.0, "0:00")] {
            assert_eq!(format(Duration::from_secs_f64(seconds)), expected);
        }
    }

    #[test]
    fn starting_with_no_length_resets() {
        let now = Instant::now();
        let mut timer = CountdownTimer::new();
        timer.start(now, MINUTE);
        timer.start(now, Duration::ZERO);
        assert_eq!(timer.state(now), CountdownState::Idle);
    }
}
