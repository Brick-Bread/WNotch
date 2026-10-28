//! Ported from `src/Notch.Core/Activities/ActivityManager.cs`.

use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

use super::{Activity, ActivityTier};

pub const DEFAULT_TRANSIENT_LIFETIME: Duration = Duration::from_secs(2);

/// Owns everything the notch can show. Sources publish and remove activities; the shell reads
/// [`Self::snapshot`] whenever something changed.
///
/// The C# class raises a `Changed` event and expires transient activities on timers. Here every
/// method that can change what is showing returns whether it did, so the caller redraws when it
/// gets `true`, and transient activities are expired by [`Self::tick`], which the app calls at
/// [`Self::next_deadline`]. Times are [`Instant`]s because only their distance apart matters, as
/// with the C# `TimeProvider` timers. The C# lock is the caller's `Mutex`, if it needs one.
#[derive(Debug, Default)]
pub struct ActivityManager {
    entries: HashMap<String, Entry>,
    suppressed: HashSet<String>,
    sequence: u64,
}

#[derive(Debug)]
struct Entry {
    activity: Activity,
    sequence: u64,
    /// When a transient activity goes; `None` for the other tiers.
    expires_at: Option<Instant>,
}

impl ActivityManager {
    pub fn new() -> Self {
        Self::default()
    }

