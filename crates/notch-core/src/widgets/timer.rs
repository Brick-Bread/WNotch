//! The timer card's brain: a countdown that may run a Pomodoro cycle, what the card shows for it
//! and the activity it puts in the pill. Time is passed in; the app calls [`TimerController::tick`]
//! a few times a second while a timer is active.

use std::time::{Duration, Instant};

use serde::Serialize;

use super::countdown::{self, CountdownState, CountdownTimer};
use super::duration;
use super::pomodoro::{PomodoroCycle, PomodoroDurations, PomodoroPhase};
use crate::activity::{Activity, ActivityTier};
use crate::glow::{Glow, GlowColor, GlowPattern};

/// Id of the timer's activity in the pill.
pub const TIMER_ACTIVITY_ID: &str = "timer";
/// Id of the short notice shown when a Pomodoro phase changes.
pub const POMODORO_NOTICE_ID: &str = "timer.phase";
const TIMER_GLYPH: &str = "\u{23F1}";

/// What the timer card shows.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TimerView {
    pub state: CountdownState,
    /// "4:05", "Done", or "0:00" when idle.
    pub display: String,
    /// What is being timed: the Pomodoro phase, the preset's name, or plain "Timer".
    pub title: String,
    /// The colour of the digits and the glow.
    pub color: GlowColor,
    /// 0 at the start, 1 when finished.
    pub progress: f64,
    /// True while a Pomodoro cycle runs rather than a single countdown.
    pub pomodoro: bool,
}

/// What changed when the timer was ticked.
#[derive(Debug, Clone, PartialEq)]
pub struct TimerUpdate {
    pub view: TimerView,
    /// The activity for the pill; `None` takes it away.
    pub activity: Option<Activity>,
    /// A short notice that a Pomodoro phase began.
    pub notice: Option<Activity>,
    /// Play the alert sound.
    pub chime: bool,
}

/// Runs the countdown, the Pomodoro cycle and the bookkeeping around them.
#[derive(Debug, Default)]
pub struct TimerController {
    countdown: CountdownTimer,
    pomodoro: Option<PomodoroCycle>,
    name: Option<String>,
    announced: bool,
    last_signature: Option<String>,
}

impl TimerController {
    /// An idle timer.
    pub fn new() -> Self {
        Self::default()
    }

    /// Starts a plain countdown; `name` is shown in place of "Timer" when it is not empty.
    pub fn start(&mut self, now: Instant, name: &str, length: Duration) {
        self.pomodoro = None;
        self.name = (!name.is_empty()).then(|| name.to_owned());
        self.start_countdown(now, length);
    }

    /// Starts a countdown from text typed by the user ("12", "1:30", "90s", "1h20m"). False when it cannot be read.
    pub fn start_text(&mut self, now: Instant, text: &str) -> bool {
        match duration::parse(text) {
            Some(length) => {
                self.start(now, "", length);
                true
            }
            None => false,
        }
    }

    /// Starts a Pomodoro cycle with a focus session.
    pub fn start_pomodoro(&mut self, now: Instant, durations: PomodoroDurations) {
        let cycle = PomodoroCycle::new(durations);
        let length = cycle.current_duration();
        self.pomodoro = Some(cycle);
        self.name = None;
        self.start_countdown(now, length);
    }

    /// Holds the countdown.
    pub fn pause(&mut self, now: Instant) {
        self.countdown.pause(now);
    }

    /// Carries on after [`pause`](Self::pause).
    pub fn resume(&mut self, now: Instant) {
        self.countdown.resume(now);
    }

    /// Cancels the timer, or dismisses it once finished.
    pub fn reset(&mut self) {
        self.pomodoro = None;
        self.name = None;
        self.countdown.reset();
    }

    /// Whether the app should keep ticking: a timer is running, paused or waiting to be dismissed.
    pub fn is_active(&self, now: Instant) -> bool {
        self.countdown.state(now) != CountdownState::Idle
    }

    fn start_countdown(&mut self, now: Instant, length: Duration) {
        self.countdown.start(now, length);
        self.announced = false;
        self.last_signature = None;
    }

