//! The pill activity for an agent session. Ported from `src/Notch.Core/Agents/AgentActivities.cs`.

use super::AgentState;
use crate::activities::{Activity, ActivityTier, Glow, GlowColor, GlowPattern};

pub fn id_for(session_id: &str) -> String {
    format!("agent.{session_id}")
}

/// The pill activity for an agent session, or `None` when it has nothing to report. Its title is
/// the display name and, after " · ", the folder.
///
/// - `folder_name`: the folder the session works in, to tell two sessions of the same agent apart.
/// - `others`: how many other sessions also have something to report; the pill only has room for one.
/// - `agent_id`: which agent it is ([`super::catalog`] id), so the pill can show its logo.
pub fn for_session(
    session_id: &str,
    display_name: &str,
    glyph: &str,
    state: AgentState,
    folder_name: Option<&str>,
    others: i32,
    agent_id: Option<&str>,
) -> Option<Activity> {
    let (tier, detail, glow) = match state {
        AgentState::Working => {
            (ActivityTier::Ongoing, "Working", Glow::with_strength(GlowColor::CYAN, GlowPattern::Breathe, 0.8))
        }
        AgentState::NeedsInput => {
            (ActivityTier::Attention, "Needs input", Glow::new(GlowColor::AMBER, GlowPattern::Pulse))
        }
        AgentState::Done => {
            (ActivityTier::Attention, "Done", Glow::with_strength(GlowColor::GREEN, GlowPattern::Steady, 0.8))
        }
        AgentState::Idle => return None,
    };

    let title = match folder_name.filter(|f| !f.is_empty()) {
        Some(folder) => format!("{display_name} · {folder}"),
        None => display_name.to_owned(),
    };

    let mut activity = Activity::new(id_for(session_id), tier, title);
    activity.detail = Some(if others > 0 { format!("{detail} · +{others}") } else { detail.to_owned() });
    activity.glyph = Some(glyph.to_owned());
    activity.agent_id = agent_id.map(str::to_owned);
    activity.agent_state = Some(state);
    activity.glow = Some(glow);
    Some(activity)
}

#[cfg(test)]
mod tests {
    use super::*;

    // From TerminalTests.cs.

    #[test]
    fn agent_states_map_to_pill_tiers() {
        let for_state = |state| for_session("1", "Claude", "g", state, None, 0, None);
        assert_eq!(for_state(AgentState::Idle), None);
        assert_eq!(for_state(AgentState::Working).unwrap().tier, ActivityTier::Ongoing);
        assert_eq!(for_state(AgentState::NeedsInput).unwrap().tier, ActivityTier::Attention);
        assert_eq!(for_state(AgentState::Done).unwrap().id, "agent.1");
    }

    #[test]
    fn agent_activity_names_its_folder_and_counts_the_other_sessions() {
        let alone = for_session("1", "Claude", "g", AgentState::Working, Some("notch"), 0, None).unwrap();
        assert_eq!((alone.title.as_str(), alone.detail.as_deref()), ("Claude · notch", Some("Working")));

        let one_of_three = for_session("1", "Codex", "g", AgentState::NeedsInput, Some("website"), 2, None).unwrap();
        assert_eq!(
            (one_of_three.title.as_str(), one_of_three.detail.as_deref()),
            ("Codex · website", Some("Needs input · +2"))
        );

        assert_eq!(for_session("1", "Claude", "g", AgentState::Done, Some(""), 0, None).unwrap().title, "Claude");
    }
}
