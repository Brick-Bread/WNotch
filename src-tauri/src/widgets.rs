//! The timer and calendar cards on Home and the Stats tab: the threads behind them, their
//! commands and the events they emit (`timer`, `calendar`, `stats`).

mod sampler;
mod sound;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread::{self, Thread};
use std::time::{Duration, Instant};

use chrono::Local;
use notch_core::settings::AppSettings;
use notch_core::widgets::calendar::{CalendarRow, CalendarSnapshot, MAX_CALENDAR_ENTRIES};
use notch_core::widgets::duration;
use notch_core::widgets::pomodoro::PomodoroDurations;
use notch_core::widgets::stats::StatsView;
use notch_core::widgets::timer::{TimerController, TimerUpdate, TimerView, TIMER_ACTIVITY_ID};
use notch_core::widgets::timer_preset::{self, TimerPreset};
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, Listener, State};

use crate::activity_hub::ActivityHub;
use crate::state::Backend;
use sampler::Sampler;

/// How often a running timer's display is refreshed.
const TIMER_TICK: Duration = Duration::from_millis(250);
/// How often the calendar feeds are downloaded again.
const CALENDAR_REFRESH: Duration = Duration::from_secs(15 * 60);
/// How often the card re-picks its events, so one that ended drops off.
const CALENDAR_RECHECK: Duration = Duration::from_secs(30);
/// Give up on a feed after this long.
const FEED_TIMEOUT: Duration = Duration::from_secs(20);
/// Stats are sampled this often while their tab is showing.
const STATS_INTERVAL: Duration = Duration::from_secs(1);

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|e| e.into_inner())
}

/// A preset button on the timer card.
#[derive(Serialize)]
struct PresetView {
    name: String,
    label: String,
    seconds: u32,
    /// The length, when the button shows a name instead.
    tooltip: Option<String>,
}

impl From<&TimerPreset> for PresetView {
    fn from(preset: &TimerPreset) -> Self {
        Self {
            name: preset.name.clone(),
            label: preset.label(),
            seconds: preset.seconds,
            tooltip: (!preset.name.is_empty()).then(|| duration::describe(preset.duration())),
        }
    }
}

/// The `timer` event: the card as it is now, and the buttons for it.
#[derive(Serialize)]
struct TimerPayload {
    #[serde(flatten)]
    view: TimerView,
    presets: Vec<PresetView>,
}

/// The `calendar` event.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CalendarPayload {
    /// How many feeds the user has set up.
    feeds: usize,
    /// How many of them failed to load last time.
    failed: usize,
    rows: Vec<CalendarRow>,
}

/// The `stats` event.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StatsPayload {
    #[serde(flatten)]
    view: StatsView,
    /// False on a machine whose GPU load cannot be read: the card is hidden.
    gpu_present: bool,
}

#[derive(Default)]
struct CalendarShared {
    feeds: Vec<String>,
    snapshot: CalendarSnapshot,
    refresh_requested: bool,
}

/// The stats thread: set while it should keep going.
struct StatsRun {
    running: Arc<AtomicBool>,
    thread: Thread,
}

struct Inner {
    app: AppHandle,
    hub: Arc<ActivityHub>,
    timer: Mutex<TimerController>,
    timer_wake: Condvar,
    presets: Mutex<Vec<TimerPreset>>,
    calendar: Mutex<CalendarShared>,
    calendar_wake: Condvar,
    sampler: Mutex<Sampler>,
    stats: Mutex<Option<StatsRun>>,
}

/// The widgets' shared state, registered with Tauri.
pub struct Widgets {
    inner: Arc<Inner>,
}

impl Widgets {
    /// Starts the timer and calendar threads and follows the settings. Returns at once.
    pub fn start(app: AppHandle, hub: Arc<ActivityHub>, settings: &AppSettings) -> Widgets {
        let inner = Arc::new(Inner {
            app: app.clone(),
            hub,
            timer: Mutex::new(TimerController::new()),
            timer_wake: Condvar::new(),
            presets: Mutex::new(timer_preset::from_lines(&settings.timer_presets)),
            calendar: Mutex::new(CalendarShared { feeds: settings.calendar_feeds.clone(), refresh_requested: true, ..CalendarShared::default() }),
            calendar_wake: Condvar::new(),
            sampler: Mutex::new(Sampler::new()),
            stats: Mutex::new(None),
        });

        spawn("timer", {
            let inner = inner.clone();
            move || inner.run_timer()
        });
        spawn("calendar", {
            let inner = inner.clone();
            move || inner.run_calendar()
        });

        let follower = inner.clone();
        app.listen("settings", move |event| {
            #[derive(Deserialize)]
            struct Changed {
                settings: AppSettings,
            }
            match serde_json::from_str::<Changed>(event.payload()) {
                Ok(changed) => follower.apply_settings(&changed.settings),
                Err(e) => log::warn!("could not read the settings event: {e}"),
            }
        });
        Widgets { inner }
    }
}

