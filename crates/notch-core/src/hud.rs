//! The transient activities shown for system events (volume, brightness, power, Bluetooth, Caps Lock)
//! and the trackers that decide which readings are worth one. Port of `Notch.Core/Hud`.
//!
//! Glyphs are plain Unicode (the C# app used Segoe Fluent code points); the UI draws a matching
//! icon for each of the `GLYPH_*` strings, see `ui/hud-icons.js`.

use std::collections::HashMap;
use std::time::Duration;

use crate::activity::{Activity, ActivityTier};
use crate::glow::{Glow, GlowColor, GlowPattern};

pub const VOLUME_ID: &str = "hud.volume";
pub const BRIGHTNESS_ID: &str = "hud.brightness";
pub const POWER_ID: &str = "hud.power";
pub const BLUETOOTH_ID: &str = "hud.bluetooth";
pub const AUDIO_OUTPUT_ID: &str = "hud.audio-output";
pub const CAPS_LOCK_ID: &str = "hud.caps-lock";

pub const GLYPH_VOLUME_MUTED: &str = "🔇";
pub const GLYPH_VOLUME_LOW: &str = "🔈";
pub const GLYPH_VOLUME_MEDIUM: &str = "🔉";
pub const GLYPH_VOLUME_HIGH: &str = "🔊";
pub const GLYPH_BRIGHTNESS: &str = "☀";
pub const GLYPH_CHARGING: &str = "⚡";
pub const GLYPH_ON_BATTERY: &str = "🔋";
pub const GLYPH_LOW_BATTERY: &str = "🪫";
pub const GLYPH_BLUETOOTH: &str = "ᛒ";
pub const GLYPH_CAPS_LOCK: &str = "⇪";
pub const GLYPH_AUDIO_OUTPUT: &str = "🎧";

const NOTICE_LIFETIME: Duration = Duration::from_millis(3500);

/// Battery percentages that get a warning when the charge drops to them.
const LOW_MARKS: [i32; 3] = [20, 10, 5];

fn flash(color: GlowColor, strength: f64) -> Option<Glow> {
    Some(Glow { color, pattern: GlowPattern::Flash, strength })
}

fn hud(id: &str, title: impl Into<String>, glyph: &str) -> Activity {
    Activity {
        id: id.to_owned(),
        tier: ActivityTier::Transient,
        title: title.into(),
        glyph: Some(glyph.to_owned()),
        ..Activity::default()
    }
}

/// The default output's volume; `level` is clamped to 0..1. A muted or silent output shows an empty bar.
pub fn volume(level: f64, muted: bool) -> Activity {
    let level = level.clamp(0.0, 1.0);
    let silent = muted || level <= 0.0;
    let glyph = if silent {
        GLYPH_VOLUME_MUTED
    } else if level < 0.34 {
        GLYPH_VOLUME_LOW
    } else if level < 0.67 {
        GLYPH_VOLUME_MEDIUM
    } else {
        GLYPH_VOLUME_HIGH
    };
    Activity {
        progress: Some(if silent { 0.0 } else { level }),
        glow: flash(GlowColor::WHITE, if silent { 0.35 } else { 0.4 + 0.6 * level }),
        ..hud(VOLUME_ID, if muted { "Muted" } else { "Volume" }, glyph)
    }
}

/// The built-in panel's brightness; `level` is clamped to 0..1.
pub fn brightness(level: f64) -> Activity {
    let level = level.clamp(0.0, 1.0);
    Activity {
        progress: Some(level),
        glow: flash(GlowColor::YELLOW, 0.4 + 0.6 * level),
        ..hud(BRIGHTNESS_ID, "Brightness", GLYPH_BRIGHTNESS)
    }
}

/// The charger was connected or removed.
pub fn power(plugged_in: bool, percent: i32) -> Activity {
    Activity {
        detail: Some(format!("{percent}%")),
        glow: if plugged_in { flash(GlowColor::GREEN, 1.0) } else { flash(GlowColor::WHITE, 0.6) },
        lifetime: Some(NOTICE_LIFETIME),
        ..hud(
            POWER_ID,
            if plugged_in { "Charging" } else { "On battery" },
            if plugged_in { GLYPH_CHARGING } else { GLYPH_ON_BATTERY },
        )
    }
}

