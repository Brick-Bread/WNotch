//! Everything the notch can show, and the bookkeeping of which activity is on top.

use std::collections::HashMap;
use std::sync::{Mutex, MutexGuard};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::glow::Glow;

/// Higher tiers win the pill. Ties go to the most recently published activity.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ActivityTier {
    /// Long-running background state: media playing, timer running, agent working.
    #[default]
    Ongoing = 0,
    /// Something wants the user: agent waiting for input, timer finished.
    Attention = 1,
    /// Short-lived HUD (volume, brightness, charging) that expires on its own.
    Transient = 2,
}

/// One thing the pill can show.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Activity {
    /// Stable per source, e.g. "media" or "hud.volume". Publishing the same id replaces it.
    pub id: String,
    pub tier: ActivityTier,
    pub title: String,
    pub detail: Option<String>,
    /// An emoji or short symbol shown before the title.
    pub glyph: Option<String>,
    /// Encoded image bytes (PNG or JPEG) shown in place of the glyph, e.g. album art.
    #[serde(skip)]
    pub image: Option<Vec<u8>>,
    /// 0..1, or `None` when the activity has no meaningful progress.
    pub progress: Option<f64>,
    /// Light around the notch while this activity is on top; `None` for none.
    pub glow: Option<Glow>,
    /// Only used by [`ActivityTier::Transient`]; defaults to [`DEFAULT_TRANSIENT_LIFETIME`].
    #[serde(skip)]
    pub lifetime: Option<Duration>,
}

/// How long a transient activity stays when it does not say.
pub const DEFAULT_TRANSIENT_LIFETIME: Duration = Duration::from_secs(2);

struct Entry {
    activity: Activity,
    sequence: u64,
    /// When a transient activity goes away; `None` for ones that stay until removed.
    expiry: Option<Instant>,
}

impl Entry {
    fn is_expired(&self, now: Instant) -> bool {
        self.expiry.is_some_and(|expiry| expiry <= now)
    }
}

#[derive(Default)]
struct State {
    entries: HashMap<String, Entry>,
    suppressed: Vec<String>,
    sequence: u64,
}

/// Owns everything the notch can show. Sources publish and remove activities; the shell reads
/// [`snapshot`](Self::snapshot) whenever something changed. Time is passed in, so the owner
/// decides when to wake up (see [`next_expiry`](Self::next_expiry)) and tests need no clock.
#[derive(Default)]
pub struct ActivityManager {
    state: Mutex<State>,
}

