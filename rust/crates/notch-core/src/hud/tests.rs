use super::*;
use crate::activities::{ActivityTier, GlowColor, GlowPattern};

#[test]
fn muted_volume_shows_an_empty_bar() {
    let hud = activities::volume(0.8, true);

    assert_eq!(hud.title, "Muted");
    assert_eq!(hud.progress, Some(0.0));
    assert_eq!(hud.tier, ActivityTier::Transient);
}

#[test]
fn volume_is_clamped() {
    assert_eq!(activities::volume(1.4, false).progress, Some(1.0));
}

#[test]
fn first_power_reading_is_silent() {
    assert_eq!(PowerHudTracker::new().update(true, true, 80), None);
}

#[test]
fn plugging_and_unplugging_show_a_hud() {
    let mut tracker = PowerHudTracker::new();
    tracker.update(true, false, 80);

    assert_eq!(tracker.update(true, true, 80).map(|a| a.title).as_deref(), Some("Charging"));
    assert_eq!(tracker.update(true, true, 81), None);
    assert_eq!(tracker.update(true, false, 81).map(|a| a.title).as_deref(), Some("On battery"));
}

#[test]
fn low_battery_fires_once_per_mark() {
    let mut tracker = PowerHudTracker::new();
    tracker.update(true, false, 22);

    assert_eq!(tracker.update(true, false, 21), None);
    assert_eq!(tracker.update(true, false, 20).map(|a| a.title).as_deref(), Some("Low battery"));
    assert_eq!(tracker.update(true, false, 19), None);
    assert_eq!(tracker.update(true, false, 10).map(|a| a.title).as_deref(), Some("Low battery"));
}

#[test]
fn low_battery_is_not_reported_while_charging() {
    let mut tracker = PowerHudTracker::new();
    tracker.update(true, true, 21);

    assert_eq!(tracker.update(true, true, 19), None);
}

#[test]
fn machines_without_a_battery_never_report() {
    assert_eq!(PowerHudTracker::new().update(false, true, 100), None);
}

#[test]
fn caps_lock_that_was_already_on_at_start_is_silent() {
    assert_eq!(CapsLockHudTracker::new().update(true), None);
}

#[test]
fn switching_caps_lock_shows_a_hud_each_time() {
    let mut tracker = CapsLockHudTracker::new();
    tracker.update(false);

    assert_eq!(tracker.update(true).and_then(|a| a.detail).as_deref(), Some("On"));
    assert_eq!(tracker.update(true), None);

    let off = tracker.update(false).unwrap();
    assert_eq!(off.detail.as_deref(), Some("Off"));
    assert_eq!(off.id, CAPS_LOCK_ID);
    assert_eq!(off.tier, ActivityTier::Transient);
}

// From GlowTests.cs.
#[test]
fn hud_glows_match_their_meaning() {
    assert_eq!(activities::power(true, 50).glow.unwrap().color, GlowColor::GREEN);
    assert_eq!(activities::low_battery(8).glow.unwrap().pattern, GlowPattern::Pulse);
    assert!(
        activities::volume(1.0, false).glow.unwrap().strength > activities::volume(0.1, false).glow.unwrap().strength
    );
}
