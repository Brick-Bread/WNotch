//! What is playing. Ported from `src/Notch.Core/Media`.

pub mod publisher;
pub mod service;
pub mod snapshot;

pub use service::MediaService;
pub use snapshot::MediaSnapshot;

/// The id of the activity that shows what is playing.
pub const ACTIVITY_ID: &str = "media";

#[cfg(test)]
mod tests {
    use std::cell::RefCell;
    use std::time::{Duration, Instant};

    use chrono::{DateTime, TimeDelta, TimeZone, Utc};

    use super::*;
    use crate::activities::ActivityManager;

    fn t0() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 1, 1, 12, 0, 0).unwrap()
    }

    fn track(playing: bool) -> MediaSnapshot {
        MediaSnapshot {
            title: "Song".into(),
            artist: "Artist".into(),
            is_playing: playing,
            position: Duration::from_secs(30),
            position_updated_at: t0(),
            duration: Duration::from_secs(100),
            ..MediaSnapshot::new("player")
        }
    }

    #[test]
    fn position_advances_while_playing() {
        assert_eq!(track(true).position_at(t0() + TimeDelta::seconds(10)), Duration::from_secs(40));
    }

    #[test]
    fn position_holds_while_paused() {
        assert_eq!(track(false).position_at(t0() + TimeDelta::seconds(10)), Duration::from_secs(30));
    }

    #[test]
    fn position_stops_at_the_duration() {
        assert_eq!(track(true).position_at(t0() + TimeDelta::minutes(10)), Duration::from_secs(100));
    }

    #[test]
    fn position_keeps_running_without_a_timeline() {
        let live = MediaSnapshot { duration: Duration::ZERO, ..track(true) };

        assert_eq!(live.position_at(t0() + TimeDelta::minutes(10)), Duration::from_secs(630));
    }

    #[derive(Default)]
    struct FakeMediaService {
        current: RefCell<Option<MediaSnapshot>>,
    }

    impl FakeMediaService {
        fn set(&self, snapshot: Option<MediaSnapshot>, activities: &mut ActivityManager) {
            *self.current.borrow_mut() = snapshot;
            // What the app does on the service's change notice.
            publisher::publish(self.current().as_ref(), activities, Instant::now());
        }
    }

    impl MediaService for FakeMediaService {
        fn current(&self) -> Option<MediaSnapshot> {
            self.current.borrow().clone()
        }

        fn toggle_play_pause(&self) {}

        fn next(&self) {}

        fn previous(&self) {}

        fn seek(&self, _position: Duration) {}
    }

    #[test]
    fn publisher_shows_media_only_while_playing() {
        let mut activities = ActivityManager::new();
        let media = FakeMediaService::default();
        media.set(Some(track(true)), &mut activities);

        let snapshot = activities.snapshot();
        assert_eq!(snapshot.len(), 1);
        assert_eq!(snapshot[0].title, "Song");
        assert_eq!(snapshot[0].detail.as_deref(), Some("Artist"));

        media.set(Some(track(false)), &mut activities);
        assert!(activities.snapshot().is_empty());

        media.set(Some(track(true)), &mut activities);
        assert_eq!(activities.snapshot().len(), 1);

        media.set(None, &mut activities);
        assert!(activities.snapshot().is_empty());
    }
}
