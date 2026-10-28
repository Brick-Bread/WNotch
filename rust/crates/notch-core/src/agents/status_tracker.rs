//! Ported from `src/Notch.Core/Agents/AgentStatusTracker.cs`.
//!
//! The C# `Changed` event becomes a return value: each method returns whether the state changed.

use super::AgentState;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum AgentKind {
    /// A plain shell; no status is tracked.
    #[default]
    None,
    Claude,
    Codex,
    /// Found running, but reports nothing about what it is doing.
    Gemini,
    /// Found running, but reports nothing about what it is doing.
    Aider,
    /// Reports through the plugin Notch puts in opencode's plugins folder.
    OpenCode,
}

/// Works out what a coding agent is doing from the hook events it reports and from what
/// passes through its terminal.
#[derive(Debug, Clone)]
pub struct AgentStatusTracker {
    kind: AgentKind,
    state: AgentState,
}

impl AgentStatusTracker {
    pub fn new(kind: AgentKind) -> Self {
        Self { kind, state: AgentState::Idle }
    }

    pub fn state(&self) -> AgentState {
        self.state
    }

    /// Codex reports only the end of a turn, so its "working" state is inferred from the terminal.
    fn infers_work_from_terminal(&self) -> bool {
        self.kind == AgentKind::Codex
    }

    /// `event_name`: a Claude Code hook event name, or a Codex notification type.
    pub fn on_hook_event(&mut self, event_name: &str) -> bool {
        match event_name {
            "UserPromptSubmit" | "PreToolUse" | "PostToolUse" => self.set(AgentState::Working),

            // Claude raises Notification when it needs a permission or has been waiting on the user.
            "Notification" => self.set(AgentState::NeedsInput),

            "Stop" | "agent-turn-complete" => self.set(AgentState::Done),

            "SessionEnd" => self.set(AgentState::Idle),

            _ => false,
        }
    }

    /// The user typed into the terminal.
    pub fn on_user_input(&mut self, submitted: bool) -> bool {
        if self.kind == AgentKind::None {
            return false;
        }

        // Any keystroke answers whatever the agent was waiting for.
        if self.state == AgentState::NeedsInput || (submitted && self.infers_work_from_terminal()) {
            return self.set(AgentState::Working);
        }

        false
    }

    /// The terminal bell rang, which agents use to ask for attention.
    pub fn on_bell(&mut self) -> bool {
        self.infers_work_from_terminal() && self.state == AgentState::Working && self.set(AgentState::NeedsInput)
    }

    /// The terminal has printed nothing for a while.
    pub fn on_output_quiet(&mut self) -> bool {
        // A working Codex animates a spinner, so silence means the "work" was a false start
        // (for example Enter on an empty prompt).
        self.infers_work_from_terminal() && self.state == AgentState::Working && self.set(AgentState::Idle)
    }

    /// The user is looking at this session.
    pub fn on_viewed(&mut self) -> bool {
        self.state == AgentState::Done && self.set(AgentState::Idle)
    }

    pub fn reset(&mut self) -> bool {
        self.set(AgentState::Idle)
    }

    fn set(&mut self, state: AgentState) -> bool {
        if self.state == state {
            return false;
        }

        self.state = state;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Runs the steps and lists the state after each one that changed it, as a `Changed` handler would.
    fn changes(
        tracker: &mut AgentStatusTracker,
        steps: &[&dyn Fn(&mut AgentStatusTracker) -> bool],
    ) -> Vec<AgentState> {
        steps.iter().filter_map(|step| step(tracker).then_some(tracker.state())).collect()
    }

    // From TerminalTests.cs.

    #[test]
    fn claude_reports_a_full_turn() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Claude);
        let states = changes(
            &mut tracker,
            &[
                &|t| t.on_hook_event("UserPromptSubmit"),
                &|t| t.on_hook_event("PreToolUse"),
                &|t| t.on_hook_event("Notification"),
                &|t| t.on_user_input(false),
                &|t| t.on_hook_event("PostToolUse"),
                &|t| t.on_hook_event("Stop"),
                &|t| t.on_viewed(),
            ],
        );

        assert_eq!(
            states,
            [AgentState::Working, AgentState::NeedsInput, AgentState::Working, AgentState::Done, AgentState::Idle]
        );
    }

    #[test]
    fn codex_work_is_inferred_from_the_terminal() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Codex);

        tracker.on_user_input(false);
        assert_eq!(tracker.state(), AgentState::Idle);

        tracker.on_user_input(true);
        assert_eq!(tracker.state(), AgentState::Working);

        tracker.on_hook_event("agent-turn-complete");
        assert_eq!(tracker.state(), AgentState::Done);
    }

    #[test]
    fn codex_false_start_clears_when_the_terminal_goes_quiet() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Codex);

        tracker.on_user_input(true);
        tracker.on_output_quiet();

        assert_eq!(tracker.state(), AgentState::Idle);
    }

    #[test]
    fn codex_bell_while_working_needs_input() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Codex);
        tracker.on_user_input(true);
        assert!(tracker.on_bell());
        assert_eq!(tracker.state(), AgentState::NeedsInput);
        assert!(!AgentStatusTracker::new(AgentKind::Claude).on_bell());
    }

    #[test]
    fn typing_in_claude_does_not_count_as_work() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Claude);

        tracker.on_user_input(true);

        assert_eq!(tracker.state(), AgentState::Idle);
    }

    #[test]
    fn shells_are_never_tracked() {
        let mut tracker = AgentStatusTracker::new(AgentKind::None);

        tracker.on_user_input(true);

        assert_eq!(tracker.state(), AgentState::Idle);
    }

    // From OpenCodeTests.cs.

    #[test]
    fn the_states_follow_working_waiting_working_done() {
        let mut tracker = AgentStatusTracker::new(AgentKind::OpenCode);
        let seen = changes(
            &mut tracker,
            &[
                &|t| t.on_hook_event("UserPromptSubmit"),
                &|t| t.on_hook_event("Notification"),
                &|t| t.on_hook_event("PostToolUse"),
                &|t| t.on_hook_event("Stop"),
                &|t| t.on_viewed(),
            ],
        );

        assert_eq!(
            seen,
            [AgentState::Working, AgentState::NeedsInput, AgentState::Working, AgentState::Done, AgentState::Idle]
        );
    }

    #[test]
    fn an_interrupted_turn_goes_back_to_idle_without_a_done_badge() {
        let mut tracker = AgentStatusTracker::new(AgentKind::OpenCode);

        tracker.on_hook_event("UserPromptSubmit");
        tracker.on_hook_event("SessionEnd");

        assert_eq!(tracker.state(), AgentState::Idle);
    }
}
