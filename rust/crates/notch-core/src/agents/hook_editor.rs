//! Adds Notch's hooks to the settings files the agents read for every session, so agents started
//! anywhere report what they are doing, and takes them out again. Ported from
//! `src/Notch.Core/Agents/AgentHookEditor.cs`.
//!
//! Everything that is not Notch's is left exactly as it was; a file that cannot be read is never
//! touched. Claude's file is written as System.Text.Json writes it (see [`super::stj`]); Codex's
//! is edited line by line.
//!
//! Where C# would throw on a hook group or command that is not an object (an array or a number
//! inside `"hooks"`), here it is simply not Notch's.

use serde_json::{Map, Value, json};

use super::hooks::CLAUDE_EVENTS;
use super::{ignore_case, stj};

/// What an edit of an agent's own settings file came to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HookEdit {
    /// The file's new contents (the old ones when nothing changed).
    pub text: String,
    pub changed: bool,
    /// Why nothing was changed, when that is because of something the user should know.
    pub problem: Option<String>,
}

impl HookEdit {
    pub(crate) fn new(text: impl Into<String>, changed: bool) -> Self {
        Self { text: text.into(), changed, problem: None }
    }

    pub(crate) fn refused(text: impl Into<String>, problem: impl Into<String>) -> Self {
        Self { text: text.into(), changed: false, problem: Some(problem.into()) }
    }
}

/// Text that marks a hook command as Notch's.
const MARKER: &str = "Notch.Hook";

const CODEX_COMMENT: &str = "# Notch: reports when Codex finishes a turn. Remove it in Notch's settings.";

const UNREADABLE: &str = "Claude's settings file could not be read, so it was left alone.";

/// The hook program as the agents' shells want it written (forward slashes, quoted when it has a space).
pub fn shell_command(hook_executable: &str) -> String {
    super::hooks::to_shell_command(hook_executable)
}

pub fn claude_has_hooks(json: Option<&str>) -> bool {
    let Some(root) = json.filter(|j| !j.trim().is_empty()).and_then(|j| serde_json::from_str::<Value>(j).ok()) else {
        return false;
    };

    let Some(Value::Object(hooks)) = root.get("hooks") else {
        return false;
    };

    hooks.values().filter_map(Value::as_array).any(|list| list.iter().any(is_notch_group))
}

/// Adds the six hooks to Claude's `settings.json` (`None` or empty text starts a new file).
pub fn add_claude_hooks(json: Option<&str>, hook_executable: &str) -> HookEdit {
    let original = json.unwrap_or("");
    let mut root = if original.trim().is_empty() {
        Map::new()
    } else {
        match serde_json::from_str::<Value>(original) {
            Ok(Value::Object(root)) => root,
            _ => return HookEdit::refused(original, UNREADABLE),
        }
    };

    let mut hooks = match root.get("hooks") {
        None | Some(Value::Null) => Map::new(),
        Some(Value::Object(hooks)) => hooks.clone(),
        Some(_) => {
            return HookEdit::refused(
                original,
                "The \"hooks\" setting in Claude's settings file is not what Notch expects, so it was left alone.",
            );
        }
    };

    let mut changed = false;
    for name in CLAUDE_EVENTS {
        let mut list = match hooks.get(name) {
            None | Some(Value::Null) => Vec::new(),
            Some(Value::Array(list)) => list.clone(),
            Some(_) => {
                return HookEdit::refused(
                    original,
                    format!(
                        "The \"{name}\" hooks in Claude's settings file are not what Notch expects, so it was left alone."
                    ),
                );
            }
        };

        if !list.iter().any(is_notch_group) {
            list.push(json!({ "hooks": [{ "type": "command", "command": shell_command(hook_executable) }] }));
            changed = true;
        }

        // Written back either way, as C# does: a null entry becomes an empty list.
        hooks.insert(name.into(), Value::Array(list));
    }

    root.insert("hooks".into(), Value::Object(hooks));
    if changed { HookEdit::new(stj::indented(&Value::Object(root)), true) } else { HookEdit::new(original, false) }
}