    fn title(&self) -> String {
        self.pomodoro
            .as_ref()
            .map(PomodoroCycle::label)
            .or_else(|| self.name.clone())
            .unwrap_or_else(|| "Timer".to_owned())
    }

    /// Red for Pomodoro focus (the tomato), green for breaks, orange for a plain timer.
    fn color(&self) -> GlowColor {
        match self.pomodoro.as_ref().map(PomodoroCycle::phase) {
            Some(PomodoroPhase::Focus) => GlowColor::RED,
            Some(PomodoroPhase::ShortBreak | PomodoroPhase::LongBreak) => GlowColor::GREEN,
            None => GlowColor::ORANGE,
        }
    }

    /// The card as it is at `now`.
    pub fn view(&self, now: Instant) -> TimerView {
        let state = self.countdown.state(now);
        let display = match state {
            CountdownState::Idle => "0:00".to_owned(),
            CountdownState::Finished => "Done".to_owned(),
            _ => countdown::format(self.countdown.remaining(now)),
        };
        TimerView {
            state,
            display,
            title: self.title(),
            color: self.color(),
            progress: self.countdown.progress(now),
            pomodoro: self.pomodoro.is_some(),
        }
    }

    /// Moves a Pomodoro on when its phase ran out and reports what changed since the last call, or
    /// `None` when nothing visible did (ticks arrive several times a second).
    pub fn tick(&mut self, now: Instant) -> Option<TimerUpdate> {
        let mut notice = None;
        let mut chime = false;

        if self.countdown.state(now) == CountdownState::Finished {
            if let Some(cycle) = self.pomodoro.as_mut() {
                let focus_next = cycle.advance() == PomodoroPhase::Focus;
                let length = cycle.current_duration();
                notice = Some(Activity {
                    id: POMODORO_NOTICE_ID.to_owned(),
                    tier: ActivityTier::Transient,
                    title: if focus_next { "Back to focus" } else { "Break time" }.to_owned(),
                    detail: Some(cycle.label()),
                    glyph: Some(TIMER_GLYPH.to_owned()),
                    glow: Some(Glow {
                        color: if focus_next { GlowColor::RED } else { GlowColor::GREEN },
                        pattern: GlowPattern::Flash,
                        strength: 1.0,
                    }),
                    lifetime: Some(Duration::from_secs(4)),
                    ..Activity::default()
                });
                chime = true;
                self.start_countdown(now, length);
            }
        }

        let view = self.view(now);
        let signature = format!("{:?}:{}:{}", view.state, view.display, view.title);
        if notice.is_none() && self.last_signature.as_deref() == Some(signature.as_str()) {
            return None;
        }
        self.last_signature = Some(signature);

        let activity = match view.state {
            CountdownState::Idle => None,
            CountdownState::Finished => {
                if !self.announced {
                    self.announced = true;
                    chime = true;
                }
                Some(self.activity(
                    ActivityTier::Attention,
                    &view.title,
                    "Done",
                    Some(Glow { color: view.color, pattern: GlowPattern::Pulse, strength: 1.0 }),
                ))
            }
            CountdownState::Paused => {
                Some(self.activity(ActivityTier::Ongoing, &format!("{} paused", view.title), &view.display, None))
            }
            CountdownState::Running => {
                // The glow breathes in the same colour as the digits; focus a touch stronger.
                let focus = self.pomodoro.as_ref().is_some_and(|c| c.phase() == PomodoroPhase::Focus);
                let glow = Glow { color: view.color, pattern: GlowPattern::Breathe, strength: if focus { 0.55 } else { 0.5 } };
                Some(self.activity(ActivityTier::Ongoing, &view.title, &view.display, Some(glow)))
            }
        };
        Some(TimerUpdate { view, activity, notice, chime })
    }

