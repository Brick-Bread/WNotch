//! Steps through focus sessions and breaks. It only tracks the sequence; a countdown does the counting.

use std::time::Duration;

/// Which part of the cycle a Pomodoro is in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PomodoroPhase {
    Focus,
    ShortBreak,
    LongBreak,
}

/// Lengths of the phases.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PomodoroDurations {
    pub focus: Duration,
    pub short_break: Duration,
    pub long_break: Duration,
    /// Every this many focus sessions, the break is a long one.
    pub focuses_per_long_break: u32,
}

impl PomodoroDurations {
    /// 25 minutes of focus, 5 of rest, and 15 after every fourth session.
    pub const CLASSIC: PomodoroDurations = PomodoroDurations {
        focus: Duration::from_secs(25 * 60),
        short_break: Duration::from_secs(5 * 60),
        long_break: Duration::from_secs(15 * 60),
        focuses_per_long_break: 4,
    };

    /// Lengths from the settings' minutes, with anything out of range (1..=600) replaced by the classic value.
    pub fn from_minutes(focus: i32, short_break: i32, long_break: i32) -> Self {
        let classic = Self::CLASSIC;
        let minutes = |value: i32, fallback: Duration| {
            u64::try_from(value)
                .ok()
                .filter(|m| (1..=600).contains(m))
                .map_or(fallback, |m| Duration::from_secs(m * 60))
        };
        Self {
            focus: minutes(focus, classic.focus),
            short_break: minutes(short_break, classic.short_break),
            long_break: minutes(long_break, classic.long_break),
            focuses_per_long_break: classic.focuses_per_long_break,
        }
    }
}

/// The sequence of phases.
#[derive(Debug, Clone)]
pub struct PomodoroCycle {
    durations: PomodoroDurations,
    phase: PomodoroPhase,
    completed_focuses: u32,
}

impl PomodoroCycle {
    /// A cycle that starts with a focus session.
    pub fn new(durations: PomodoroDurations) -> Self {
        Self { durations, phase: PomodoroPhase::Focus, completed_focuses: 0 }
    }

    /// The current phase.
    pub fn phase(&self) -> PomodoroPhase {
        self.phase
    }

    /// Focus sessions finished so far.
    pub fn completed_focuses(&self) -> u32 {
        self.completed_focuses
    }

    fn per_set(&self) -> u32 {
        self.durations.focuses_per_long_break.max(1)
    }

    /// Which focus session of the current set this is (1-based), e.g. 2 in "Focus 2/4".
    pub fn focus_number(&self) -> u32 {
        self.completed_focuses % self.per_set() + 1
    }

    /// How long the current phase lasts.
    pub fn current_duration(&self) -> Duration {
        match self.phase {
            PomodoroPhase::ShortBreak => self.durations.short_break,
            PomodoroPhase::LongBreak => self.durations.long_break,
            PomodoroPhase::Focus => self.durations.focus,
        }
    }

    /// "Focus 2/4", "Short break" or "Long break".
    pub fn label(&self) -> String {
        match self.phase {
            PomodoroPhase::ShortBreak => "Short break".to_owned(),
            PomodoroPhase::LongBreak => "Long break".to_owned(),
            PomodoroPhase::Focus => format!("Focus {}/{}", self.focus_number(), self.per_set()),
        }
    }

    /// Moves on once the current phase has run out, and returns the new phase.
    pub fn advance(&mut self) -> PomodoroPhase {
        if self.phase == PomodoroPhase::Focus {
            self.completed_focuses += 1;
            self.phase = if self.completed_focuses % self.per_set() == 0 {
                PomodoroPhase::LongBreak
            } else {
                PomodoroPhase::ShortBreak
            };
        } else {
            self.phase = PomodoroPhase::Focus;
        }
        self.phase
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn alternates_and_takes_a_long_break_every_fourth_focus() {
        let mut cycle = PomodoroCycle::new(PomodoroDurations::CLASSIC);
        let mut phases = vec![cycle.label()];
        for _ in 0..9 {
            cycle.advance();
            phases.push(cycle.label());
        }
        assert_eq!(
            phases,
            ["Focus 1/4", "Short break", "Focus 2/4", "Short break", "Focus 3/4", "Short break", "Focus 4/4", "Long break", "Focus 1/4", "Short break"]
        );
        assert_eq!(cycle.completed_focuses(), 5);
    }

    #[test]
    fn durations_follow_the_phase() {
        let minutes = |m: u64| Duration::from_secs(m * 60);
        let mut cycle = PomodoroCycle::new(PomodoroDurations {
            focus: minutes(50),
            short_break: minutes(10),
            long_break: minutes(30),
            focuses_per_long_break: 2,
        });
        assert_eq!(cycle.current_duration(), minutes(50));
        cycle.advance();
        assert_eq!(cycle.current_duration(), minutes(10));
        cycle.advance();
        cycle.advance();
        assert_eq!(cycle.phase(), PomodoroPhase::LongBreak);
        assert_eq!(cycle.current_duration(), minutes(30));
    }

    #[test]
    fn settings_minutes_out_of_range_fall_back_to_classic() {
        let durations = PomodoroDurations::from_minutes(50, 0, 601);
        assert_eq!(durations.focus, Duration::from_secs(50 * 60));
        assert_eq!(durations.short_break, PomodoroDurations::CLASSIC.short_break);
        assert_eq!(durations.long_break, PomodoroDurations::CLASSIC.long_break);
    }
}
