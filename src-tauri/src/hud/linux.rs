//! The Linux watchers: `pactl` (PulseAudio and PipeWire) for volume and the default output,
//! `/sys/class/backlight` for brightness, `/sys/class/power_supply` for the charger,
//! `bluetoothctl` (BlueZ) for Bluetooth and `/sys/class/leds` for Caps Lock.
//!
//! Missing tools or sysfs entries only switch that one HUD off, with a single log line.

use std::fs;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use notch_core::hud::{self, bluetoothctl, pactl, BluetoothHudTracker};

use super::{spawn, watch_caps_lock, watch_power, PowerReading};
use crate::activity_hub::ActivityHub;

const CAPS_LOCK_INTERVAL: Duration = Duration::from_millis(150);
const BRIGHTNESS_INTERVAL: Duration = Duration::from_millis(200);
const BLUETOOTH_INTERVAL: Duration = Duration::from_secs(3);
/// How long the audio server is given to settle before volume is read after a burst of events.
const AUDIO_SETTLE: Duration = Duration::from_millis(20);
const AUDIO_RETRY: Duration = Duration::from_secs(5);

pub fn start(hub: &Arc<ActivityHub>) {
    let volume_hub = hub.clone();
    spawn("volume", move || watch_volume(&volume_hub));
    let brightness_hub = hub.clone();
    spawn("brightness", move || watch_brightness(&brightness_hub));
    let power_hub = hub.clone();
    spawn("power", move || watch_power(&power_hub, read_power));
    let bluetooth_hub = hub.clone();
    spawn("bluetooth", move || watch_bluetooth(&bluetooth_hub));
    let caps_hub = hub.clone();
    spawn("caps-lock", move || match caps_lock_led() {
        Some(led) => watch_caps_lock(&caps_hub, CAPS_LOCK_INTERVAL, move || read_flag(&led)),
        None => log::info!("Caps Lock HUD unavailable: no capslock LED in /sys/class/leds"),
    });
}

// Volume -------------------------------------------------------------------

/// The default output as `pactl` reports it.
#[derive(Debug, Clone, PartialEq)]
struct Sink {
    name: String,
    /// Whole percent of the loudest channel.
    percent: i32,
    muted: bool,
}

/// Runs `pactl` and returns what it printed, or `None` when it is missing or fails.
fn pactl_output(args: &[&str]) -> Option<String> {
    let output = Command::new("pactl").args(args).stderr(Stdio::null()).output().ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned())
}

fn read_sink() -> Option<Sink> {
    let name = pactl_output(&["get-default-sink"])?.trim().to_owned();
    let level = pactl::parse_volume(&pactl_output(&["get-sink-volume", &name])?)?;
    let muted = pactl::parse_mute(&pactl_output(&["get-sink-mute", &name])?)?;
    Some(Sink { name, percent: (level * 100.0).round() as i32, muted })
}

fn describe_sink(name: &str) -> String {
    pactl_output(&["list", "sinks"])
        .and_then(|list| pactl::sink_description(&list, name))
        .unwrap_or_else(|| name.to_owned())
}

/// Follows `pactl subscribe`, reconnecting when the audio server restarts.
fn watch_volume(hub: &ActivityHub) {
    let mut warned = false;
    loop {
        match subscribe() {
            Some(events) => {
                warned = false;
                follow_sink(hub, &events);
            }
            None if !warned => {
                log::info!("volume HUD unavailable: could not run `pactl subscribe`");
                warned = true;
            }
            None => {}
        }
        thread::sleep(AUDIO_RETRY);
    }
}

/// Starts `pactl subscribe`; the receiver yields every event line until the process ends.
fn subscribe() -> Option<mpsc::Receiver<pactl::Event>> {
    let mut child = Command::new("pactl")
        .arg("subscribe")
        .env("LC_ALL", "C")
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .ok()?;
    let stdout = child.stdout.take()?;
    let (sender, events) = mpsc::channel();
    thread::Builder::new()
        .name("hud-pactl".into())
        .spawn(move || {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                if let Some(event) = pactl::parse_event(&line) {
                    if sender.send(event).is_err() {
                        break;
                    }
                }
            }
            // Reaps the process so a dead audio server does not leave a zombie.
            let _ = child.kill();
            let _ = child.wait();
        })
        .ok()?;
    Some(events)
}

fn follow_sink(hub: &ActivityHub, events: &mpsc::Receiver<pactl::Event>) {
    let mut last = read_sink();
    while events.recv().is_ok() {
        // One key press makes several events; read once they have settled.
        thread::sleep(AUDIO_SETTLE);
        while events.try_recv().is_ok() {}

        let Some(now) = read_sink() else { continue };
        match &last {
            Some(before) if before.name != now.name => hub.publish(hud::audio_output(&describe_sink(&now.name))),
            Some(before) if *before != now => hub.publish(hud::volume(f64::from(now.percent) / 100.0, now.muted)),
            _ => {}
        }
        last = Some(now);
    }
}

