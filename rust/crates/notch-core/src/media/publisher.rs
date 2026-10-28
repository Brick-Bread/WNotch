//! Keeps a "media" activity in the pill for as long as something is playing. Ported from
//! `src/Notch.Core/Media/MediaActivityPublisher.cs`.

use std::time::Instant;

use super::{ACTIVITY_ID, MediaSnapshot};
use crate::activities::{Activity, ActivityManager, ActivityTier, Glow, GlowColor, GlowPattern};

const MUSIC_GLYPH: &str = "\u{E8D6}";

/// Shows `media` in the pill while it plays and removes it otherwise. The C# class does this on
/// creation and on every `Changed` from the media service; the app calls this at those times.
/// Returns whether the activities changed.
pub fn publish(media: Option<&MediaSnapshot>, activities: &mut ActivityManager, now: Instant) -> bool {
    let Some(media) = media.filter(|m| m.is_playing) else {
        return activities.remove(ACTIVITY_ID);
    };

    let title = if media.title.trim().is_empty() { "Playing" } else { media.title.as_str() };
    activities.publish(
        Activity {
            glyph: Some(MUSIC_GLYPH.into()),
            image: media.thumbnail.clone(),
            detail: Some(media.artist.clone()),

            // The shell swaps in a colour taken from the artwork when it has one.
            glow: Some(Glow::with_strength(GlowColor::VIOLET, GlowPattern::Audio, 0.85)),
            ..Activity::new(ACTIVITY_ID, ActivityTier::Ongoing, title)
        },
        now,
    )
}
