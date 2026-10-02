//! Coding agents in a terminal: what they are doing, how that shows in the pill, and the hooks
//! that make the agent CLIs report their status.

use serde::{Deserialize, Serialize};

use crate::activity::{Activity, ActivityTier};
use crate::glow::{Glow, GlowColor, GlowPattern};

/// Which agent CLI a terminal profile runs.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AgentKind {
    /// A plain shell; no status is tracked.
    #[default]
    None,
    Claude,
    Codex,
}

/// What an agent is doing right now.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AgentState {
    #[default]
    Idle,
    Working,
    NeedsInput,
    /// Finished a turn that the user has not looked at yet.
    Done,
}

/// Works out what a coding agent is doing from the hook events it reports and from what
/// passes through its terminal. Every method returns whether the state changed.
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

    /// A Claude Code hook event name, or a Codex notification type.
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
            self.set(AgentState::Working)
        } else {
            false
        }
    }

    /// The terminal bell rang, which agents use to ask for attention.
    pub fn on_bell(&mut self) -> bool {
        if self.infers_work_from_terminal() && self.state == AgentState::Working {
            self.set(AgentState::NeedsInput)
        } else {
            false
        }
    }

    /// The terminal has printed nothing for a while.
    pub fn on_output_quiet(&mut self) -> bool {
        // A working Codex animates a spinner, so silence means the "work" was a false start
        // (for example Enter on an empty prompt).
        if self.infers_work_from_terminal() && self.state == AgentState::Working {
            self.set(AgentState::Idle)
        } else {
            false
        }
    }

    /// The user is looking at this session.
    pub fn on_viewed(&mut self) -> bool {
        if self.state == AgentState::Done {
            self.set(AgentState::Idle)
        } else {
            false
        }
    }

    pub fn reset(&mut self) -> bool {
        self.set(AgentState::Idle)
    }

    fn set(&mut self, state: AgentState) -> bool {
        let changed = self.state != state;
        self.state = state;
        changed
    }
}

/// The id of the pill activity for an agent session.
pub fn activity_id(session_id: &str) -> String {
    format!("agent.{session_id}")
}

/// The pill activity for an agent session, or `None` when it has nothing to report.
///
/// `folder_name` is the folder the session works in, to tell two sessions of the same agent
/// apart. `others` is how many other sessions also have something to report; the pill only has
/// room for one.
pub fn agent_activity(
    session_id: &str,
    display_name: &str,
    glyph: &str,
    state: AgentState,
    folder_name: Option<&str>,
    others: usize,
) -> Option<Activity> {
    let title = match folder_name {
        Some(folder) if !folder.is_empty() => format!("{display_name} · {folder}"),
        _ => display_name.to_string(),
    };

    let (tier, detail, glow) = match state {
        AgentState::Working => (
            ActivityTier::Ongoing,
            "Working",
            Glow { color: GlowColor::CYAN, pattern: GlowPattern::Breathe, strength: 0.8 },
        ),
        AgentState::NeedsInput => (
            ActivityTier::Attention,
            "Needs input",
            Glow { color: GlowColor::AMBER, pattern: GlowPattern::Pulse, strength: 1.0 },
        ),
        AgentState::Done => (
            ActivityTier::Attention,
            "Done",
            Glow { color: GlowColor::GREEN, pattern: GlowPattern::Steady, strength: 0.8 },
        ),
        AgentState::Idle => return None,
    };

    Some(Activity {
        id: activity_id(session_id),
        tier,
        title,
        detail: Some(if others > 0 { format!("{detail} · +{others}") } else { detail.to_string() }),
        glyph: Some(glyph.to_string()),
        glow: Some(glow),
        ..Activity::default()
    })
}

/// The contract between the app and the agent CLIs. The app starts each CLI with hooks pointing
/// at itself (`<notch exe> --hook`); the hook forwards what the CLI tells it to the app,
/// tagged with the session it came from.
pub mod hooks {
    use serde_json::{json, Map, Value};

    use super::AgentKind;

    /// Environment variable naming the pipe (or socket) the hook should write to.
    pub const PIPE_VARIABLE: &str = "NOTCH_PIPE";

    /// Environment variable identifying the terminal session the CLI runs in.
    pub const SESSION_VARIABLE: &str = "NOTCH_SESSION";

    /// The argument that makes the app act as a hook instead of opening its window.
    const HOOK_ARGUMENT: &str = "--hook";

