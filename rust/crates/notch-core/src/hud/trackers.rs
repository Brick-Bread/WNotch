//! Decide which readings are worth a HUD. Ported from `src/Notch.Core/Hud/PowerHudTracker.cs` and
//! `src/Notch.Core/Hud/CapsLockHudTracker.cs`.

use super::activities;
use crate::activities::Activity;

const LOW_MARKS: [i32; 3] = [20, 10, 5];

/// Decides which power readings are worth a HUD: plugging in, unplugging, and crossing a
/// low-battery mark.
#[derive(Debug, Clone)]
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
    pub fn update(&mut self, has_battery: bool, plugged_in: bool, percent: i32) -> Option<Activity> {
        if !has_battery {
            self.plugged_in = None;
            return None;
        }

        let was_plugged_in = self.plugged_in.replace(plugged_in);
        let previous_percent = std::mem::replace(&mut self.percent, percent);

        // The first reading only establishes the baseline.
        let was_plugged_in = was_plugged_in?;

        if was_plugged_in != plugged_in {
            return Some(activities::power(plugged_in, percent));
        }

        if !plugged_in && LOW_MARKS.iter().any(|&mark| previous_percent > mark && percent <= mark) {
            return Some(activities::low_battery(percent));
        }

        None
    }
}

/// Turns readings of the Caps Lock key into a HUD each time it is switched on or off.
#[derive(Debug, Clone, Default)]
pub struct CapsLockHudTracker {
    on: Option<bool>,
}

impl CapsLockHudTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Feed every reading; returns the HUD to show, or `None` when the key is as it was.
    pub fn update(&mut self, on: bool) -> Option<Activity> {
        let was = self.on.replace(on);

        // The first reading only establishes the baseline.
        match was {
            Some(was) if was != on => Some(activities::caps_lock(on)),
            _ => None,
        }
    }
}