    /// Turns sources off by id (the user's "show in the pill" settings). Suppressed ids are
    /// dropped on publish, and any that are currently showing are removed. Returns whether any
    /// were removed.
    pub fn set_suppressed<I, S>(&mut self, ids: I) -> bool
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        self.suppressed = ids.into_iter().map(Into::into).collect();
        let before = self.entries.len();
        let suppressed = &self.suppressed;
        self.entries.retain(|id, _| !suppressed.contains(id));
        self.entries.len() != before
    }

    /// Adds or replaces the activity with this id. `now` starts a transient activity's lifetime.
    /// Returns `false` when the id is suppressed and nothing changed.
    pub fn publish(&mut self, activity: Activity, now: Instant) -> bool {
        if self.suppressed.contains(&activity.id) {
            return false;
        }

        // An update keeps its place in line so a ticking progress value does not reorder the pill.
        let sequence = match self.entries.remove(&activity.id) {
            Some(previous) => previous.sequence,
            None => {
                self.sequence += 1;
                self.sequence
            }
        };

        let expires_at = (activity.tier == ActivityTier::Transient)
            .then(|| now + activity.lifetime.unwrap_or(DEFAULT_TRANSIENT_LIFETIME));
        self.entries.insert(activity.id.clone(), Entry { activity, sequence, expires_at });
        true
    }

    pub fn remove(&mut self, id: &str) -> bool {
        self.entries.remove(id).is_some()
    }

    /// When the next transient activity expires, or `None` when none is showing. Call
    /// [`Self::tick`] then.
    pub fn next_deadline(&self) -> Option<Instant> {
        self.entries.values().filter_map(|e| e.expires_at).min()
    }

    /// Removes the transient activities whose lifetime is over at `now`. Returns whether any were.
    pub fn tick(&mut self, now: Instant) -> bool {
        let before = self.entries.len();
        self.entries.retain(|_, e| e.expires_at.is_none_or(|at| at > now));
        self.entries.len() != before
    }

    /// Highest tier first, then most recently published first.
    pub fn snapshot(&self) -> Vec<Activity> {
        let mut entries: Vec<&Entry> = self.entries.values().collect();
        entries.sort_by(|a, b| b.activity.tier.cmp(&a.activity.tier).then(b.sequence.cmp(&a.sequence)));
        entries.into_iter().map(|e| e.activity.clone()).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make(id: &str, tier: ActivityTier, lifetime: Option<Duration>) -> Activity {
        Activity { lifetime, ..Activity::new(id, tier, id) }
    }

    fn ids(manager: &ActivityManager) -> Vec<String> {
        manager.snapshot().into_iter().map(|a| a.id).collect()
    }

    fn seconds(value: f64) -> Duration {
        Duration::from_secs_f64(value)
    }

    #[test]
    fn snapshot_orders_by_tier_then_most_recent() {
        let mut manager = ActivityManager::new();
        let now = Instant::now();

        manager.publish(make("media", ActivityTier::Ongoing, None), now);
        manager.publish(make("agent", ActivityTier::Attention, None), now);
        manager.publish(make("timer", ActivityTier::Ongoing, None), now);
        manager.publish(make("volume", ActivityTier::Transient, None), now);

        assert_eq!(ids(&manager), ["volume", "agent", "timer", "media"]);
    }

    #[test]
    fn glance_activities_come_after_every_other_tier() {
        let mut manager = ActivityManager::new();
        let now = Instant::now();

        manager.publish(make("media", ActivityTier::Ongoing, None), now);
        manager.publish(make("players", ActivityTier::Glance, None), now);
        manager.publish(make("stopwatch", ActivityTier::Glance, None), now);

        assert_eq!(ids(&manager), ["media", "stopwatch", "players"]);

        manager.remove("media");
        assert_eq!(manager.snapshot()[0].tier, ActivityTier::Glance);
    }

    #[test]
    fn republishing_replaces_and_keeps_position() {
        let mut manager = ActivityManager::new();
        let now = Instant::now();

        manager.publish(make("media", ActivityTier::Ongoing, None), now);
        manager.publish(make("timer", ActivityTier::Ongoing, None), now);
        manager.publish(Activity { title: "updated".into(), ..make("media", ActivityTier::Ongoing, None) }, now);

        let snapshot = manager.snapshot();
        assert_eq!(ids(&manager), ["timer", "media"]);
        assert_eq!(snapshot[1].title, "updated");
    }

    #[test]
    fn transient_expires_after_its_lifetime() {
        let mut manager = ActivityManager::new();
        let start = Instant::now();
        let mut changes = 0;

        changes += i32::from(manager.publish(make("volume", ActivityTier::Transient, Some(seconds(2.0))), start));
        assert_eq!(manager.next_deadline(), Some(start + seconds(2.0)));
        changes += i32::from(manager.tick(start + seconds(1.9)));
        assert_eq!(manager.snapshot().len(), 1);

        changes += i32::from(manager.tick(start + seconds(2.1)));
        assert!(manager.snapshot().is_empty());
        assert_eq!(changes, 2);
        assert_eq!(manager.next_deadline(), None);
    }

    #[test]
    fn republishing_a_transient_restarts_its_lifetime() {
        let mut manager = ActivityManager::new();
        let start = Instant::now();

        manager.publish(make("volume", ActivityTier::Transient, Some(seconds(2.0))), start);
        manager.tick(start + seconds(1.5));
        manager.publish(make("volume", ActivityTier::Transient, Some(seconds(2.0))), start + seconds(1.5));
        manager.tick(start + seconds(3.0));
        assert_eq!(manager.snapshot().len(), 1);

        manager.tick(start + seconds(4.0));
        assert!(manager.snapshot().is_empty());
    }

    #[test]
    fn ongoing_activities_do_not_expire() {
        let mut manager = ActivityManager::new();
        let start = Instant::now();

        manager.publish(make("media", ActivityTier::Ongoing, None), start);
        assert_eq!(manager.next_deadline(), None);
        manager.tick(start + Duration::from_secs(3600));

        assert_eq!(manager.snapshot().len(), 1);
    }

    #[test]
    fn suppressed_ids_are_removed_and_ignored() {
        let mut manager = ActivityManager::new();
        let now = Instant::now();
        manager.publish(make("media", ActivityTier::Ongoing, None), now);
        manager.publish(make("timer", ActivityTier::Ongoing, None), now);

        assert!(manager.set_suppressed(["media"]));
        assert!(!manager.publish(make("media", ActivityTier::Ongoing, None), now));
        assert_eq!(ids(&manager), ["timer"]);

        assert!(!manager.set_suppressed(Vec::<String>::new()));
        manager.publish(make("media", ActivityTier::Ongoing, None), now);
        assert_eq!(manager.snapshot().len(), 2);
    }

    #[test]
    fn remove_reports_whether_anything_was_removed() {
        let mut manager = ActivityManager::new();
        manager.publish(make("media", ActivityTier::Ongoing, None), Instant::now());

        assert!(manager.remove("media"));
        assert!(!manager.remove("media"));
        assert!(manager.snapshot().is_empty());
    }
}
