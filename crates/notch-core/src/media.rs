//! What the system is playing, the "media" activity it puts in the pill, and the loudness
//! smoothing behind the audio-reactive glow.

use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::activity::{image_data_uri, Activity, ActivityTier};
use crate::glow::{Glow, GlowColor, GlowPattern};

/// The id of the activity that shows the current track in the pill.
pub const ACTIVITY_ID: &str = "media";

/// Shown in the pill when the player gives no title.
const UNTITLED: &str = "Playing";

/// How far a polled position may stray from the extrapolated one before it counts as a change.
const POSITION_TOLERANCE_SECS: f64 = 1.5;

/// What the system's current media session is playing, at one point in time.
#[derive(Debug, Clone, PartialEq)]
pub struct MediaSnapshot {
    pub source_app_id: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub is_playing: bool,
    /// Position as of [`position_updated_at`](Self::position_updated_at); use
    /// [`position_at`](Self::position_at) for the live value.
    pub position: Duration,
    pub position_updated_at: SystemTime,
    /// Zero when the source does not report a timeline (live streams, some browsers).
    pub duration: Duration,
    pub playback_rate: f64,
    /// Encoded image bytes (PNG or JPEG) as supplied by the source app.
    pub thumbnail: Option<Vec<u8>>,
    pub can_toggle_play_pause: bool,
    pub can_go_next: bool,
    pub can_go_previous: bool,
    pub can_seek: bool,
}

impl Default for MediaSnapshot {
    fn default() -> Self {
        Self {
            source_app_id: String::new(),
            title: String::new(),
            artist: String::new(),
            album: String::new(),
            is_playing: false,
            position: Duration::ZERO,
            position_updated_at: UNIX_EPOCH,
            duration: Duration::ZERO,
            playback_rate: 1.0,
            thumbnail: None,
            can_toggle_play_pause: false,
            can_go_next: false,
            can_go_previous: false,
            can_seek: false,
        }
    }
}

impl MediaSnapshot {
    pub fn has_timeline(&self) -> bool {
        self.duration > Duration::ZERO
    }

    /// Sources only report position occasionally, so the live value is extrapolated.
    pub fn position_at(&self, now: SystemTime) -> Duration {
        let mut seconds = self.position.as_secs_f64();
        if self.is_playing {
            if let Ok(elapsed) = now.duration_since(self.position_updated_at) {
                seconds += elapsed.as_secs_f64() * self.playback_rate;
            }
        }

        let position = Duration::from_secs_f64(seconds.max(0.0));
        if self.has_timeline() {
            position.min(self.duration)
        } else {
            position
        }
    }

    /// Whether `self`, read at `now`, is news compared with `previous`. Sources that are
    /// polled report a fresh position every time; that alone is not a change unless it
    /// jumped away from where `previous` was expected to be (a seek, or a restart).
    pub fn differs_from(&self, previous: &MediaSnapshot, now: SystemTime) -> bool {
        let expected = previous.position_at(now).as_secs_f64();
        let actual = self.position.as_secs_f64();
        let jumped = (actual - expected).abs() > POSITION_TOLERANCE_SECS;
        jumped
            || Self { position: previous.position, position_updated_at: previous.position_updated_at, ..self.clone() }
                != *previous
    }

    /// Where a click at `ratio` (0..1) along the timeline seeks to; `None` when the source
    /// cannot seek or has no timeline.
    pub fn seek_target(&self, ratio: f64) -> Option<Duration> {
        (self.can_seek && self.has_timeline()).then(|| self.duration.mul_f64(ratio.clamp(0.0, 1.0)))
    }
}

/// The "media" activity for what is playing, or `None` when nothing is (the activity should
/// then be removed). `accent` is a colour taken from the artwork; it replaces the default
/// glow colour when the track has artwork.
pub fn media_activity(media: Option<&MediaSnapshot>, accent: Option<GlowColor>) -> Option<Activity> {
    let media = media.filter(|m| m.is_playing)?;
    let color = media
        .thumbnail
        .is_some()
        .then_some(accent)
        .flatten()
        .unwrap_or(GlowColor::VIOLET);
    Some(Activity {
        id: ACTIVITY_ID.into(),
        tier: ActivityTier::Ongoing,
        glyph: Some("♪".into()),
        image: media.thumbnail.clone(),
        title: if media.title.trim().is_empty() { UNTITLED.into() } else { media.title.clone() },
        detail: Some(media.artist.clone()),
        glow: Some(Glow { color, pattern: GlowPattern::Audio, strength: 0.85 }),
        ..Activity::default()
    })
}