/// Takes Notch's hooks out of Claude's settings, leaving every other hook and setting.
pub fn remove_claude_hooks(json: Option<&str>) -> HookEdit {
    let Some(original) = json.filter(|j| !j.trim().is_empty()) else {
        return HookEdit::new("", false);
    };

    let mut root = match serde_json::from_str::<Value>(original) {
        Ok(Value::Object(root)) => root,
        _ => return HookEdit::refused(original, UNREADABLE),
    };

    let Some(Value::Object(hooks)) = root.get_mut("hooks") else {
        return HookEdit::new(original, false);
    };

    let mut changed = false;
    let names: Vec<String> = hooks.keys().cloned().collect();
    for name in names {
        let Some(Value::Array(list)) = hooks.get_mut(&name) else {
            continue;
        };

        let before = list.len();
        list.retain(|group| !is_notch_group(group));
        changed |= list.len() != before;

        if list.is_empty() {
            hooks.shift_remove(&name);
        }
    }

    if hooks.is_empty() {
        root.shift_remove("hooks");
    }

    if changed { HookEdit::new(stj::indented(&Value::Object(root)), true) } else { HookEdit::new(original, false) }
}

/// A hook group (`{ "hooks": [ { "command": ... } ] }`) that runs Notch's hook program.
fn is_notch_group(group: &Value) -> bool {
    let Some(Value::Array(commands)) = group.get("hooks") else {
        return false;
    };

    commands
        .iter()
        .any(|c| c.get("command").and_then(Value::as_str).is_some_and(|command| ignore_case::contains(command, MARKER)))
}

pub fn codex_has_hook(toml: Option<&str>) -> bool {
    toml.and_then(top_level_notify).is_some_and(|line| ignore_case::contains(line, MARKER))
}

/// Adds a top-level `notify` line to Codex's `config.toml`. Codex takes only one, so an
/// existing one that is not Notch's is left alone and reported.
pub fn add_codex_hook(toml: Option<&str>, hook_executable: &str) -> HookEdit {
    let toml = toml.unwrap_or("");
    if hook_executable.contains('\'') {
        return HookEdit::refused(
            toml,
            "The path of Notch's hook program contains a quote, which Codex's settings cannot hold.",
        );
    }

    if let Some(existing) = top_level_notify(toml) {
        return if ignore_case::contains(existing, MARKER) {
            HookEdit::new(toml, false)
        } else {
            HookEdit::refused(
                toml,
                "Codex already has a notify command of your own. Codex runs only one, so Notch did not change it.",
            )
        };
    }

    let newline = if toml.contains("\r\n") { "\r\n" } else { "\n" };
    let block = format!("{CODEX_COMMENT}{newline}notify = ['{hook_executable}']{newline}");
    HookEdit::new(if toml.is_empty() { block } else { format!("{block}{newline}{toml}") }, true)
}

/// Takes the `notify` line Notch added out of Codex's config.
pub fn remove_codex_hook(toml: Option<&str>) -> HookEdit {
    let Some(toml) = toml.filter(|t| !t.is_empty()) else {
        return HookEdit::new("", false);
    };

    let lines: Vec<&str> = toml.split('\n').collect();
    let mut kept: Vec<&str> = Vec::new();
    let mut changed = false;
    let mut i = 0;
    while i < lines.len() {
        let trimmed = lines[i].trim();
        let is_ours = is_notify_line(trimmed) && ignore_case::contains(trimmed, MARKER);
        if is_ours {
            changed = true;
            if kept.last().is_some_and(|last| last.trim() == CODEX_COMMENT) {
                kept.pop();
            }

            // The blank line that separated the block from the rest goes with it.
            if i + 1 < lines.len() && lines[i + 1].trim().is_empty() {
                i += 1;
            }

            i += 1;
            continue;
        }

        kept.push(lines[i]);
        i += 1;
    }

    if changed { HookEdit::new(kept.join("\n"), true) } else { HookEdit::new(toml, false) }
}

