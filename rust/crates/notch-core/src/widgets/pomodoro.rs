//! Focus sessions and breaks. Ported from `src/Notch.Core/Widgets/PomodoroCycle.cs`.

use std::time::Duration;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PomodoroPhase {
    Focus,
    ShortBreak,
    LongBreak,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PomodoroDurations {
    pub focus: Duration,
    pub short_break: Duration,
    pub long_break: Duration,
    /// Every this many focus sessions, the break is a long one.
    pub focuses_per_long_break: u32,
}

impl PomodoroDurations {
    pub const CLASSIC: Self = Self {
        focus: Duration::from_secs(25 * 60),
        short_break: Duration::from_secs(5 * 60),
        long_break: Duration::from_secs(15 * 60),
        focuses_per_long_break: 4,
    };
}

/// Steps through focus sessions and breaks. It only tracks the sequence; a
/// [`CountdownTimer`](super::countdown::CountdownTimer) does the counting.
#[derive(Debug, Clone)]
pub struct PomodoroCycle {
    pub durations: PomodoroDurations,
    phase: PomodoroPhase,
    completed_focuses: u32,
}

impl PomodoroCycle {
    pub fn new(durations: PomodoroDurations) -> Self {
        Self { durations, phase: PomodoroPhase::Focus, completed_focuses: 0 }
    }

    pub fn phase(&self) -> PomodoroPhase {
        self.phase
    }

    pub fn completed_focuses(&self) -> u32 {
        self.completed_focuses
    }

    fn per_long_break(&self) -> u32 {
        self.durations.focuses_per_long_break.max(1)
    }

    /// Which focus session of the current set this is (1-based), e.g. 2 in "Focus 2/4".
    pub fn focus_number(&self) -> u32 {
        self.completed_focuses % self.per_long_break() + 1
    }

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
            PomodoroPhase::ShortBreak => "Short break".into(),
            PomodoroPhase::LongBreak => "Long break".into(),
            PomodoroPhase::Focus => format!("Focus {}/{}", self.focus_number(), self.per_long_break()),
        }
    }

    /// Moves on once the current phase has run out, and returns the new phase.
    pub fn advance(&mut self) -> PomodoroPhase {
        if self.phase == PomodoroPhase::Focus {
            self.completed_focuses += 1;
            self.phase = if self.completed_focuses % self.per_long_break() == 0 {
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
    fn pomodoro_alternates_and_takes_a_long_break_every_fourth_focus() {
        let mut cycle = PomodoroCycle::new(PomodoroDurations::CLASSIC);
        let mut phases = vec![cycle.label()];

        for _ in 0..9 {
            cycle.advance();
            phases.push(cycle.label());
        }

        assert_eq!(
            phases,
            [
                "Focus 1/4",
                "Short break",
                "Focus 2/4",
                "Short break",
                "Focus 3/4",
                "Short break",
                "Focus 4/4",
                "Long break",
                "Focus 1/4",
                "Short break"
            ]
        );
        assert_eq!(cycle.completed_focuses(), 5);
    }

    #[test]
    fn pomodoro_durations_follow_the_phase() {
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
}
