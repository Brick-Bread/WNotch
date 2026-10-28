//! Ported from `src/Notch.Core/Activities/Activity.cs`.

use std::time::Duration;

use chrono::{DateTime, Utc};

use super::Glow;
use crate::agents::AgentState;

/// Higher tiers win the pill. Ties go to the most recently published activity.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum ActivityTier {
    /// A figure to glance at, such as a stopwatch or a player count. While nothing of a higher
    /// tier is showing, every glance activity is drawn side by side in the pill, from any source:
    /// its glyph or image and its title (or its clock, see [`Activity::counting_from`]), in
    /// [`Activity::order`]. Stays until removed. API version 10.
    Glance = -1,
    /// Long-running background state: media playing, timer running, agent working.
    Ongoing = 0,
    /// Something wants the user: agent waiting for input, timer finished.
    Attention = 1,
    /// Short-lived HUD (volume, brightness, charging) that expires on its own.
    Transient = 2,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Activity {
    /// Stable per source, e.g. "media" or "hud.volume". Publishing the same id replaces it.
    pub id: String,
    pub tier: ActivityTier,
    pub title: String,
    pub detail: Option<String>,
    /// A Segoe Fluent Icons code point.
    pub glyph: Option<String>,
    /// The coding agent this is about, by agent catalog id ("claude", "codex", ...). The pill
    /// shows that agent's logo in place of [`Self::glyph`] when it has one.
    pub agent_id: Option<String>,
    /// What that agent is doing, for an activity about an agent; `None` otherwise.
    pub agent_state: Option<AgentState>,
    /// Encoded image bytes (PNG or JPEG) shown in place of [`Self::glyph`], e.g. album art.
    pub image: Option<Vec<u8>>,
    /// 0..1, or `None` when the activity has no meaningful progress.
    pub progress: Option<f64>,
    /// Light around the notch while this activity is on top; `None` for none.
    pub glow: Option<Glow>,
    /// Only used by [`ActivityTier::Transient`]; defaults to two seconds.
    pub lifetime: Option<Duration>,
    /// For a plugin's activity: the id of one of the plugin's pages, which clicking the pill opens
    /// while this activity is showing, instead of Home. API version 9.
    pub opens_page: Option<String>,
    /// A clock that counts up from this moment, shown in place of [`Self::detail`]. Notch keeps it
    /// ticking, so a running stopwatch needs no update each second. API version 9.
    pub counting_from: Option<DateTime<Utc>>,
    /// For a [`ActivityTier::Glance`] activity: its place in the row, lower first. Activities with
    /// the same order stand in the order of their ids. API version 10.
    pub order: i32,
}

impl Activity {
    /// An activity with only what every activity needs; set the rest on the result.
    pub fn new(id: impl Into<String>, tier: ActivityTier, title: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            tier,
            title: title.into(),
            detail: None,
            glyph: None,
            agent_id: None,
            agent_state: None,
            image: None,
            progress: None,
            glow: None,
            lifetime: None,
            opens_page: None,
            counting_from: None,
            order: 0,
        }
    }
}