fn spawn(name: &str, body: impl FnOnce() + Send + 'static) {
    if let Err(e) = thread::Builder::new().name(format!("widgets-{name}")).spawn(body) {
        log::warn!("could not start the {name} thread: {e}");
    }
}

impl Inner {
    fn apply_settings(&self, settings: &AppSettings) {
        *lock(&self.presets) = timer_preset::from_lines(&settings.timer_presets);
        self.emit_timer(&lock(&self.timer).view(Instant::now()));

        let mut calendar = lock(&self.calendar);
        if calendar.feeds != settings.calendar_feeds {
            calendar.feeds.clone_from(&settings.calendar_feeds);
            calendar.refresh_requested = true;
            self.calendar_wake.notify_one();
        }
    }

    // Timer ----------------------------------------------------------------------------------

    fn timer_payload(&self, view: TimerView) -> TimerPayload {
        TimerPayload { view, presets: lock(&self.presets).iter().map(PresetView::from).collect() }
    }

    fn emit_timer(&self, view: &TimerView) {
        if let Err(e) = self.app.emit("timer", self.timer_payload(view.clone())) {
            log::warn!("could not emit timer: {e}");
        }
    }

    /// Puts a tick's outcome on screen: the pill, the sound and the card.
    fn publish_timer(&self, update: TimerUpdate) {
        match update.activity {
            Some(activity) => self.hub.publish(activity),
            None => self.hub.remove(TIMER_ACTIVITY_ID),
        }
        if let Some(notice) = update.notice {
            self.hub.publish(notice);
        }
        if update.chime {
            sound::chime();
        }
        self.emit_timer(&update.view);
    }

    /// Changes the timer, shows the result at once and wakes the tick thread.
    fn with_timer<R>(&self, change: impl FnOnce(&mut TimerController, Instant) -> R) -> R {
        let now = Instant::now();
        let mut timer = lock(&self.timer);
        let result = change(&mut timer, now);
        if let Some(update) = timer.tick(now) {
            self.publish_timer(update);
        }
        self.timer_wake.notify_all();
        result
    }

    /// Ticks a few times a second while a timer is active, and sleeps when none is.
    fn run_timer(&self) {
        let mut timer = lock(&self.timer);
        loop {
            if let Some(update) = timer.tick(Instant::now()) {
                self.publish_timer(update);
            }
            timer = if timer.is_active(Instant::now()) {
                self.timer_wake.wait_timeout(timer, TIMER_TICK).unwrap_or_else(|e| e.into_inner()).0
            } else {
                self.timer_wake.wait(timer).unwrap_or_else(|e| e.into_inner())
            };
        }
    }

    // Calendar -------------------------------------------------------------------------------

    fn calendar_payload(&self, shared: &CalendarShared) -> CalendarPayload {
        CalendarPayload {
            feeds: shared.feeds.len(),
            failed: shared.snapshot.failed_feeds,
            rows: shared.snapshot.rows(Local::now(), MAX_CALENDAR_ENTRIES),
        }
    }

    /// Downloads the feeds when asked and every quarter hour, and re-picks the card's events twice a minute.
    fn run_calendar(&self) {
        let agent = ureq::AgentBuilder::new().timeout(FEED_TIMEOUT).build();
        let mut last_fetch: Option<Instant> = None;
        let mut last_sent: Option<String> = None;
        let mut calendar = lock(&self.calendar);

        loop {
            let due = calendar.refresh_requested || last_fetch.is_none_or(|at| at.elapsed() >= CALENDAR_REFRESH);
            if due {
                calendar.refresh_requested = false;
                let feeds = calendar.feeds.clone();
                drop(calendar);
                let snapshot = CalendarSnapshot::load(&feeds, Local::now(), |url| fetch(&agent, url));
                calendar = lock(&self.calendar);
                // The feeds may have been edited while downloading; then the next lap fetches again.
                calendar.snapshot = snapshot;
                last_fetch = Some(Instant::now());
            }

            match serde_json::to_string(&self.calendar_payload(&calendar)) {
                Ok(json) if last_sent.as_deref() != Some(json.as_str()) => {
                    if let Err(e) = self.app.emit("calendar", self.calendar_payload(&calendar)) {
                        log::warn!("could not emit calendar: {e}");
                    }
                    last_sent = Some(json);
                }
                Ok(_) => {}
                Err(e) => log::warn!("could not serialise the calendar: {e}"),
            }

            if !calendar.refresh_requested {
                calendar = self.calendar_wake.wait_timeout(calendar, CALENDAR_RECHECK).unwrap_or_else(|e| e.into_inner()).0;
            }
        }
    }

    // Stats ----------------------------------------------------------------------------------

    fn set_stats_visible(self: &Arc<Self>, visible: bool) {
        let mut stats = lock(&self.stats);
        if !visible {
            if let Some(run) = stats.take() {
                run.running.store(false, Ordering::Release);
                run.thread.unpark();
            }
            return;
        }
        if stats.is_some() {
            return;
        }

        let running = Arc::new(AtomicBool::new(true));
        let inner = self.clone();
        let flag = running.clone();
        match thread::Builder::new().name("widgets-stats".to_owned()).spawn(move || inner.run_stats(&flag)) {
            Ok(handle) => *stats = Some(StatsRun { running, thread: handle.thread().clone() }),
            Err(e) => log::warn!("could not start the stats thread: {e}"),
        }
    }

