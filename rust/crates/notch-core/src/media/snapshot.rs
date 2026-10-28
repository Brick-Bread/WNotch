//! Ported from `src/Notch.Core/Media/MediaSnapshot.cs`.

use std::time::Duration;

use chrono::{DateTime, Utc};

/// What the system's current media session is playing, at one point in time.
#[derive(Debug, Clone, PartialEq)]
pub struct MediaSnapshot {
    pub source_app_id: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub is_playing: bool,
    /// Position as of [`Self::position_updated_at`]; use [`Self::position_at`] for the live value.
    pub position: Duration,
    pub position_updated_at: DateTime<Utc>,
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

/// `TimeSpan` ticks: 100 ns.
const NANOS_PER_TICK: i128 = 100;

impl MediaSnapshot {
    /// A snapshot with only the source set and the rest at the C# defaults.
    pub fn new(source_app_id: impl Into<String>) -> Self {
        Self {
            source_app_id: source_app_id.into(),
            title: String::new(),
            artist: String::new(),
            album: String::new(),
            is_playing: false,
            position: Duration::ZERO,
            position_updated_at: DateTime::<Utc>::MIN_UTC,
            duration: Duration::ZERO,
            playback_rate: 1.0,
            thumbnail: None,
            can_toggle_play_pause: false,
            can_go_next: false,
            can_go_previous: false,
            can_seek: false,
        }
    }

    pub fn has_timeline(&self) -> bool {
        self.duration > Duration::ZERO
    }

    /// Sources only report position occasionally, so the live value is extrapolated.
    pub fn position_at(&self, now: DateTime<Utc>) -> Duration {
        // In ticks, as `TimeSpan * double` works: the scaled span is rounded to whole ticks.
        let mut position = self.position.as_nanos() as i128 / NANOS_PER_TICK;
        if self.is_playing && now > self.position_updated_at {
            let elapsed = now - self.position_updated_at;
            let ticks = i128::from(elapsed.num_seconds()) * 10_000_000 + i128::from(elapsed.subsec_nanos()) / 100;
            // C# throws on a NaN or overflowing rate; here NaN adds nothing and the rest saturates.
            position += (ticks as f64 * self.playback_rate).round_ties_even() as i64 as i128;
        }

        if position < 0 {
            return Duration::ZERO;
        }

        let position = Duration::from_nanos(u64::try_from(position * NANOS_PER_TICK).unwrap_or(u64::MAX));
        if self.has_timeline() && position > self.duration { self.duration } else { position }
    }
}