/// The top-level `notify` line, if there is one before the first table.
fn top_level_notify(toml: &str) -> Option<&str> {
    for raw in toml.split('\n') {
        let line = raw.trim();
        if line.starts_with('[') {
            return None;
        }

        if is_notify_line(line) {
            return Some(line);
        }
    }

    None
}

/// The C# regex `^notify\s*=`.
fn is_notify_line(line: &str) -> bool {
    line.strip_prefix("notify").is_some_and(|rest| rest.trim_start().starts_with('='))
}

#[cfg(test)]
mod tests {
    use super::*;

    const HOOK: &str = r"C:\Users\me\AppData\Local\Programs\Notch\Notch.Hook.exe";

    fn parse(text: &str) -> Value {
        serde_json::from_str(text).unwrap()
    }

    // From AgentDetectionTests.cs (AgentHookEditorTests).

    #[test]
    fn claude_gets_the_six_hooks_and_keeps_everything_else() {
        let existing = r#"{
  "model": "opus",
  "hooks": {
    "Stop": [ { "hooks": [ { "type": "command", "command": "my-script" } ] } ],
    "PreCompact": [ { "hooks": [ { "type": "command", "command": "other" } ] } ]
  }
}"#;

        let edit = add_claude_hooks(Some(existing), HOOK);