/// Playback commands the UI can send.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum MediaAction {
    #[serde(alias = "toggle", alias = "play_pause")]
    PlayPause,
    Next,
    Previous,
}

/// A [`MediaSnapshot`] as the UI receives it in the `media` event.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MediaState {
    pub source_app_id: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub is_playing: bool,
    pub position_ms: u64,
    /// When `position_ms` was true, in milliseconds since the Unix epoch.
    pub position_updated_at_ms: u64,
    pub duration_ms: u64,
    pub playback_rate: f64,
    /// The artwork as a `data:` URI.
    pub art: Option<String>,
    pub can_toggle_play_pause: bool,
    pub can_go_next: bool,
    pub can_go_previous: bool,
    pub can_seek: bool,
    pub has_timeline: bool,
}

impl From<&MediaSnapshot> for MediaState {
    fn from(media: &MediaSnapshot) -> Self {
        let millis = |d: Duration| u64::try_from(d.as_millis()).unwrap_or(u64::MAX);
        Self {
            source_app_id: media.source_app_id.clone(),
            title: media.title.clone(),
            artist: media.artist.clone(),
            album: media.album.clone(),
            is_playing: media.is_playing,
            position_ms: millis(media.position),
            position_updated_at_ms: millis(media.position_updated_at.duration_since(UNIX_EPOCH).unwrap_or_default()),
            duration_ms: millis(media.duration),
            playback_rate: media.playback_rate,
            art: media.thumbnail.as_deref().map(image_data_uri),
            can_toggle_play_pause: media.can_toggle_play_pause,
            can_go_next: media.can_go_next,
            can_go_previous: media.can_go_previous,
            can_seek: media.can_seek,
            has_timeline: media.has_timeline(),
        }
    }
}

/// Turns the raw output peak into the level the glow follows: the square root lifts quiet
/// passages, and a quick attack with a slow release keeps it from flickering.
#[derive(Debug, Clone, Copy, Default)]
pub struct LevelSmoother {
    level: f64,
}

impl LevelSmoother {
    const ATTACK: f64 = 0.6;
    /// Fraction of the level left after one second without a louder peak.
    const RELEASE_PER_SECOND: f64 = 0.08;

    /// Folds in a raw peak (0..1) measured `dt` seconds after the previous one; returns the
    /// smoothed level (0..1).
    pub fn update(&mut self, raw_peak: f64, dt: f64) -> f64 {
        let peak = raw_peak.clamp(0.0, 1.0).sqrt();
        self.level = if peak > self.level {
            self.level + (peak - self.level) * Self::ATTACK
        } else {
            self.level * Self::RELEASE_PER_SECOND.powf(dt.max(0.0))
        };
        self.level
    }

