//! The contract between the app, the agent CLIs and Notch.Hook.exe. Ported from
//! `src/Notch.Core/Agents/AgentHooks.cs`.
//!
//! The app starts each CLI with hooks pointing at the hook executable; the hook forwards what the
//! CLI tells it to the app's named pipe, tagged with the session it came from.

use serde_json::{Map, Value, json};

use super::{AgentKind, stj};

/// Environment variable naming the pipe the hook should write to.
pub const PIPE_VARIABLE: &str = "NOTCH_PIPE";

/// Environment variable identifying the terminal session the CLI runs in.
pub const SESSION_VARIABLE: &str = "NOTCH_SESSION";

pub(crate) const CLAUDE_EVENTS: [&str; 6] =
    ["UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

/// The pipe every Notch of this user listens on for agents that were not started from Notch's
/// terminal, whose hooks do not know the per-process pipe. Debug builds use their own.
pub const SHARED_PIPE_NAME: &str =
    if cfg!(debug_assertions) { "notch-agent-shared-debug" } else { "notch-agent-shared" };

/// The settings JSON passed to `claude --settings`: every relevant hook runs the hook executable.
pub fn build_claude_settings(hook_executable_path: &str) -> String {
    let mut hooks = Map::new();
    for name in CLAUDE_EVENTS {
        hooks.insert(
            name.into(),
            json!([{ "hooks": [{ "type": "command", "command": to_shell_command(hook_executable_path) }] }]),
        );
    }

    stj::compact(&json!({ "hooks": hooks }))
}

/// Extra arguments that make an agent CLI report to the hook executable.
///
/// `claude_settings_path`: where the output of [`build_claude_settings`] was saved.
pub fn build_arguments(kind: AgentKind, hook_executable_path: &str, claude_settings_path: &str) -> Vec<String> {
    match kind {
        AgentKind::Claude => vec!["--settings".into(), claude_settings_path.into()],

        // A TOML literal string keeps backslashes as-is, but cannot contain a single quote.
        AgentKind::Codex if !hook_executable_path.contains('\'') => {
            vec!["-c".into(), format!("notify=['{hook_executable_path}']")]
        }
        _ => Vec::new(),
    }
}

/// Formats the line the hook executable writes to the pipe.
///
/// `hook_pid`: the hook program's own process id, so the app can find which agent started it.
pub fn format_message(session_id: &str, payload: &str, hook_pid: i32) -> String {
    stj::compact(&json!({ "session": session_id, "payload": payload, "pid": hook_pid }))
}

/// What a hook reported.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HookMessage {
    /// The Notch terminal session, or empty for an agent started elsewhere.
    pub session: String,
    pub event_name: String,
    /// The hook program's process id; 0 when it did not say.
    pub hook_pid: i32,
    /// The folder the agent works in, when it said.
    pub folder: Option<String>,
    /// Which agent reported, when it said (opencode's plugin does); `None` for Claude and Codex.
    pub agent: Option<String>,
}

/// A JSON property read as C#'s `(string?)node["name"]` reads it: missing and `null` are `None`,
/// a string is itself, and anything else (or indexing something that is not an object) throws,
/// which the callers treat as a message they cannot read: `Err`.
fn string_property(node: &Value, name: &str) -> Result<Option<String>, ()> {
    match node {
        Value::Null => Ok(None),
        Value::Object(map) => match map.get(name) {
            None | Some(Value::Null) => Ok(None),
            Some(Value::String(s)) => Ok(Some(s.clone())),
            Some(_) => Err(()),
        },
        _ => Err(()),
    }
}

/// The session, payload object and event name of a line, as both parsers read them.
fn read_line(line: &str) -> Result<(Value, Option<String>, Value, String), ()> {
    let message: Value = serde_json::from_str(line).map_err(|_| ())?;
    let session = string_property(&message, "session")?;
    let payload_text = string_property(&message, "payload")?.filter(|p| !p.trim().is_empty()).ok_or(())?;

    let payload: Value = serde_json::from_str(&payload_text).map_err(|_| ())?;
    let name = match string_property(&payload, "hook_event_name")? {
        Some(name) => Some(name),
        None => string_property(&payload, "type")?,
    };
    let name = name.filter(|n| !n.is_empty()).ok_or(())?;
    Ok((message, session, payload, name))
}

