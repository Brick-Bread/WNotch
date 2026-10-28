//! Ported from `src/Notch.Core/Shelf/ShelfActivities.cs`.

use std::time::Duration;

use crate::activities::{Activity, ActivityTier, Glow, GlowColor, GlowPattern};

pub const ADDED_ID: &str = "shelf.added";

/// The notice for files dropped on the pill while the notch stayed closed, so the drop is seen to
/// have landed.
pub fn added(count: i32) -> Activity {
    Activity {
        detail: Some(if count == 1 { "1 item".into() } else { format!("{count} items") }),
        glyph: Some("\u{E8A5}".into()),
        glow: Some(Glow::with_strength(GlowColor::WHITE, GlowPattern::Flash, 0.6)),
        lifetime: Some(Duration::from_millis(3500)),
        ..Activity::new(ADDED_ID, ActivityTier::Transient, "On the shelf")
    }
}
