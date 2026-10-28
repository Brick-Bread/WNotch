//! Builds the transient activities shown for system events. Ported from
//! `src/Notch.Core/Hud/HudActivities.cs`.

use std::time::Duration;

use super::{AUDIO_OUTPUT_ID, BLUETOOTH_ID, BRIGHTNESS_ID, CAPS_LOCK_ID, POWER_ID, VOLUME_ID};
use crate::activities::{Activity, ActivityTier, Glow, GlowColor, GlowPattern};

const NOTICE_LIFETIME: Duration = Duration::from_millis(3500);

pub fn volume(level: f64, muted: bool) -> Activity {
    let level = level.clamp(0.0, 1.0);
    let silent = muted || level <= 0.0;

    let glyph = if silent {
        "\u{E74F}"
    } else if level < 0.34 {
        "\u{E993}"
    } else if level < 0.67 {
        "\u{E994}"
    } else {
        "\u{E995}"
    };

    Activity {
        progress: Some(if silent { 0.0 } else { level }),
        glow: Some(Glow::with_strength(
            GlowColor::WHITE,
            GlowPattern::Flash,
            if silent { 0.35 } else { 0.4 + 0.6 * level },
        )),
        glyph: Some(glyph.into()),
        ..Activity::new(VOLUME_ID, ActivityTier::Transient, if muted { "Muted" } else { "Volume" })
    }
}

pub fn brightness(level: f64) -> Activity {
    Activity {
        progress: Some(level.clamp(0.0, 1.0)),
        glow: Some(Glow::with_strength(GlowColor::YELLOW, GlowPattern::Flash, 0.4 + 0.6 * level.clamp(0.0, 1.0))),
        glyph: Some("\u{E706}".into()),
        ..Activity::new(BRIGHTNESS_ID, ActivityTier::Transient, "Brightness")
    }
}

pub fn power(plugged_in: bool, percent: i32) -> Activity {
    Activity {
        glow: Some(if plugged_in {
            Glow::new(GlowColor::GREEN, GlowPattern::Flash)
        } else {
            Glow::with_strength(GlowColor::WHITE, GlowPattern::Flash, 0.6)
        }),
        detail: Some(format!("{percent}%")),
        glyph: Some(if plugged_in { "\u{EA93}" } else { "\u{E83F}" }.into()),
        lifetime: Some(NOTICE_LIFETIME),
        ..Activity::new(POWER_ID, ActivityTier::Transient, if plugged_in { "Charging" } else { "On battery" })
    }
}

pub fn low_battery(percent: i32) -> Activity {
    Activity {
        glow: Some(Glow::new(GlowColor::RED, GlowPattern::Pulse)),
        detail: Some(format!("{percent}%")),
        glyph: Some("\u{E851}".into()),
        lifetime: Some(Duration::from_secs(6)),
        ..Activity::new(POWER_ID, ActivityTier::Transient, "Low battery")
    }
}

pub fn bluetooth(device_name: &str, connected: bool) -> Activity {
    Activity {
        detail: Some(if connected { "Connected" } else { "Disconnected" }.into()),
        glow: Some(Glow::with_strength(GlowColor::BLUE, GlowPattern::Flash, if connected { 1.0 } else { 0.5 })),
        glyph: Some("\u{E702}".into()),
        lifetime: Some(NOTICE_LIFETIME),
        ..Activity::new(BLUETOOTH_ID, ActivityTier::Transient, device_name)
    }
}

pub fn caps_lock(on: bool) -> Activity {
    Activity {
        detail: Some(if on { "On" } else { "Off" }.into()),
        glow: Some(Glow::with_strength(GlowColor::WHITE, GlowPattern::Flash, if on { 0.8 } else { 0.4 })),
        glyph: Some("\u{E752}".into()),
        lifetime: Some(Duration::from_secs(2)),
        ..Activity::new(CAPS_LOCK_ID, ActivityTier::Transient, "Caps Lock")
    }
}

pub fn audio_output(device_name: &str) -> Activity {
    Activity {
        detail: Some("Output".into()),
        glow: Some(Glow::with_strength(GlowColor::WHITE, GlowPattern::Flash, 0.7)),
        glyph: Some("\u{E7F5}".into()),
        lifetime: Some(NOTICE_LIFETIME),
        ..Activity::new(AUDIO_OUTPUT_ID, ActivityTier::Transient, device_name)
    }
}