/// The battery fell to one of the low marks.
pub fn low_battery(percent: i32) -> Activity {
    Activity {
        detail: Some(format!("{percent}%")),
        glow: Some(Glow { color: GlowColor::RED, pattern: GlowPattern::Pulse, strength: 1.0 }),
        lifetime: Some(Duration::from_secs(6)),
        ..hud(POWER_ID, "Low battery", GLYPH_LOW_BATTERY)
    }
}

/// A paired Bluetooth device connected or disconnected.
pub fn bluetooth(device_name: &str, connected: bool) -> Activity {
    Activity {
        detail: Some(if connected { "Connected" } else { "Disconnected" }.to_owned()),
        glow: flash(GlowColor::BLUE, if connected { 1.0 } else { 0.5 }),
        lifetime: Some(NOTICE_LIFETIME),
        ..hud(BLUETOOTH_ID, device_name, GLYPH_BLUETOOTH)
    }
}

/// Caps Lock was switched on or off.
pub fn caps_lock(on: bool) -> Activity {
    Activity {
        detail: Some(if on { "On" } else { "Off" }.to_owned()),
        glow: flash(GlowColor::WHITE, if on { 0.8 } else { 0.4 }),
        lifetime: Some(Duration::from_secs(2)),
        ..hud(CAPS_LOCK_ID, "Caps Lock", GLYPH_CAPS_LOCK)
    }
}

/// A different audio output became the default.
pub fn audio_output(device_name: &str) -> Activity {
    Activity {
        detail: Some("Output".to_owned()),
        glow: flash(GlowColor::WHITE, 0.7),
        lifetime: Some(NOTICE_LIFETIME),
        ..hud(AUDIO_OUTPUT_ID, device_name, GLYPH_AUDIO_OUTPUT)
    }
}

/// Decides which power readings are worth a HUD: plugging in, unplugging, and crossing a low-battery mark.
#[derive(Debug)]
pub struct PowerHudTracker {
    plugged_in: Option<bool>,
    percent: i32,
}

impl Default for PowerHudTracker {
    fn default() -> Self {
        Self { plugged_in: None, percent: 100 }
    }
}

impl PowerHudTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Feed every reading; returns the HUD to show, or `None` when nothing notable changed.
    /// The first reading only establishes the baseline.
    pub fn update(&mut self, has_battery: bool, plugged_in: bool, percent: i32) -> Option<Activity> {
        if !has_battery {
            self.plugged_in = None;
            return None;
        }

        let was_plugged_in = self.plugged_in;
        let previous = self.percent;
        self.plugged_in = Some(plugged_in);
        self.percent = percent;

        let was_plugged_in = was_plugged_in?;
        if was_plugged_in != plugged_in {
            return Some(power(plugged_in, percent));
        }
        if !plugged_in && LOW_MARKS.iter().any(|&mark| previous > mark && percent <= mark) {
            return Some(low_battery(percent));
        }
        None
    }
}

/// Turns readings of the Caps Lock key into a HUD each time it is switched on or off.
#[derive(Debug, Default)]
pub struct CapsLockHudTracker {
    on: Option<bool>,
}

impl CapsLockHudTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Feed every reading; returns the HUD to show, or `None` when the key is as it was.
    /// The first reading only establishes the baseline.
    pub fn update(&mut self, on: bool) -> Option<Activity> {
        let was = self.on.replace(on);
        (was.is_some_and(|was| was != on)).then(|| caps_lock(on))
    }
}

/// Remembers which paired Bluetooth devices are connected and announces changes.
#[derive(Debug, Default)]
pub struct BluetoothHudTracker {
    devices: HashMap<String, (String, bool)>,
    baselined: bool,
}

