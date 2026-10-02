//! The shared activity list and the thread that tells the UI when it changes.

use std::sync::{Condvar, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use notch_core::activity::{Activity, ActivityManager};
use tauri::{AppHandle, Emitter};

use crate::window::WindowController;

/// How long the emitter sleeps when nothing is due to expire.
const IDLE_WAIT: Duration = Duration::from_secs(3600);

/// An [`ActivityManager`] that wakes a thread whenever it changes.
#[derive(Default)]
pub struct ActivityHub {
    manager: ActivityManager,
    dirty: Mutex<bool>,
    changed: Condvar,
}

impl ActivityHub {
    pub fn new() -> Self {
        Self::default()
    }

    /// Shows or updates an activity.
    pub fn publish(&self, activity: Activity) {
        self.manager.publish(activity, Instant::now());
        self.wake();
    }

    /// Takes an activity away. Nothing happens when it is not there.
    pub fn remove(&self, id: &str) {
        if self.manager.remove(id) {
            self.wake();
        }
    }

    /// What the UI should show right now, most important first.
    pub fn snapshot(&self) -> Vec<Activity> {
        self.manager.snapshot(Instant::now())
    }

    fn wake(&self) {
        *self.dirty.lock().unwrap_or_else(|e| e.into_inner()) = true;
        self.changed.notify_one();
    }

    /// Starts the thread that emits `activities` whenever the list changes, including when a
    /// transient one expires, and tells the window whether anything is showing.
    pub fn spawn_emitter(self: &std::sync::Arc<Self>, app: AppHandle, window: WindowController) {
        let hub = self.clone();
        thread::spawn(move || {
            let mut last = String::new();
            loop {
                let now = Instant::now();
                hub.manager.prune(now);
                let activities = hub.manager.snapshot(now);
                match serde_json::to_string(&activities) {
                    Ok(json) if json != last => {
                        if let Err(e) = app.emit("activities", &activities) {
                            log::warn!("could not emit activities: {e}");
                        }
                        window.set_compact(!activities.is_empty());
                        last = json;
                    }
                    Ok(_) => {}
                    Err(e) => log::warn!("could not serialise activities: {e}"),
                }
                hub.sleep_until_due();
            }
        });
    }

    /// Blocks until something changes or the next transient activity expires.
    fn sleep_until_due(&self) {
        let timeout = self
            .manager
            .next_expiry()
            .map_or(IDLE_WAIT, |due| due.saturating_duration_since(Instant::now()));
        let mut dirty = self.dirty.lock().unwrap_or_else(|e| e.into_inner());
        if !*dirty {
            dirty = self.changed.wait_timeout(dirty, timeout).unwrap_or_else(|e| e.into_inner()).0;
        }
        *dirty = false;
    }
}