        assert!(edit.changed);
        let root = parse(&edit.text);
        assert_eq!(root["model"], "opus");
        assert_eq!(root["hooks"]["Stop"].as_array().unwrap().len(), 2);
        assert_eq!(root["hooks"]["PreCompact"].as_array().unwrap().len(), 1);
        for name in CLAUDE_EVENTS {
            assert!(
                root["hooks"][name]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|g| g["hooks"][0]["command"].as_str().unwrap().contains("Notch.Hook"))
            );
        }

        assert!(claude_has_hooks(Some(&edit.text)));
    }

    #[test]
    fn adding_twice_changes_nothing_the_second_time() {
        let first = add_claude_hooks(None, HOOK);
        let second = add_claude_hooks(Some(&first.text), HOOK);

        assert!(first.changed);
        assert!(!second.changed);
        assert_eq!(first.text, second.text);
    }

    #[test]
    fn removing_puts_the_file_back_as_far_as_content_goes() {
        let original = r#"{ "model": "opus", "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "mine" } ] } ] } }"#;

        let added = add_claude_hooks(Some(original), HOOK);
        let removed = remove_claude_hooks(Some(&added.text));

        assert!(removed.changed);
        assert_eq!(parse(original), parse(&removed.text));
        assert!(!claude_has_hooks(Some(&removed.text)));
    }

    #[test]
    fn a_file_that_is_all_ours_empties_out_without_leaving_an_empty_hooks_section() {
        let removed = remove_claude_hooks(Some(&add_claude_hooks(Some("{}"), HOOK).text));
        assert_eq!(parse(&removed.text).get("hooks"), None);
        assert_eq!(removed.text, "{}");
    }

    #[test]
    fn a_file_that_is_not_what_was_expected_is_never_changed() {
        for text in ["{ not json", "[1,2]", r#"{ "hooks": 5 }"#, r#"{ "hooks": { "Stop": "x" } }"#] {
            let edit = add_claude_hooks(Some(text), HOOK);
            assert!(!edit.changed, "{text}");
            assert_eq!(edit.text, text);
            assert!(edit.problem.is_some());
        }
    }

    #[test]
    fn claudes_file_is_written_as_the_csharp_app_writes_it() {
        let edit =
            add_claude_hooks(Some(r#"{"model":"o'pus","hooks":{"Stop":null,"Other":[]}}"#), r"C:\n\Notch.Hook.exe");
        let group = "[\r\n      {\r\n        \"hooks\": [\r\n          {\r\n            \"type\": \"command\",\r\n            \"command\": \"C:/n/Notch.Hook.exe\"\r\n          }\r\n        ]\r\n      }\r\n    ]";
        let hooks = ["Stop", "Other", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "SessionEnd"]
            .map(|name| format!("    \"{name}\": {}", if name == "Other" { "[]" } else { group }))
            .join(",\r\n");
        let expected = format!("{{\r\n  \"model\": \"o\\u0027pus\",\r\n  \"hooks\": {{\r\n{hooks}\r\n  }}\r\n}}");
        assert_eq!(edit.text, expected);

        // Taking them out keeps the other keys in their places.
        let removed = remove_claude_hooks(Some(
            r#"{"a":1,"hooks":{"x":[],"Stop":[{"hooks":[{"command":"Notch.Hook"}]}],"y":[1]},"b":2}"#,
        ));
        assert_eq!(
            removed.text,
            "{\r\n  \"a\": 1,\r\n  \"hooks\": {\r\n    \"y\": [\r\n      1\r\n    ]\r\n  },\r\n  \"b\": 2\r\n}"
        );
    }

    #[test]
    fn groups_that_are_not_objects_are_not_notchs() {
        let edit = add_claude_hooks(Some(r#"{"hooks":{"Stop":[1,[2],{"hooks":[3,{"command":4}]}]}}"#), HOOK);
        assert!(edit.changed);
        assert_eq!(parse(&edit.text)["hooks"]["Stop"].as_array().unwrap().len(), 4);
        assert!(!claude_has_hooks(Some("[1]")));
    }

    #[test]
    fn codex_gets_a_notify_line_before_any_table() {
        let existing = "model = \"o3\"\n\n[projects.x]\ntrust = true\n";
        let edit = add_codex_hook(Some(existing), HOOK);

        assert!(edit.changed);
        assert!(edit.text.starts_with("# Notch:"));
        assert!(edit.text.find("notify =").unwrap() < edit.text.find("[projects.x]").unwrap());
        assert!(codex_has_hook(Some(&edit.text)));
        assert!(edit.text.ends_with(existing));
    }

    #[test]
    fn codex_keeps_a_notify_command_of_the_users_own() {
        let existing = "notify = [\"my-notifier\"]\n";
        let edit = add_codex_hook(Some(existing), HOOK);

        assert!(!edit.changed);
        assert_eq!(edit.text, existing);
        assert!(edit.problem.is_some());
        assert!(!codex_has_hook(Some(existing)));
    }

    #[test]
    fn a_notify_line_inside_a_table_is_not_the_top_level_one() {
        let edit = add_codex_hook(Some("[tui]\nnotify = true\n"), HOOK);
        assert!(edit.changed);
    }

    #[test]
    fn codex_is_restored_exactly() {
        for original in ["", "model = \"o3\"\n", "model = \"o3\"\r\n\r\n[x]\r\ny = 1\r\n"] {
            let added = add_codex_hook(Some(original), HOOK);
            let removed = remove_codex_hook(Some(&added.text));
            assert_eq!(original.replace("\r\n", "\n"), removed.text.replace("\r\n", "\n"));
        }
    }

    #[test]
    fn codex_block_uses_the_files_newlines() {
        let edit = add_codex_hook(Some("a = 1\r\n"), r"C:\n\Notch.Hook.exe");
        assert_eq!(edit.text, format!("{CODEX_COMMENT}\r\nnotify = ['C:\\n\\Notch.Hook.exe']\r\n\r\na = 1\r\n"));
        assert!(add_codex_hook(None, "C:\\it's\\Notch.Hook.exe").problem.is_some());
    }

    #[test]
    fn removing_from_a_file_without_our_hook_does_nothing() {
        assert!(!remove_codex_hook(Some("notify = [\"mine\"]\n")).changed);
        assert!(
            !remove_claude_hooks(Some(r#"{ "hooks": { "Stop": [ { "hooks": [ { "command": "mine" } ] } ] } }"#))
                .changed
        );
    }

    #[test]
    fn paths_with_spaces_are_quoted_and_use_forward_slashes() {
        assert_eq!(
            shell_command(r"C:\Program Files\Notch\Notch.Hook.exe"),
            "\"C:/Program Files/Notch/Notch.Hook.exe\""
        );
        assert_eq!(shell_command(r"C:\n\Notch.Hook.exe"), "C:/n/Notch.Hook.exe");
    }
}