/// Reads a line written by the hook executable, including agents that were not started by Notch.
pub fn try_parse_hook_message(line: &str) -> Option<HookMessage> {
    let (message, session, payload, event_name) = read_line(line).ok()?;

    let hook_pid = message.get("pid").and_then(|v| crate::json::int(v).ok()).unwrap_or(0);
    let folder = string_property(&payload, "cwd").ok()?;
    let agent = string_property(&payload, "agent").ok()?;
    Some(HookMessage {
        session: session.unwrap_or_default(),
        event_name,
        hook_pid,
        folder: folder.filter(|f| !f.trim().is_empty()),
        agent: agent.filter(|a| !a.trim().is_empty()),
    })
}

/// Reads a line written by the hook executable: the session id and the event name (Claude's
/// `hook_event_name` or Codex's notification `type`).
pub fn try_parse_message(line: &str) -> Option<(String, String)> {
    let (_, session, _, event_name) = read_line(line).ok()?;
    let session = session.filter(|s| !s.is_empty())?;
    Some((session, event_name))
}

/// Claude runs hook commands through a shell (Git Bash by default on Windows), where
/// backslashes are escapes, so the path is written with forward slashes.
pub(crate) fn to_shell_command(executable_path: &str) -> String {
    let path = executable_path.replace('\\', "/");
    if path.contains(' ') { format!("\"{path}\"") } else { path }
}

#[cfg(test)]
mod tests {
    use super::*;

    // From AgentDetectionTests.cs (AgentHookMessageTests).

    #[test]
    fn a_message_from_an_agent_started_elsewhere_carries_the_hook_pid_and_folder() {
        let payload = r#"{"hook_event_name":"Stop","cwd":"C:\\work\\app","session_id":"abc"}"#;
        let line = format_message("", payload, 4321);

        let message = try_parse_hook_message(&line).unwrap();
        assert_eq!(message.session, "");
        assert_eq!(message.event_name, "Stop");
        assert_eq!(message.hook_pid, 4321);
        assert_eq!(message.folder.as_deref(), Some(r"C:\work\app"));
    }

