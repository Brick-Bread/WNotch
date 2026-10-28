//! Ported from `src/Notch.Core/Shell/NotchMode.cs`.

use crate::activities::{Activity, ActivityTier};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum NotchMode {
    /// Plain pill, nothing to show.
    Idle,
    /// Wider pill showing the top ongoing or attention activity, or the row of glance activities.
    Compact,
    /// Briefly enlarged pill for a transient HUD.
    Peek,
    /// Full panel, opened by the user.
    Expanded,
}

/// `expanded`: the user has the notch open (hover or click). `activities` are ordered as the
/// activity manager's snapshot returns them.
pub fn resolve(expanded: bool, activities: &[Activity]) -> NotchMode {
    if expanded {
        return NotchMode::Expanded;
    }

    match activities.first() {
        None => NotchMode::Idle,
        Some(top) if top.tier == ActivityTier::Transient => NotchMode::Peek,
        Some(_) => NotchMode::Compact,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make(tier: ActivityTier) -> Activity {
        Activity::new(format!("{tier:?}"), tier, "x")
    }

    #[test]
    fn no_activities_is_idle() {
        assert_eq!(resolve(false, &[]), NotchMode::Idle);
    }

    #[test]
    fn top_activity_picks_the_mode() {
        for (tier, expected) in [
            (ActivityTier::Ongoing, NotchMode::Compact),
            (ActivityTier::Attention, NotchMode::Compact),
            (ActivityTier::Transient, NotchMode::Peek),
            (ActivityTier::Glance, NotchMode::Compact),
        ] {
            assert_eq!(resolve(false, &[make(tier)]), expected);
        }
    }

    #[test]
    fn expanded_wins_over_activities() {
        assert_eq!(resolve(true, &[make(ActivityTier::Transient)]), NotchMode::Expanded);
    }
}