    /// Samples once a second, starting at once, until `running` is cleared.
    fn run_stats(&self, running: &AtomicBool) {
        while running.load(Ordering::Acquire) {
            let payload = {
                let mut sampler = lock(&self.sampler);
                let view = sampler.sample().view();
                StatsPayload { view, gpu_present: sampler.has_gpu() }
            };
            if let Err(e) = self.app.emit("stats", payload) {
                log::warn!("could not emit stats: {e}");
            }
            thread::park_timeout(STATS_INTERVAL);
        }
    }
}

/// Downloads one feed.
fn fetch(agent: &ureq::Agent, url: &str) -> Result<String, String> {
    let response = agent.get(url).call().map_err(|e| e.to_string())?;
    response.into_string().map_err(|e| e.to_string())
}

// Commands ---------------------------------------------------------------------------------------

/// Starts a countdown, from `text` as the user typed it ("12", "1:30", "90s", "1h20m") or from
/// `seconds` (a preset, optionally with its `name`). False when the length cannot be used.
#[tauri::command]
pub fn timer_start(widgets: State<'_, Widgets>, text: Option<String>, seconds: Option<u32>, name: Option<String>) -> bool {
    let length = match (text, seconds) {
        (Some(text), _) => duration::parse(&text),
        (None, Some(seconds)) => {
            let preset = TimerPreset::new("", seconds);
            preset.is_valid().then(|| preset.duration())
        }
        (None, None) => None,
    };
    let Some(length) = length else {
        return false;
    };
    let name = name.unwrap_or_default();
    let name = name.trim();
    widgets.inner.with_timer(|timer, now| timer.start(now, name, length));
    true
}

/// Starts a Pomodoro cycle with the lengths from the settings.
#[tauri::command]
pub fn timer_pomodoro(widgets: State<'_, Widgets>, backend: State<'_, Backend>) {
    let durations = {
        let settings = backend.settings();
        PomodoroDurations::from_minutes(
            settings.pomodoro_focus_minutes,
            settings.pomodoro_short_break_minutes,
            settings.pomodoro_long_break_minutes,
        )
    };
    widgets.inner.with_timer(|timer, now| timer.start_pomodoro(now, durations));
}

#[tauri::command]
pub fn timer_pause(widgets: State<'_, Widgets>) {
    widgets.inner.with_timer(|timer, now| timer.pause(now));
}

#[tauri::command]
pub fn timer_resume(widgets: State<'_, Widgets>) {
    widgets.inner.with_timer(|timer, now| timer.resume(now));
}

/// Cancels the timer, or dismisses it once finished.
#[tauri::command]
pub fn timer_reset(widgets: State<'_, Widgets>) {
    widgets.inner.with_timer(|timer, _| timer.reset());
}

/// The timer card as it is now, for a page that has just loaded.
#[tauri::command]
pub fn timer_get(widgets: State<'_, Widgets>) -> serde_json::Value {
    let view = lock(&widgets.inner.timer).view(Instant::now());
    serde_json::to_value(widgets.inner.timer_payload(view)).unwrap_or_default()
}

/// Downloads the calendar feeds again, e.g. after they were edited.
#[tauri::command]
pub fn calendar_refresh(widgets: State<'_, Widgets>) {
    let mut calendar = lock(&widgets.inner.calendar);
    calendar.refresh_requested = true;
    widgets.inner.calendar_wake.notify_one();
}

/// The calendar card as it is now, for a page that has just loaded.
#[tauri::command]
pub fn calendar_get(widgets: State<'_, Widgets>) -> serde_json::Value {
    let calendar = lock(&widgets.inner.calendar);
    serde_json::to_value(widgets.inner.calendar_payload(&calendar)).unwrap_or_default()
}

/// Starts or stops sampling the system. The UI says so while the Stats tab is on screen.
#[tauri::command]
pub fn set_stats_visible(widgets: State<'_, Widgets>, visible: bool) {
    widgets.inner.set_stats_visible(visible);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preset_buttons_show_the_length_only_when_named() {
        let named = PresetView::from(&TimerPreset::new("Tea", 180));
        assert_eq!((named.label.as_str(), named.tooltip.as_deref()), ("Tea", Some("3m")));
        let bare = PresetView::from(&TimerPreset::new("", 900));
        assert_eq!((bare.label.as_str(), bare.tooltip), ("15m", None));
    }

    #[test]
    fn the_calendar_payload_names_the_fields_the_ui_reads() {
        let json = serde_json::to_value(CalendarPayload { feeds: 1, failed: 0, rows: Vec::new() }).expect("json");
        assert_eq!(json, serde_json::json!({ "feeds": 1, "failed": 0, "rows": [] }));
    }
}