    #[test]
    fn codex_notifications_are_read_too() {
        let line = format_message("s1", r#"{"type":"agent-turn-complete","cwd":"/x"}"#, 0);
        let message = try_parse_hook_message(&line).unwrap();
        assert_eq!(message.session, "s1");
        assert_eq!(message.event_name, "agent-turn-complete");
        assert_eq!(message.hook_pid, 0);
    }

    #[test]
    fn bad_messages_are_refused() {
        for line in ["nope", "{}", r#"{"session":"s","payload":"{}"}"#, r#"{"session":"s","payload":"nope"}"#] {
            assert_eq!(try_parse_hook_message(line), None, "{line}");
        }
    }

    #[test]
    fn values_of_the_wrong_type_refuse_the_message_as_csharp_does() {
        let event = r#"{\"hook_event_name\":\"Stop\"}"#;
        assert_eq!(try_parse_hook_message(&format!(r#"{{"session":5,"payload":"{event}"}}"#)), None);
        assert_eq!(try_parse_hook_message(r#"{"payload":"{\"hook_event_name\":1,\"type\":\"x\"}"}"#), None);
        assert_eq!(try_parse_hook_message(r#"{"payload":"{\"hook_event_name\":\"Stop\",\"cwd\":2}"}"#), None);
        assert_eq!(try_parse_hook_message("[1]"), None);

        // null falls back to "type"; a pid that is not a 32-bit integer reads as 0.
        let message =
            try_parse_hook_message(r#"{"pid":1.5,"payload":"{\"hook_event_name\":null,\"type\":\"x\"}"}"#).unwrap();
        assert_eq!((message.event_name.as_str(), message.hook_pid), ("x", 0));
    }

    #[test]
    fn the_line_is_written_as_system_text_json_writes_it() {
        assert_eq!(format_message("s", r#"{"x":1}"#, 3), r#"{"session":"s","payload":"{\u0022x\u0022:1}","pid":3}"#);
    }

    // From OpenCodeTests.cs (OpenCodeHookTests).

    #[test]
    fn the_plugins_message_says_which_agent_and_folder_it_came_from() {
        let payload = r#"{"hook_event_name":"Notification","cwd":"C:\\work\\app","agent":"opencode"}"#;
        let line = format_message("", payload, 321);

        let message = try_parse_hook_message(&line).unwrap();

        assert_eq!(message.event_name, "Notification");
        assert_eq!(message.folder.as_deref(), Some(r"C:\work\app"));
        assert_eq!(message.agent.as_deref(), Some("opencode"));
        assert_eq!(message.hook_pid, 321);
        assert_eq!(message.session, "");
    }

    #[test]
    fn claude_and_codex_messages_have_no_agent_tag() {
        let line = format_message("s1", r#"{"hook_event_name":"Stop"}"#, 0);

        assert_eq!(try_parse_hook_message(&line).unwrap().agent, None);
    }

    #[test]
    fn opencode_reports_through_the_plugin_so_it_gets_no_command_line_arguments() {
        assert!(build_arguments(AgentKind::OpenCode, r"C:\Notch\Notch.Hook.exe", r"C:\x\claude.json").is_empty());
    }

    // From TerminalTests.cs.

    #[test]
    fn claude_settings_hook_every_tracked_event() {
        let settings: Value = serde_json::from_str(&build_claude_settings(r"C:\Apps\Notch\Notch.Hook.exe")).unwrap();

        for name in CLAUDE_EVENTS {
            let hook = &settings["hooks"][name][0]["hooks"][0];
            assert_eq!(hook["type"], "command");
            assert_eq!(hook["command"], "C:/Apps/Notch/Notch.Hook.exe");
        }
    }

    #[test]
    fn hook_paths_with_spaces_are_quoted() {
        let settings: Value =
            serde_json::from_str(&build_claude_settings(r"C:\Program Files\Notch\Notch.Hook.exe")).unwrap();

        assert_eq!(settings["hooks"]["Stop"][0]["hooks"][0]["command"], "\"C:/Program Files/Notch/Notch.Hook.exe\"");
    }

    #[test]
    fn agent_arguments_point_at_the_hook() {
        assert_eq!(build_arguments(AgentKind::Claude, r"C:\hook.exe", r"C:\s.json"), ["--settings", r"C:\s.json"]);
        assert_eq!(build_arguments(AgentKind::Codex, r"C:\hook.exe", r"C:\s.json"), ["-c", r"notify=['C:\hook.exe']"]);
        assert!(build_arguments(AgentKind::None, r"C:\hook.exe", r"C:\s.json").is_empty());
    }

    #[test]
    fn hook_messages_round_trip() {
        for (payload, expected_event) in [
            (r#"{"hook_event_name":"Stop","session_id":"abc"}"#, "Stop"),
            (r#"{"type":"agent-turn-complete","turn-id":"1"}"#, "agent-turn-complete"),
        ] {
            let line = format_message("session-7", payload, 0);

            assert_eq!(try_parse_message(&line), Some(("session-7".into(), expected_event.into())));
        }
    }

    #[test]
    fn malformed_hook_messages_are_rejected() {
        for line in [
            "not json",
            r#"{"session":"s","payload":"not json"}"#,
            r#"{"session":"s","payload":"{}"}"#,
            r#"{"payload":"{\"type\":\"x\"}"}"#,
        ] {
            assert_eq!(try_parse_message(line), None, "{line}");
        }
    }
}