    const CLAUDE_EVENTS: [&str; 6] =
        ["UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    /// The executable path without the `--hook` argument, which callers may or may not have appended.
    fn executable_path(hook_exe: &str) -> &str {
        hook_exe
            .trim()
            .strip_suffix(HOOK_ARGUMENT)
            .map_or(hook_exe.trim(), str::trim_end)
    }

    /// The settings JSON passed to `claude --settings`: every relevant hook runs the app with `--hook`.
    pub fn build_claude_settings(hook_exe: &str) -> String {
        let command = to_shell_command(executable_path(hook_exe));
        let hooks: Map<String, Value> = CLAUDE_EVENTS
            .iter()
            .map(|name| {
                let entry = json!([{ "hooks": [{ "type": "command", "command": command }] }]);
                ((*name).to_string(), entry)
            })
            .collect();
        json!({ "hooks": hooks }).to_string()
    }

    /// Extra arguments that make an agent CLI report to the hook. `claude_settings_path` is
    /// where the output of [`build_claude_settings`] was saved.
    pub fn build_arguments(kind: AgentKind, hook_exe: &str, claude_settings_path: &str) -> Vec<String> {
        let exe = executable_path(hook_exe);
        match kind {
            AgentKind::Claude => vec!["--settings".into(), claude_settings_path.into()],
            // A TOML literal string keeps backslashes as-is, but cannot contain a single quote.
            AgentKind::Codex if !exe.contains('\'') => {
                vec!["-c".into(), format!("notify=['{exe}','{HOOK_ARGUMENT}']")]
            }
            AgentKind::Codex | AgentKind::None => Vec::new(),
        }
    }

    /// Formats the line the hook writes to the pipe.
    pub fn format_message(session: &str, payload: &str) -> String {
        json!({ "session": session, "payload": payload }).to_string()
    }

    /// Reads a line written by the hook: the session and the event name, which is Claude's
    /// `hook_event_name` or Codex's notification `type`.
    pub fn try_parse_message(line: &str) -> Option<(String, String)> {
        let message: Value = serde_json::from_str(line).ok()?;
        let session = message.get("session")?.as_str().filter(|s| !s.is_empty())?;
        let payload_text = message.get("payload")?.as_str().filter(|s| !s.trim().is_empty())?;

        let payload: Value = serde_json::from_str(payload_text).ok()?;
        let name = ["hook_event_name", "type"]
            .iter()
            .find_map(|key| payload.get(key)?.as_str())
            .filter(|n| !n.is_empty())?;
        Some((session.to_string(), name.to_string()))
    }

    /// Claude runs hook commands through a shell (Git Bash by default on Windows), where
    /// backslashes are escapes, so the path is written with forward slashes.
    fn to_shell_command(executable_path: &str) -> String {
        let path = executable_path.replace('\\', "/");
        if path.contains(' ') {
            format!("\"{path}\" {HOOK_ARGUMENT}")
        } else {
            format!("{path} {HOOK_ARGUMENT}")
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        fn commands(settings: &str) -> Vec<(String, String)> {
            let value: Value = serde_json::from_str(settings).unwrap();
            CLAUDE_EVENTS
                .iter()
                .map(|name| {
                    let hook = &value["hooks"][name][0]["hooks"][0];
                    assert_eq!(hook["type"], "command");
                    ((*name).to_string(), hook["command"].as_str().unwrap().to_string())
                })
                .collect()
        }

        #[test]
        fn claude_settings_hook_every_tracked_event() {
            let settings = build_claude_settings(r"C:\Apps\Notch\notch.exe");
            let commands = commands(&settings);
            assert_eq!(commands.len(), 6);
            for (_, command) in commands {
                assert_eq!(command, "C:/Apps/Notch/notch.exe --hook");
            }
        }

        #[test]
        fn hook_paths_with_spaces_are_quoted() {
            let settings = build_claude_settings(r"C:\Program Files\Notch\notch.exe");
            for (_, command) in commands(&settings) {
                assert_eq!(command, "\"C:/Program Files/Notch/notch.exe\" --hook");
            }
        }

        #[test]
        fn an_appended_hook_argument_is_not_doubled() {
            let plain = build_claude_settings(r"C:\Program Files\Notch\notch.exe");
            assert_eq!(build_claude_settings(r"C:\Program Files\Notch\notch.exe --hook"), plain);
            assert_eq!(
                build_arguments(AgentKind::Codex, "/usr/bin/notch --hook", ""),
                build_arguments(AgentKind::Codex, "/usr/bin/notch", "")
            );
        }

        #[test]
        fn agent_arguments_point_at_the_hook() {
            assert_eq!(
                build_arguments(AgentKind::Claude, r"C:\notch.exe", r"C:\s.json"),
                ["--settings", r"C:\s.json"]
            );
            assert_eq!(
                build_arguments(AgentKind::Codex, r"C:\notch.exe", r"C:\s.json"),
                ["-c", r"notify=['C:\notch.exe','--hook']"]
            );
            assert!(build_arguments(AgentKind::None, r"C:\notch.exe", r"C:\s.json").is_empty());
        }

        #[test]
        fn codex_paths_with_a_single_quote_get_no_arguments() {
            assert!(build_arguments(AgentKind::Codex, "/home/o'neil/notch", "s.json").is_empty());
        }

        #[test]
        fn hook_messages_round_trip() {
            for (payload, expected) in [
                (r#"{"hook_event_name":"Stop","session_id":"abc"}"#, "Stop"),
                (r#"{"type":"agent-turn-complete","turn-id":"1"}"#, "agent-turn-complete"),
            ] {
                let line = format_message("session-7", payload);
                assert_eq!(try_parse_message(&line), Some(("session-7".to_string(), expected.to_string())));
            }
        }

        #[test]
        fn malformed_hook_messages_are_rejected() {
            for line in [
                "not json",
                r#"{"session":"s","payload":"not json"}"#,
                r#"{"session":"s","payload":"{}"}"#,
                r#"{"payload":"{\"type\":\"x\"}"}"#,
                r#"{"session":"","payload":"{\"type\":\"x\"}"}"#,
                r#"{"session":"s","payload":"  "}"#,
            ] {
                assert_eq!(try_parse_message(line), None, "{line}");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn claude_reports_a_full_turn() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Claude);
        let steps: [fn(&mut AgentStatusTracker) -> bool; 7] = [
            |t| t.on_hook_event("UserPromptSubmit"),
            |t| t.on_hook_event("PreToolUse"),
            |t| t.on_hook_event("Notification"),
            |t| t.on_user_input(false),
            |t| t.on_hook_event("PostToolUse"),
            |t| t.on_hook_event("Stop"),
            |t| t.on_viewed(),
        ];
        let mut states = Vec::new();
        for step in steps {
            if step(&mut tracker) {
                states.push(tracker.state());
            }
        }

        assert_eq!(
            states,
            [AgentState::Working, AgentState::NeedsInput, AgentState::Working, AgentState::Done, AgentState::Idle]
        );
    }

    #[test]
    fn unknown_events_change_nothing() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Claude);
        assert!(!tracker.on_hook_event("SomethingElse"));
        assert!(!tracker.reset());
    }

    #[test]
    fn codex_work_is_inferred_from_the_terminal() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Codex);

        assert!(!tracker.on_user_input(false));
        assert_eq!(tracker.state(), AgentState::Idle);

        assert!(tracker.on_user_input(true));
        assert_eq!(tracker.state(), AgentState::Working);

        assert!(tracker.on_bell());
        assert_eq!(tracker.state(), AgentState::NeedsInput);

        assert!(tracker.on_user_input(false));
        assert!(tracker.on_hook_event("agent-turn-complete"));
        assert_eq!(tracker.state(), AgentState::Done);
    }

    #[test]
    fn codex_false_start_clears_when_the_terminal_goes_quiet() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Codex);

