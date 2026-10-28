//! Every agent session there is, from every source. Ported from `src/Notch.Core/Agents/AgentBoard.cs`.
//!
//! C# raises a `Changed` event from a locked board, and `AgentPublisher` listens to it. Here the
//! board's methods return whether it changed, the caller holds it behind its own lock, and
//! [`AgentPublisher::publish`] is called with a snapshot and returns what to publish and remove,
//! since the activity manager lives with the app.

use indexmap::{IndexMap, IndexSet};

use super::{AgentState, activities};
use crate::activities::Activity;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AgentSource {
    /// Started from Notch's own terminal.
    Notch,
    /// Found running somewhere else on the computer.
    Detected,
    /// A session inside the Claude or ChatGPT desktop app, read from the files the app keeps.
    App,
}

/// One agent session as the Home tab and the pill show it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentEntry {
    /// Unique among entries: the terminal session id, or the key of a detected agent.
    pub key: String,
    pub agent_id: String,
    pub display_name: String,
    pub glyph: String,
    pub state: AgentState,
    pub folder_name: Option<String>,
    pub source: AgentSource,
    /// The agent's process id, for a detected agent.
    pub pid: Option<i32>,
    /// Where it runs, for a detected agent: "Windows Terminal", "VS Code", ...
    pub host: Option<String>,
}

impl AgentEntry {
    /// What the Home list says: the state, or "Open" for an agent that is there but reports nothing.
    pub fn state_text(&self) -> &'static str {
        match self.state {
            AgentState::Working => "Working",
            AgentState::NeedsInput => "Needs input",
            AgentState::Done => "Done",
            AgentState::Idle if self.source == AgentSource::Notch => "Idle",
            AgentState::Idle => "Open",
        }
    }
}

/// Every agent session there is, from every source. Writers update it; the pill and the Home tab read it.
#[derive(Debug, Clone, Default)]
pub struct AgentBoard {
    /// In the order they appeared.
    entries: IndexMap<String, AgentEntry>,
}

impl AgentBoard {
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds an entry or replaces the one with the same key. Keeps its place in the list.
    /// Returns whether anything changed.
    pub fn set(&mut self, entry: AgentEntry) -> bool {
        if self.entries.get(&entry.key) == Some(&entry) {
            return false;
        }

        self.entries.insert(entry.key.clone(), entry);
        true
    }

    pub fn remove(&mut self, key: &str) -> bool {
        self.entries.shift_remove(key).is_some()
    }

    /// Removes every entry of a source whose key is not in `keep`.
    pub fn keep_only<S: AsRef<str>>(&mut self, source: AgentSource, keep: &[S]) -> bool {
        let before = self.entries.len();
        self.entries.retain(|key, entry| entry.source != source || keep.iter().any(|k| k.as_ref() == key));
        self.entries.len() != before
    }

    /// The entries in the order they appeared.
    pub fn snapshot(&self) -> Vec<AgentEntry> {
        self.entries.values().cloned().collect()
    }

    pub fn find(&self, key: &str) -> Option<&AgentEntry> {
        self.entries.get(key)
    }
}

/// What the pill should change after the board changed.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Publication {
    pub publish: Vec<Activity>,
    /// Activity ids to take down.
    pub remove: Vec<String>,
}

/// Puts what is on an [`AgentBoard`] in the pill. Every entry is published, not just
/// the one that changed: each says how many others have something to report, since the pill shows one.
#[derive(Debug, Clone, Default)]
pub struct AgentPublisher {
    published: IndexSet<String>,
}

impl AgentPublisher {
    pub fn new() -> Self {
        Self::default()
    }