impl ActivityManager {
    pub fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        // The state is consistent between statements, so a panic elsewhere does not poison it.
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Turns sources off by id (the user's "show in the pill" settings). Suppressed ids are
    /// dropped on publish, and any that are currently showing are removed. Returns whether
    /// anything that was showing is gone.
    pub fn set_suppressed(&self, ids: &[String]) -> bool {
        let mut state = self.lock();
        state.suppressed = ids.to_vec();
        let mut removed = false;
        for id in ids {
            removed |= state.entries.remove(id).is_some();
        }
        removed
    }

    /// Shows `activity`, replacing any with the same id.
    pub fn publish(&self, activity: Activity, now: Instant) {
        let mut state = self.lock();
        if state.suppressed.contains(&activity.id) {
            return;
        }

        // An update keeps its place in line so a ticking progress value does not reorder the pill.
        let sequence = match state.entries.remove(&activity.id) {
            Some(previous) => previous.sequence,
            None => {
                state.sequence += 1;
                state.sequence
            }
        };

        let expiry = (activity.tier == ActivityTier::Transient)
            .then(|| now + activity.lifetime.unwrap_or(DEFAULT_TRANSIENT_LIFETIME));
        state.entries.insert(activity.id.clone(), Entry { activity, sequence, expiry });
    }

    /// Removes the activity with this id; true when there was one.
    pub fn remove(&self, id: &str) -> bool {
        self.lock().entries.remove(id).is_some()
    }

    /// What is showing at `now`: highest tier first, then most recently published first.
    pub fn snapshot(&self, now: Instant) -> Vec<Activity> {
        let state = self.lock();
        let mut live: Vec<&Entry> = state.entries.values().filter(|e| !e.is_expired(now)).collect();
        live.sort_by(|a, b| {
            b.activity
                .tier
                .cmp(&a.activity.tier)
                .then(b.sequence.cmp(&a.sequence))
        });
        live.into_iter().map(|e| e.activity.clone()).collect()
    }

    /// Drops what has expired by `now`; true when anything was dropped.
    pub fn prune(&self, now: Instant) -> bool {
        let mut state = self.lock();
        let before = state.entries.len();
        state.entries.retain(|_, e| !e.is_expired(now));
        state.entries.len() != before
    }

    /// When the next transient activity expires, so the owner knows when to call [`prune`](Self::prune).
    pub fn next_expiry(&self) -> Option<Instant> {
        self.lock().entries.values().filter_map(|e| e.expiry).min()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make(id: &str, tier: ActivityTier) -> Activity {
        Activity { id: id.into(), tier, title: id.into(), ..Activity::default() }
    }

    fn transient(id: &str, lifetime: Duration) -> Activity {
        Activity { lifetime: Some(lifetime), ..make(id, ActivityTier::Transient) }
    }

    fn ids(manager: &ActivityManager, now: Instant) -> Vec<String> {
        manager.snapshot(now).into_iter().map(|a| a.id).collect()
    }

    #[test]
    fn snapshot_orders_by_tier_then_most_recent() {
        let (manager, now) = (ActivityManager::new(), Instant::now());
        manager.publish(make("media", ActivityTier::Ongoing), now);
        manager.publish(make("agent", ActivityTier::Attention), now);
        manager.publish(make("timer", ActivityTier::Ongoing), now);
        manager.publish(make("volume", ActivityTier::Transient), now);

        assert_eq!(ids(&manager, now), ["volume", "agent", "timer", "media"]);
    }

    #[test]
    fn republishing_replaces_and_keeps_position() {
        let (manager, now) = (ActivityManager::new(), Instant::now());
        manager.publish(make("media", ActivityTier::Ongoing), now);
        manager.publish(make("timer", ActivityTier::Ongoing), now);
        manager.publish(Activity { title: "updated".into(), ..make("media", ActivityTier::Ongoing) }, now);

        let snapshot = manager.snapshot(now);
        assert_eq!(ids(&manager, now), ["timer", "media"]);
        assert_eq!(snapshot[1].title, "updated");
    }

    #[test]
    fn transient_expires_after_its_lifetime() {
        let (manager, start) = (ActivityManager::new(), Instant::now());
        manager.publish(transient("volume", Duration::from_secs(2)), start);

        assert_eq!(manager.snapshot(start + Duration::from_millis(1900)).len(), 1);
        assert!(manager.snapshot(start + Duration::from_millis(2100)).is_empty());
        assert_eq!(manager.next_expiry(), Some(start + Duration::from_secs(2)));
        assert!(!manager.prune(start + Duration::from_millis(1900)));
        assert!(manager.prune(start + Duration::from_millis(2100)));
        assert!(!manager.prune(start + Duration::from_millis(2100)));
        assert_eq!(manager.next_expiry(), None);
    }

    #[test]
    fn transient_without_a_lifetime_uses_the_default() {
        let (manager, start) = (ActivityManager::new(), Instant::now());
        manager.publish(make("volume", ActivityTier::Transient), start);

        assert_eq!(manager.snapshot(start + Duration::from_millis(1900)).len(), 1);
        assert!(manager.snapshot(start + Duration::from_millis(2100)).is_empty());
    }

    #[test]
    fn republishing_a_transient_restarts_its_lifetime() {
        let (manager, start) = (ActivityManager::new(), Instant::now());
        let lifetime = Duration::from_secs(2);
        manager.publish(transient("volume", lifetime), start);
        manager.publish(transient("volume", lifetime), start + Duration::from_millis(1500));

        assert_eq!(manager.snapshot(start + Duration::from_millis(3000)).len(), 1);
        assert!(manager.snapshot(start + Duration::from_millis(4000)).is_empty());
    }

    #[test]
    fn ongoing_activities_do_not_expire() {
        let (manager, start) = (ActivityManager::new(), Instant::now());
        manager.publish(make("media", ActivityTier::Ongoing), start);

        assert_eq!(manager.snapshot(start + Duration::from_secs(3600)).len(), 1);
        assert!(!manager.prune(start + Duration::from_secs(3600)));
    }

    #[test]
    fn suppressed_ids_are_removed_and_ignored() {
        let (manager, now) = (ActivityManager::new(), Instant::now());
        manager.publish(make("media", ActivityTier::Ongoing), now);
        manager.publish(make("timer", ActivityTier::Ongoing), now);

        assert!(manager.set_suppressed(&["media".to_string()]));
        manager.publish(make("media", ActivityTier::Ongoing), now);
        assert_eq!(ids(&manager, now), ["timer"]);

        assert!(!manager.set_suppressed(&[]));
        manager.publish(make("media", ActivityTier::Ongoing), now);
        assert_eq!(manager.snapshot(now).len(), 2);
    }

    #[test]
    fn remove_reports_whether_anything_was_removed() {
        let (manager, now) = (ActivityManager::new(), Instant::now());
        manager.publish(make("media", ActivityTier::Ongoing), now);

        assert!(manager.remove("media"));
        assert!(!manager.remove("media"));
        assert!(manager.snapshot(now).is_empty());
    }

    #[test]
    fn activities_serialise_for_the_ui() {
        let activity = Activity {
            glyph: Some("✳".into()),
            image: Some(vec![1, 2]),
            lifetime: Some(Duration::from_secs(1)),
            ..make("agent.1", ActivityTier::Attention)
        };
        let value = serde_json::to_value(&activity).unwrap();

        assert_eq!(value["tier"], "attention");
        assert_eq!(value["glyph"], "✳");
        assert!(value.get("image").is_none() && value.get("lifetime").is_none());
    }
}