        tracker.on_user_input(true);
        assert!(tracker.on_output_quiet());

        assert_eq!(tracker.state(), AgentState::Idle);
    }

    #[test]
    fn typing_in_claude_does_not_count_as_work() {
        let mut tracker = AgentStatusTracker::new(AgentKind::Claude);

        assert!(!tracker.on_user_input(true));
        assert!(!tracker.on_bell());
        assert!(!tracker.on_output_quiet());

        assert_eq!(tracker.state(), AgentState::Idle);
    }

    #[test]
    fn shells_are_never_tracked() {
        let mut tracker = AgentStatusTracker::new(AgentKind::None);

        assert!(!tracker.on_user_input(true));

        assert_eq!(tracker.state(), AgentState::Idle);
    }

    #[test]
    fn agent_states_map_to_pill_tiers() {
        let make = |state| agent_activity("1", "Claude", "g", state, None, 0);

        assert_eq!(make(AgentState::Idle), None);
        assert_eq!(make(AgentState::Working).unwrap().tier, ActivityTier::Ongoing);
        assert_eq!(make(AgentState::NeedsInput).unwrap().tier, ActivityTier::Attention);
        assert_eq!(make(AgentState::Done).unwrap().id, "agent.1");
        assert_eq!(activity_id("1"), "agent.1");
    }

    #[test]
    fn agent_activity_names_its_folder_and_counts_the_other_sessions() {
        let alone = agent_activity("1", "Claude", "g", AgentState::Working, Some("notch"), 0).unwrap();
        assert_eq!((alone.title.as_str(), alone.detail.as_deref()), ("Claude · notch", Some("Working")));

        let one_of_three = agent_activity("1", "Codex", "g", AgentState::NeedsInput, Some("website"), 2).unwrap();
        assert_eq!(
            (one_of_three.title.as_str(), one_of_three.detail.as_deref()),
            ("Codex · website", Some("Needs input · +2"))
        );

        let no_folder = agent_activity("1", "Claude", "g", AgentState::Done, Some(""), 0).unwrap();
        assert_eq!(no_folder.title, "Claude");
    }

    #[test]
    fn agent_states_have_distinct_glows() {
        let glow = |state| agent_activity("s", "Claude", "g", state, None, 0).unwrap().glow.unwrap();
        let (working, waiting, done) = (glow(AgentState::Working), glow(AgentState::NeedsInput), glow(AgentState::Done));

        assert_eq!(working.pattern, GlowPattern::Breathe);
        assert_eq!(waiting.pattern, GlowPattern::Pulse);
        assert_eq!(done.color, GlowColor::GREEN);
        assert_ne!(working.color, waiting.color);
    }

    #[test]
    fn states_serialise_for_the_ui() {
        assert_eq!(serde_json::to_string(&AgentState::NeedsInput).unwrap(), "\"needsInput\"");
        assert_eq!(serde_json::to_string(&AgentState::Idle).unwrap(), "\"idle\"");
        assert_eq!(serde_json::to_string(&AgentKind::Claude).unwrap(), "\"claude\"");
    }
}
