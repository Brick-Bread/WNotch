//! What Notch does when a plugin process dies or stalls: restart it a few times with growing
//! pauses, then give up and show the plugin as failed so the user can retry it.

use std::collections::VecDeque;
use std::time::{Duration, Instant};

/// How long a plugin has to say `ready` after its `hello`.
pub const READY_TIMEOUT: Duration = Duration::from_secs(15);

/// How long a plugin has to exit after a `stop` message before it is killed.
pub const STOP_GRACE: Duration = Duration::from_secs(3);

/// Crashes tolerated within [`CRASH_WINDOW`] before Notch gives up on a plugin.
pub const MAX_CRASHES: usize = 3;

/// The span in which crashes are counted. A plugin that runs longer than this between crashes starts fresh.
pub const CRASH_WINDOW: Duration = Duration::from_secs(120);

/// What to do about a crash.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Decision {
    /// Start it again after this pause.
    Restart(Duration),
    /// It keeps crashing: mark it failed.
    GiveUp,
}

/// Counts a plugin's recent crashes.
#[derive(Debug, Default)]
pub struct RestartPolicy {
    crashes: VecDeque<Instant>,
}

impl RestartPolicy {
    pub fn new() -> Self {
        Self::default()
    }

    /// Records a crash at `now` and says what to do. The pauses are 1 s, 2 s, 4 s.
    pub fn on_crash(&mut self, now: Instant) -> Decision {
        while self.crashes.front().is_some_and(|first| now.saturating_duration_since(*first) > CRASH_WINDOW) {
            self.crashes.pop_front();
        }
        self.crashes.push_back(now);
        if self.crashes.len() > MAX_CRASHES {
            return Decision::GiveUp;
        }
        let exponent = u32::try_from(self.crashes.len() - 1).unwrap_or(0);
        Decision::Restart(Duration::from_secs(1u64 << exponent))
    }

    /// Forgets the crashes, as when the user switches the plugin off and on again.
    pub fn reset(&mut self) {
        self.crashes.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn restarts_with_growing_pauses_then_gives_up() {
        let mut policy = RestartPolicy::new();
        let start = Instant::now();
        assert_eq!(policy.on_crash(start), Decision::Restart(Duration::from_secs(1)));
        assert_eq!(policy.on_crash(start + Duration::from_secs(2)), Decision::Restart(Duration::from_secs(2)));
        assert_eq!(policy.on_crash(start + Duration::from_secs(5)), Decision::Restart(Duration::from_secs(4)));
        assert_eq!(policy.on_crash(start + Duration::from_secs(10)), Decision::GiveUp);
    }

    #[test]
    fn a_plugin_that_ran_for_a_while_starts_fresh() {
        let mut policy = RestartPolicy::new();
        let start = Instant::now();
        policy.on_crash(start);
        policy.on_crash(start + Duration::from_secs(2));
        policy.on_crash(start + Duration::from_secs(5));

        let later = start + CRASH_WINDOW + Duration::from_secs(60);
        assert_eq!(policy.on_crash(later), Decision::Restart(Duration::from_secs(1)));
    }

    #[test]
    fn resetting_forgets_the_crashes() {
        let mut policy = RestartPolicy::new();
        let now = Instant::now();
        for _ in 0..MAX_CRASHES {
            policy.on_crash(now);
        }
        policy.reset();
        assert_eq!(policy.on_crash(now), Decision::Restart(Duration::from_secs(1)));
    }
}