    /// Call with the board's entries each time it changes.
    pub fn publish(&mut self, entries: &[AgentEntry]) -> Publication {
        let reporting = entries.iter().filter(|e| e.state != AgentState::Idle).count() as i32;
        let mut now = IndexSet::new();
        let mut publication = Publication::default();
        for entry in entries {
            let activity = activities::for_session(
                &entry.key,
                &entry.display_name,
                &entry.glyph,
                entry.state,
                entry.folder_name.as_deref(),
                reporting - 1,
                Some(&entry.agent_id),
            );
            if let Some(activity) = activity {
                now.insert(entry.key.clone());
                publication.publish.push(activity);
            }
        }

        publication.remove = self.published.difference(&now).map(|gone| activities::id_for(gone)).collect();
        self.published = now;
        publication
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(key: &str, state: AgentState, source: AgentSource) -> AgentEntry {
        AgentEntry {
            key: key.into(),
            agent_id: "claude".into(),
            display_name: "Claude".into(),
            glyph: "g".into(),
            state,
            folder_name: Some("proj".into()),
            source,
            pid: None,
            host: None,
        }
    }

    fn working(key: &str) -> AgentEntry {
        entry(key, AgentState::Working, AgentSource::Notch)
    }

    fn keys(board: &AgentBoard) -> Vec<String> {
        board.snapshot().into_iter().map(|e| e.key).collect()
    }

    // From AgentDetectionTests.cs (AgentBoardTests).

    #[test]
    fn entries_keep_their_place_when_updated() {
        let mut board = AgentBoard::new();
        board.set(working("a"));
        board.set(working("b"));
        board.set(entry("a", AgentState::Done, AgentSource::Notch));

        assert_eq!(keys(&board), ["a", "b"]);
        assert_eq!(board.find("a").unwrap().state, AgentState::Done);
    }

    #[test]
    fn only_real_changes_are_announced() {
        let mut board = AgentBoard::new();

        let changes = [board.set(working("a")), board.set(working("a")), board.remove("nope"), board.remove("a")];

        assert_eq!(changes.iter().filter(|&&c| c).count(), 2);
    }

    #[test]
    fn keeping_only_some_removes_the_rest_of_that_source() {
        let mut board = AgentBoard::new();
        board.set(entry("tab", AgentState::Working, AgentSource::Notch));
        board.set(entry("d1", AgentState::Working, AgentSource::Detected));
        board.set(entry("d2", AgentState::Working, AgentSource::Detected));

        assert!(board.keep_only(AgentSource::Detected, &["d2"]));

        assert_eq!(keys(&board), ["tab", "d2"]);
        assert!(!board.keep_only(AgentSource::Detected, &["d2"]));
    }

    #[test]
    fn a_detected_agent_that_reports_nothing_is_called_open() {
        assert_eq!(entry("d", AgentState::Idle, AgentSource::Detected).state_text(), "Open");
        assert_eq!(entry("t", AgentState::Idle, AgentSource::Notch).state_text(), "Idle");
        assert_eq!(entry("t", AgentState::NeedsInput, AgentSource::Notch).state_text(), "Needs input");
    }

    #[test]
    fn the_pill_shows_each_entry_that_has_something_to_say_and_counts_the_others() {
        // Stands in for the activity manager: publishing an id replaces it.
        let mut shown: IndexMap<String, Activity> = IndexMap::new();
        let mut board = AgentBoard::new();
        let mut publisher = AgentPublisher::new();
        let mut change = |board: &mut AgentBoard, edit: &dyn Fn(&mut AgentBoard) -> bool| {
            if edit(board) {
                let publication = publisher.publish(&board.snapshot());
                for activity in publication.publish {
                    shown.insert(activity.id.clone(), activity);
                }
                for id in publication.remove {
                    shown.shift_remove(&id);
                }
            }
            shown.values().cloned().collect::<Vec<_>>()
        };

        change(&mut board, &|b| b.set(working("a")));
        change(&mut board, &|b| b.set(entry("b", AgentState::NeedsInput, AgentSource::Detected)));
        let now = change(&mut board, &|b| b.set(entry("c", AgentState::Idle, AgentSource::Detected)));

        assert_eq!(now.len(), 2);
        assert!(now.iter().all(|a| a.detail.as_deref().unwrap().ends_with("+1")));

        let now = change(&mut board, &|b| b.set(entry("b", AgentState::Idle, AgentSource::Detected)));
        assert_eq!(now.len(), 1);
        assert_eq!(now[0].detail.as_deref(), Some("Working"));

        let now = change(&mut board, &|b| b.remove("a"));
        assert!(now.is_empty());
    }

    // From AppSessionTests.cs.

    #[test]
    fn an_app_session_with_nothing_to_report_reads_as_open() {
        let entry = AgentEntry {
            key: "k".into(),
            agent_id: "claude".into(),
            display_name: "Claude".into(),
            glyph: String::new(),
            state: AgentState::Idle,
            folder_name: Some("Bug fix".into()),
            source: AgentSource::App,
            pid: Some(10),
            host: Some("Claude app".into()),
        };
        assert_eq!(entry.state_text(), "Open");
    }
}