impl BluetoothHudTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// A device appeared in the watcher's initial enumeration (or was paired): only records its state.
    pub fn added(&mut self, id: &str, name: &str, connected: bool) {
        self.devices.insert(id.to_owned(), (name.to_owned(), connected));
    }

    /// A device's connection state was reported; returns the HUD when it really changed.
    /// Unknown devices and devices without a name are ignored.
    pub fn updated(&mut self, id: &str, connected: bool) -> Option<Activity> {
        let (name, was) = self.devices.get_mut(id)?;
        if *was == connected {
            return None;
        }
        *was = connected;
        (!name.trim().is_empty()).then(|| bluetooth(name, connected))
    }

    /// A device was unpaired.
    pub fn removed(&mut self, id: &str) {
        self.devices.remove(id);
    }

    /// For sources that can only list the devices connected right now as `(id, name)`. The first
    /// call is the baseline; later calls announce each device that joined or left the list.
    pub fn observe_connected(&mut self, connected: &[(String, String)]) -> Vec<Activity> {
        let mut huds = Vec::new();
        if self.baselined {
            for (id, name) in connected {
                match self.devices.get(id) {
                    Some((_, true)) => {}
                    _ => huds.extend(self.announce(id, name, true)),
                }
            }
            let gone: Vec<String> = self
                .devices
                .iter()
                .filter(|(id, (_, on))| *on && !connected.iter().any(|(c, _)| c == *id))
                .map(|(id, _)| id.clone())
                .collect();
            for id in gone {
                let name = self.devices.get(&id).map(|(name, _)| name.clone()).unwrap_or_default();
                huds.extend(self.announce(&id, &name, false));
            }
        } else {
            for (id, name) in connected {
                self.added(id, name, true);
            }
            self.baselined = true;
        }
        huds
    }

    fn announce(&mut self, id: &str, name: &str, connected: bool) -> Option<Activity> {
        self.devices.insert(id.to_owned(), (name.to_owned(), connected));
        (!name.trim().is_empty()).then(|| bluetooth(name, connected))
    }
}

/// Parsers for the text `pactl` prints, used on Linux where volume comes from PulseAudio/PipeWire.
pub mod pactl {
    /// What a line of `pactl subscribe` is about.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum Event {
        /// A sink changed (volume, mute) or appeared.
        Sink,
        /// The server changed, e.g. the default sink.
        Server,
    }

    /// `Event 'change' on sink #52` => [`Event::Sink`]; `Event 'change' on server` => [`Event::Server`]; anything else `None`.
    pub fn parse_event(line: &str) -> Option<Event> {
        let rest = line.trim().strip_prefix("Event '")?;
        let (kind, rest) = rest.split_once("' on ")?;
        match (kind, rest.split_whitespace().next()?) {
            ("change" | "new", "sink") => Some(Event::Sink),
            ("change", "server") => Some(Event::Server),
            _ => None,
        }
    }

    /// The loudest channel of `Volume: front-left: 42000 /  64% / -11.0 dB,   front-right: ...` as 0..1.
    pub fn parse_volume(text: &str) -> Option<f64> {
        let percent = text
            .split(|c: char| c == ',' || c == '\n')
            .filter_map(|channel| {
                let before = channel.split('%').next().filter(|_| channel.contains('%'))?;
                before.rsplit(|c: char| !c.is_ascii_digit()).next()?.parse::<f64>().ok()
            })
            .fold(None, |best: Option<f64>, p| Some(best.map_or(p, |b| b.max(p))))?;
        Some(percent / 100.0)
    }

    /// `Mute: yes` / `Mute: no`.
    pub fn parse_mute(text: &str) -> Option<bool> {
        match text.trim().strip_prefix("Mute:")?.trim() {
            "yes" => Some(true),
            "no" => Some(false),
            _ => None,
        }
    }

    /// The `Description:` of the sink called `name` in the output of `pactl list sinks`.
    pub fn sink_description(list: &str, name: &str) -> Option<String> {
        let mut in_sink = false;
        for line in list.lines() {
            let line = line.trim();
            if let Some(found) = line.strip_prefix("Name:") {
                in_sink = found.trim() == name;
            } else if in_sink {
                if let Some(description) = line.strip_prefix("Description:") {
                    return Some(description.trim().to_owned());
                }
            }
        }
        None
    }
}

