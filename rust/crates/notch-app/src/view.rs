//! What the notch's page is told: the mode, where the island sits, and the activities to show.

use base64::Engine;
use notch_core::activities::{Activity, ActivityTier, GlowPattern};
use notch_core::agents::AgentState;
use notch_core::shell::NotchMode;
use serde::Serialize;

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ShellView {
    pub mode: &'static str,
    pub expanded: bool,
    pub at_bottom: bool,
    pub floating: bool,
    pub edge_gap: f64,
    pub side_inset: f64,
    pub idle_height: f64,
    pub compact_height: f64,
    pub peek_height: f64,
    pub light: bool,
    pub glow_effects: bool,
    pub activities: Vec<ActivityView>,
}

pub fn mode_name(mode: NotchMode) -> &'static str {
    match mode {
        NotchMode::Idle => "idle",
        NotchMode::Compact => "compact",
        NotchMode::Peek => "peek",
        NotchMode::Expanded => "expanded",
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GlowView {
    /// `#rrggbb`.
    pub color: String,
    pub pattern: &'static str,
    pub strength: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ActivityView {
    pub id: String,
    pub tier: &'static str,
    pub title: String,
    pub detail: Option<String>,
    pub glyph: Option<String>,
    pub agent_id: Option<String>,
    pub agent_state: Option<&'static str>,
    /// A `data:` URL for the image, when there is one.
    pub image: Option<String>,
    pub progress: Option<f64>,
    pub glow: Option<GlowView>,
    /// Milliseconds since 1970, for a clock that counts up.
    pub counting_from: Option<i64>,
    pub opens_page: Option<String>,
    pub order: i32,
}

impl From<&Activity> for ActivityView {
    fn from(activity: &Activity) -> Self {
        Self {
            id: activity.id.clone(),
            tier: match activity.tier {
                ActivityTier::Glance => "glance",
                ActivityTier::Ongoing => "ongoing",
                ActivityTier::Attention => "attention",
                ActivityTier::Transient => "transient",
            },
            title: activity.title.clone(),
            detail: activity.detail.clone(),
            glyph: activity.glyph.clone(),
            agent_id: activity.agent_id.clone(),
            agent_state: activity.agent_state.map(|state| match state {
                AgentState::Idle => "idle",
                AgentState::Working => "working",
                AgentState::NeedsInput => "needsInput",
                AgentState::Done => "done",
            }),
            image: activity.image.as_deref().map(data_url),
            progress: activity.progress,
            glow: activity.glow.map(|glow| GlowView {
                color: glow.color.to_hex(),
                pattern: glow_pattern(glow.pattern),
                strength: glow.strength,
            }),
            counting_from: activity.counting_from.map(|at| at.timestamp_millis()),
            opens_page: activity.opens_page.clone(),
            order: activity.order,
        }
    }
}

fn glow_pattern(pattern: GlowPattern) -> &'static str {
    match pattern {
        GlowPattern::Steady => "steady",
        GlowPattern::Breathe => "breathe",
        GlowPattern::Pulse => "pulse",
        GlowPattern::Flash => "flash",
        GlowPattern::Audio => "audio",
    }
}

/// Images are PNG or JPEG; the browser reads either whatever the type says.
fn data_url(bytes: &[u8]) -> String {
    let kind = if bytes.starts_with(&[0xFF, 0xD8]) { "jpeg" } else { "png" };
    format!("data:image/{kind};base64,{}", base64::engine::general_purpose::STANDARD.encode(bytes))
}