// Brightness ---------------------------------------------------------------

/// The built-in panel's backlight, preferring the kind the firmware or hardware driver exposes.
fn backlight() -> Option<PathBuf> {
    let mut devices: Vec<PathBuf> = fs::read_dir("/sys/class/backlight").ok()?.filter_map(Result::ok).map(|e| e.path()).collect();
    devices.sort();
    let rank = |path: &PathBuf| match read_text(&path.join("type")).as_deref() {
        Some("firmware") => 0,
        Some("platform") => 1,
        _ => 2,
    };
    devices.into_iter().min_by_key(rank)
}

fn read_number(path: &Path) -> Option<f64> {
    read_text(path)?.parse().ok()
}

fn read_text(path: &Path) -> Option<String> {
    fs::read_to_string(path).ok().map(|text| text.trim().to_owned())
}

fn watch_brightness(hub: &ActivityHub) {
    let Some(device) = backlight() else {
        log::info!("brightness HUD unavailable: no backlight in /sys/class/backlight");
        return;
    };
    let level = || {
        let max = read_number(&device.join("max_brightness"))?;
        (max > 0.0).then(|| read_number(&device.join("brightness")).map(|value| value / max))?
    };
    let mut last = level();
    loop {
        thread::sleep(BRIGHTNESS_INTERVAL);
        let now = level();
        if now != last {
            if let Some(level) = now {
                hub.publish(hud::brightness(level));
            }
            last = now;
        }
    }
}

// Power --------------------------------------------------------------------

fn read_power() -> Option<PowerReading> {
    let mut battery: Option<(i32, String)> = None;
    let mut mains: Option<bool> = None;
    for entry in fs::read_dir("/sys/class/power_supply").ok()?.filter_map(Result::ok) {
        let path = entry.path();
        match read_text(&path.join("type")).as_deref() {
            // Batteries of wireless peripherals are not the computer's.
            Some("Battery") if read_text(&path.join("scope")).as_deref() != Some("Device") => {
                let capacity = read_number(&path.join("capacity"))?;
                let status = read_text(&path.join("status")).unwrap_or_default();
                battery.get_or_insert((capacity.round() as i32, status));
            }
            Some("Mains" | "USB" | "USB_C" | "USB_PD" | "USB_DCP" | "USB_CDP" | "Wireless") => {
                let online = read_number(&path.join("online")).is_some_and(|v| v > 0.0);
                mains = Some(mains.unwrap_or(false) || online);
            }
            _ => {}
        }
    }
    Some(match battery {
        None => PowerReading { has_battery: false, plugged_in: mains.unwrap_or(true), percent: 100 },
        Some((percent, status)) => PowerReading {
            has_battery: true,
            // Without a mains supply file the battery's own state says whether it is being fed.
            plugged_in: mains.unwrap_or(matches!(status.as_str(), "Charging" | "Full")),
            percent,
        },
    })
}

// Bluetooth ----------------------------------------------------------------

fn watch_bluetooth(hub: &ActivityHub) {
    let connected = || {
        let output = Command::new("bluetoothctl").args(["devices", "Connected"]).stderr(Stdio::null()).output().ok()?;
        output.status.success().then(|| bluetoothctl::parse_devices(&String::from_utf8_lossy(&output.stdout)))
    };
    let mut tracker = BluetoothHudTracker::new();
    let Some(first) = connected() else {
        log::info!("Bluetooth HUD unavailable: `bluetoothctl devices Connected` failed");
        return;
    };
    tracker.observe_connected(&first);
    loop {
        thread::sleep(BLUETOOTH_INTERVAL);
        if let Some(devices) = connected() {
            for activity in tracker.observe_connected(&devices) {
                hub.publish(activity);
            }
        }
    }
}

// Caps Lock ----------------------------------------------------------------

/// The keyboard LED that mirrors Caps Lock, e.g. `/sys/class/leds/input3::capslock/brightness`.
fn caps_lock_led() -> Option<PathBuf> {
    let mut leds: Vec<PathBuf> = fs::read_dir("/sys/class/leds")
        .ok()?
        .filter_map(Result::ok)
        .filter(|e| e.file_name().to_string_lossy().ends_with("::capslock"))
        .map(|e| e.path().join("brightness"))
        .collect();
    leds.sort();
    leds.into_iter().next()
}

fn read_flag(path: &Path) -> Option<bool> {
    read_number(path).map(|value| value > 0.0)
}
