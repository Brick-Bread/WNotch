//! Watchers that turn system events (volume, brightness, power, Bluetooth, Caps Lock) into HUDs.
//!
//! Each watcher lives on its own thread and publishes into the [`ActivityHub`]. None of them
//! announces the state it finds at startup, and each one that cannot work on this machine (no
//! battery, no dimmable panel, no audio server, no Bluetooth radio) logs once and stops.

use std::sync::Arc;
use std::thread;
use std::time::Duration;

use notch_core::hud::{CapsLockHudTracker, PowerHudTracker};

use crate::activity_hub::ActivityHub;

#[cfg(target_os = "linux")]
mod linux;
#[cfg(windows)]
mod win;

#[cfg(target_os = "linux")]
use linux as platform;
#[cfg(windows)]
use win as platform;

/// How often the charger and battery are read.
const POWER_INTERVAL: Duration = Duration::from_secs(2);

/// One reading of the battery.
#[derive(Debug, Clone, Copy)]
struct PowerReading {
    has_battery: bool,
    plugged_in: bool,
    percent: i32,
}

/// Starts every watcher this machine supports. Returns at once.
pub fn start(hub: Arc<ActivityHub>) {
    #[cfg(any(windows, target_os = "linux"))]
    platform::start(&hub);
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = hub;
        log::info!("system HUDs are not available on this platform");
    }
}

/// Runs `body` on a named thread. A failure to spawn only costs that one HUD.
fn spawn(name: &str, body: impl FnOnce() + Send + 'static) {
    if let Err(e) = thread::Builder::new().name(format!("hud-{name}")).spawn(body) {
        log::warn!("could not start the {name} HUD: {e}");
    }
}

/// Reads the battery every [`POWER_INTERVAL`] and publishes plug/unplug and low-battery HUDs.
fn watch_power(hub: &ActivityHub, mut read: impl FnMut() -> Option<PowerReading>) {
    let mut tracker = PowerHudTracker::new();
    let mut warned = false;
    loop {
        match read() {
            Some(reading) => {
                if let Some(hud) = tracker.update(reading.has_battery, reading.plugged_in, reading.percent) {
                    hub.publish(hud);
                }
            }
            None if !warned => {
                log::info!("power HUD: no battery reading available");
                warned = true;
            }
            None => {}
        }
        thread::sleep(POWER_INTERVAL);
    }
}

/// Reads the Caps Lock state every `interval` and publishes a HUD when it flips.
fn watch_caps_lock(hub: &ActivityHub, interval: Duration, mut read: impl FnMut() -> Option<bool>) {
    let mut tracker = CapsLockHudTracker::new();
    loop {
        if let Some(on) = read() {
            if let Some(hud) = tracker.update(on) {
                hub.publish(hud);
            }
        }
        thread::sleep(interval);
    }
}