    pub fn level(&self) -> f64 {
        self.level
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn t0() -> SystemTime {
        UNIX_EPOCH + Duration::from_secs(1_700_000_000)
    }

    fn secs(s: u64) -> Duration {
        Duration::from_secs(s)
    }

    fn track(playing: bool) -> MediaSnapshot {
        MediaSnapshot {
            source_app_id: "player".into(),
            title: "Song".into(),
            artist: "Artist".into(),
            is_playing: playing,
            position: secs(30),
            position_updated_at: t0(),
            duration: secs(100),
            ..MediaSnapshot::default()
        }
    }

    #[test]
    fn position_advances_while_playing() {
        assert_eq!(track(true).position_at(t0() + secs(10)), secs(40));
    }

    #[test]
    fn position_holds_while_paused() {
        assert_eq!(track(false).position_at(t0() + secs(10)), secs(30));
    }

    #[test]
    fn position_stops_at_the_duration() {
        assert_eq!(track(true).position_at(t0() + secs(600)), secs(100));
    }

    #[test]
    fn position_keeps_running_without_a_timeline() {
        let live = MediaSnapshot { duration: Duration::ZERO, ..track(true) };
        assert_eq!(live.position_at(t0() + secs(600)), secs(630));
    }

    #[test]
    fn position_follows_the_playback_rate_and_ignores_the_past() {
        let fast = MediaSnapshot { playback_rate: 2.0, ..track(true) };
        assert_eq!(fast.position_at(t0() + secs(10)), secs(50));
        assert_eq!(track(true).position_at(t0() - secs(10)), secs(30));
    }

    #[test]
    fn publisher_shows_media_only_while_playing() {
        let activity = media_activity(Some(&track(true)), None).expect("playing shows");
        assert_eq!(activity.id, ACTIVITY_ID);
        assert_eq!(activity.title, "Song");
        assert_eq!(activity.detail.as_deref(), Some("Artist"));

        assert!(media_activity(Some(&track(false)), None).is_none());
        assert!(media_activity(None, None).is_none());
    }

    #[test]
    fn untitled_tracks_are_called_playing() {
        let media = MediaSnapshot { title: "  ".into(), ..track(true) };
        assert_eq!(media_activity(Some(&media), None).map(|a| a.title).as_deref(), Some("Playing"));
    }

    #[test]
    fn artwork_colours_the_glow_only_when_there_is_artwork() {
        let accent = GlowColor::new(10, 20, 30);
        let plain = media_activity(Some(&track(true)), Some(accent)).and_then(|a| a.glow);
        assert_eq!(plain.map(|g| g.color), Some(GlowColor::VIOLET));

        let with_art = MediaSnapshot { thumbnail: Some(vec![1]), ..track(true) };
        let activity = media_activity(Some(&with_art), Some(accent)).expect("shows");
        assert_eq!(activity.glow.map(|g| g.color), Some(accent));
        assert_eq!(activity.glow.map(|g| g.pattern), Some(GlowPattern::Audio));
        assert_eq!(activity.image, Some(vec![1]));
        assert_eq!(media_activity(Some(&with_art), None).and_then(|a| a.glow).map(|g| g.color), Some(GlowColor::VIOLET));
    }

    #[test]
    fn a_polled_position_is_news_only_when_it_jumps() {
        let before = track(true);
        let later = t0() + secs(10);
        let drifted = MediaSnapshot { position: Duration::from_millis(40_400), position_updated_at: later, ..track(true) };
        assert!(!drifted.differs_from(&before, later));

        let sought = MediaSnapshot { position: secs(80), position_updated_at: later, ..track(true) };
        assert!(sought.differs_from(&before, later));

        let paused = MediaSnapshot { position: secs(40), position_updated_at: later, ..track(false) };
        assert!(paused.differs_from(&before, later));

        let next_track = MediaSnapshot { title: "Other".into(), position: secs(40), position_updated_at: later, ..track(true) };
        assert!(next_track.differs_from(&before, later));
    }

    #[test]
    fn seeking_needs_a_timeline_and_permission() {
        let seekable = MediaSnapshot { can_seek: true, ..track(true) };
        assert_eq!(seekable.seek_target(0.25), Some(secs(25)));
        assert_eq!(seekable.seek_target(7.0), Some(secs(100)));
        assert_eq!(seekable.seek_target(-1.0), Some(Duration::ZERO));
        assert_eq!(track(true).seek_target(0.5), None);
        assert_eq!(MediaSnapshot { duration: Duration::ZERO, ..seekable }.seek_target(0.5), None);
    }

    #[test]
    fn ui_state_carries_milliseconds_and_artwork() {
        let media = MediaSnapshot { thumbnail: Some(vec![0xFF, 0xD8, 1]), can_seek: true, ..track(true) };
        let value = serde_json::to_value(MediaState::from(&media)).expect("serialises");
        assert_eq!(value["positionMs"], 30_000);
        assert_eq!(value["positionUpdatedAtMs"], 1_700_000_000_000_u64);
        assert_eq!(value["durationMs"], 100_000);
        assert_eq!(value["art"], "data:image/jpeg;base64,/9gB");
        assert_eq!(value["hasTimeline"], true);
        assert_eq!(value["canSeek"], true);
    }

    #[test]
    fn actions_parse_from_their_names() {
        let parse = |s: &str| serde_json::from_str::<MediaAction>(&format!("\"{s}\"")).ok();
        assert_eq!(parse("playPause"), Some(MediaAction::PlayPause));
        assert_eq!(parse("toggle"), Some(MediaAction::PlayPause));
        assert_eq!(parse("next"), Some(MediaAction::Next));
        assert_eq!(parse("previous"), Some(MediaAction::Previous));
        assert_eq!(parse("stop"), None);
    }

    #[test]
    fn level_attacks_fast_and_releases_slowly() {
        let mut smoother = LevelSmoother::default();
        let loud = smoother.update(1.0, 0.04);
        assert!(loud > 0.5);
        let after_one_second = smoother.update(0.0, 1.0);
        assert!((after_one_second - loud * 0.08).abs() < 1e-9);
        assert_eq!(smoother.level(), after_one_second);
    }

    #[test]
    fn quiet_passages_are_lifted_by_the_square_root() {
        let mut smoother = LevelSmoother::default();
        // sqrt(0.04) = 0.2, and the first step covers 60% of the way there.
        assert!((smoother.update(0.04, 0.04) - 0.12).abs() < 1e-9);
    }
}