    fn activity(&self, tier: ActivityTier, title: &str, detail: &str, glow: Option<Glow>) -> Activity {
        Activity {
            id: TIMER_ACTIVITY_ID.to_owned(),
            tier,
            title: title.to_owned(),
            detail: Some(detail.to_owned()),
            glyph: Some(TIMER_GLYPH.to_owned()),
            glow,
            ..Activity::default()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MINUTE: Duration = Duration::from_secs(60);

    #[test]
    fn a_plain_timer_publishes_breathes_then_pulses_once_when_done() {
        let t0 = Instant::now();
        let mut timer = TimerController::new();
        timer.start(t0, "Tea", 3 * MINUTE);

        let running = timer.tick(t0).expect("first tick publishes");
        let activity = running.activity.expect("activity");
        assert_eq!((activity.id.as_str(), activity.title.as_str(), activity.detail.as_deref()), ("timer", "Tea", Some("3:00")));
        assert_eq!(activity.tier, ActivityTier::Ongoing);
        let glow = activity.glow.expect("glow");
        assert_eq!((glow.color, glow.pattern, glow.strength), (GlowColor::ORANGE, GlowPattern::Breathe, 0.5));
        assert!(!running.chime);
        assert!(timer.tick(t0 + Duration::from_millis(250)).is_none(), "the same text is not republished");

        let done = timer.tick(t0 + 3 * MINUTE).expect("done");
        let activity = done.activity.expect("activity");
        assert_eq!((activity.tier, activity.detail.as_deref()), (ActivityTier::Attention, Some("Done")));
        assert_eq!(activity.glow.expect("glow").pattern, GlowPattern::Pulse);
        assert!(done.chime);
        assert!(timer.tick(t0 + 4 * MINUTE).is_none());

        timer.reset();
        let idle = timer.tick(t0 + 5 * MINUTE).expect("idle");
        assert!(idle.activity.is_none());
        assert!(!timer.is_active(t0 + 5 * MINUTE));
    }

    #[test]
    fn pausing_shows_the_remaining_time_without_a_glow() {
        let t0 = Instant::now();
        let mut timer = TimerController::new();
        timer.start(t0, "", 5 * MINUTE);
        timer.tick(t0);
        timer.pause(t0 + MINUTE);
        let paused = timer.tick(t0 + MINUTE).expect("paused").activity.expect("activity");
        assert_eq!((paused.title.as_str(), paused.detail.as_deref(), paused.glow), ("Timer paused", Some("4:00"), None));
        timer.resume(t0 + 2 * MINUTE);
        let running = timer.tick(t0 + 2 * MINUTE).expect("running");
        assert_eq!(running.view.state, CountdownState::Running);
    }

    #[test]
    fn a_pomodoro_carries_on_with_a_chime_and_a_notice() {
        let t0 = Instant::now();
        let mut timer = TimerController::new();
        timer.start_pomodoro(t0, PomodoroDurations::CLASSIC);

        let first = timer.tick(t0).expect("focus");
        assert_eq!(first.view.title, "Focus 1/4");
        assert_eq!(first.view.color, GlowColor::RED);
        assert_eq!(first.activity.expect("activity").glow.expect("glow").strength, 0.55);

        let rest = timer.tick(t0 + 25 * MINUTE).expect("break");
        assert!(rest.chime);
        assert_eq!(rest.view.title, "Short break");
        assert_eq!(rest.view.display, "5:00");
        assert_eq!(rest.view.color, GlowColor::GREEN);
        let notice = rest.notice.expect("notice");
        assert_eq!((notice.id.as_str(), notice.title.as_str(), notice.detail.as_deref()), ("timer.phase", "Break time", Some("Short break")));
        assert_eq!((notice.tier, notice.lifetime), (ActivityTier::Transient, Some(Duration::from_secs(4))));
        assert_eq!(notice.glow.expect("glow").pattern, GlowPattern::Flash);

        let back = timer.tick(t0 + 30 * MINUTE).expect("focus again");
        assert_eq!(back.notice.expect("notice").title, "Back to focus");
        assert_eq!(back.view.title, "Focus 2/4");
    }

    #[test]
    fn typed_lengths_are_validated() {
        let now = Instant::now();
        let mut timer = TimerController::new();
        assert!(!timer.start_text(now, "soon"));
        assert!(!timer.is_active(now));
        assert!(timer.start_text(now, "1:30"));
        assert_eq!(timer.view(now).display, "1:30");
    }
}