/// Parser for the text `bluetoothctl` prints, used on Linux where Bluetooth comes from BlueZ.
pub mod bluetoothctl {
    /// The `(address, name)` of each `Device AA:BB:CC:DD:EE:FF Name` line of `bluetoothctl devices Connected`.
    /// A device without a name is listed under its address.
    pub fn parse_devices(output: &str) -> Vec<(String, String)> {
        output
            .lines()
            .filter_map(|line| {
                let rest = line.trim().strip_prefix("Device ")?;
                let (address, name) = rest.split_once(' ').unwrap_or((rest, ""));
                let name = name.trim();
                Some((address.to_owned(), if name.is_empty() { address } else { name }.to_owned()))
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn power_reading(tracker: &mut PowerHudTracker, plugged_in: bool, percent: i32) -> Option<Activity> {
        tracker.update(true, plugged_in, percent)
    }

    fn title(activity: Option<Activity>) -> Option<String> {
        activity.map(|a| a.title)
    }

    #[test]
    fn muted_volume_shows_an_empty_bar() {
        let hud = volume(0.8, true);
        assert_eq!(hud.title, "Muted");
        assert_eq!(hud.progress, Some(0.0));
        assert_eq!(hud.tier, ActivityTier::Transient);
        assert_eq!(hud.glyph.as_deref(), Some(GLYPH_VOLUME_MUTED));
    }

    #[test]
    fn volume_is_clamped() {
        assert_eq!(volume(1.4, false).progress, Some(1.0));
        assert_eq!(volume(-1.0, false).progress, Some(0.0));
    }

    #[test]
    fn volume_glyph_follows_the_level() {
        let glyph = |level| volume(level, false).glyph.unwrap();
        assert_eq!(glyph(0.0), GLYPH_VOLUME_MUTED);
        assert_eq!(glyph(0.2), GLYPH_VOLUME_LOW);
        assert_eq!(glyph(0.5), GLYPH_VOLUME_MEDIUM);
        assert_eq!(glyph(0.9), GLYPH_VOLUME_HIGH);
    }

    #[test]
    fn huds_keep_their_ids_and_lifetimes() {
        assert_eq!(brightness(0.5).id, BRIGHTNESS_ID);
        assert_eq!(audio_output("Speakers").id, AUDIO_OUTPUT_ID);
        assert_eq!(low_battery(9).lifetime, Some(Duration::from_secs(6)));
        assert_eq!(caps_lock(true).lifetime, Some(Duration::from_secs(2)));
        assert_eq!(bluetooth("Buds", false).detail.as_deref(), Some("Disconnected"));
        assert_eq!(low_battery(9).glow.map(|g| g.pattern), Some(GlowPattern::Pulse));
    }

    #[test]
    fn first_power_reading_is_silent() {
        assert!(power_reading(&mut PowerHudTracker::new(), true, 80).is_none());
    }

    #[test]
    fn plugging_and_unplugging_show_a_hud() {
        let mut tracker = PowerHudTracker::new();
        power_reading(&mut tracker, false, 80);

        assert_eq!(title(power_reading(&mut tracker, true, 80)).as_deref(), Some("Charging"));
        assert!(power_reading(&mut tracker, true, 81).is_none());
        assert_eq!(title(power_reading(&mut tracker, false, 81)).as_deref(), Some("On battery"));
    }

    #[test]
    fn low_battery_fires_once_per_mark() {
        let mut tracker = PowerHudTracker::new();
        power_reading(&mut tracker, false, 22);

        assert!(power_reading(&mut tracker, false, 21).is_none());
        assert_eq!(title(power_reading(&mut tracker, false, 20)).as_deref(), Some("Low battery"));
        assert!(power_reading(&mut tracker, false, 19).is_none());
        assert_eq!(title(power_reading(&mut tracker, false, 10)).as_deref(), Some("Low battery"));
    }

    #[test]
    fn low_battery_is_not_reported_while_charging() {
        let mut tracker = PowerHudTracker::new();
        power_reading(&mut tracker, true, 21);
        assert!(power_reading(&mut tracker, true, 19).is_none());
    }

    #[test]
    fn machines_without_a_battery_never_report() {
        assert!(PowerHudTracker::new().update(false, true, 100).is_none());
    }

    #[test]
    fn caps_lock_that_was_already_on_at_start_is_silent() {
        assert!(CapsLockHudTracker::new().update(true).is_none());
    }

    #[test]
    fn switching_caps_lock_shows_a_hud_each_time() {
        let mut tracker = CapsLockHudTracker::new();
        tracker.update(false);

        assert_eq!(tracker.update(true).and_then(|a| a.detail).as_deref(), Some("On"));
        assert!(tracker.update(true).is_none());

        let off = tracker.update(false);
        assert_eq!(off.as_ref().and_then(|a| a.detail.as_deref()), Some("Off"));
        assert_eq!(off.as_ref().map(|a| a.id.as_str()), Some(CAPS_LOCK_ID));
        assert_eq!(off.map(|a| a.tier), Some(ActivityTier::Transient));
    }

    #[test]
    fn bluetooth_announces_only_real_changes_of_known_devices() {
        let mut tracker = BluetoothHudTracker::new();
        tracker.added("a", "Buds", false);

        assert!(tracker.updated("a", false).is_none());
        assert!(tracker.updated("unknown", true).is_none());
        assert_eq!(tracker.updated("a", true).map(|a| a.title).as_deref(), Some("Buds"));
        assert_eq!(
            tracker.updated("a", false).and_then(|a| a.detail).as_deref(),
            Some("Disconnected")
        );
        tracker.removed("a");
        assert!(tracker.updated("a", true).is_none());
    }

    #[test]
    fn nameless_bluetooth_devices_are_silent() {
        let mut tracker = BluetoothHudTracker::new();
        tracker.added("a", "  ", false);
        assert!(tracker.updated("a", true).is_none());
    }

    #[test]
    fn connected_lists_are_diffed_after_the_baseline() {
        let device = |id: &str, name: &str| (id.to_owned(), name.to_owned());
        let mut tracker = BluetoothHudTracker::new();

        assert!(tracker.observe_connected(&[device("a", "Buds")]).is_empty());
        assert!(tracker.observe_connected(&[device("a", "Buds")]).is_empty());

        let joined = tracker.observe_connected(&[device("a", "Buds"), device("b", "Mouse")]);
        assert_eq!(joined.len(), 1);
        assert_eq!(joined[0].title, "Mouse");
        assert_eq!(joined[0].detail.as_deref(), Some("Connected"));

        let left = tracker.observe_connected(&[device("b", "Mouse")]);
        assert_eq!(left.len(), 1);
        assert_eq!(left[0].title, "Buds");
        assert_eq!(left[0].detail.as_deref(), Some("Disconnected"));
    }

    #[test]
    fn bluetoothctl_devices_parse() {
        let output = "Device 00:11:22:33:44:55 WH-1000XM4\nDevice AA:BB:CC:DD:EE:FF\nnoise\n";
        assert_eq!(
            bluetoothctl::parse_devices(output),
            [
                ("00:11:22:33:44:55".to_owned(), "WH-1000XM4".to_owned()),
                ("AA:BB:CC:DD:EE:FF".to_owned(), "AA:BB:CC:DD:EE:FF".to_owned()),
            ]
        );
    }

    #[test]
    fn pactl_events_are_recognised() {
        use pactl::Event;
        assert_eq!(pactl::parse_event("Event 'change' on sink #52"), Some(Event::Sink));
        assert_eq!(pactl::parse_event("Event 'change' on server"), Some(Event::Server));
        assert_eq!(pactl::parse_event("Event 'change' on source #3"), None);
        assert_eq!(pactl::parse_event("Event 'remove' on sink #52"), None);
        assert_eq!(pactl::parse_event("garbage"), None);
    }

    #[test]
    fn pactl_volume_takes_the_loudest_channel() {
        let text = "Volume: front-left: 42000 /  64% / -11.0 dB,   front-right: 30000 /  46% / -20.0 dB\n        balance -0.28";
        assert_eq!(pactl::parse_volume(text), Some(0.64));
        assert_eq!(pactl::parse_volume("Volume: mono: 65536 / 100% / 0.00 dB"), Some(1.0));
        assert_eq!(pactl::parse_volume("nothing"), None);
    }

    #[test]
    fn pactl_mute_and_descriptions_parse() {
        assert_eq!(pactl::parse_mute("Mute: yes\n"), Some(true));
        assert_eq!(pactl::parse_mute("Mute: no"), Some(false));
        assert_eq!(pactl::parse_mute("oops"), None);

        let list = "Sink #1\n\tName: alsa_output.a\n\tDescription: Speakers\nSink #2\n\tName: alsa_output.b\n\tDescription: Headphones\n";
        assert_eq!(pactl::sink_description(list, "alsa_output.b").as_deref(), Some("Headphones"));
        assert_eq!(pactl::sink_description(list, "missing"), None);
    }
}
