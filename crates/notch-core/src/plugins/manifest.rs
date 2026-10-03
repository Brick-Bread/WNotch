//! The contents of a plugin's `plugin.json`, which describes it to Notch before any of its code runs.

use serde_json::{Map, Value};

use super::options::{self, OptionField};
use super::{json, PluginError, PROTOCOL_VERSION};

/// The manifest's file name.
pub const FILE_NAME: &str = "plugin.json";

const MAX_ID_LENGTH: usize = 64;
const SYSTEMS: [&str; 3] = ["windows", "linux", "macos"];

/// The program Notch starts for a plugin, and its arguments. Run with the plugin's folder as
/// the working directory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    /// A program on the `PATH`, or a path inside the plugin's folder (relative, with `/`).
    pub command: String,
    pub args: Vec<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Manifest {
    /// Unique and permanent, e.g. `acme.build-status`: lowercase letters and digits in groups
    /// separated by dots or dashes. Names the plugin's data folder and prefixes its activity ids.
    pub id: String,
    /// The name shown in settings.
    pub name: String,
    /// The protocol version the plugin was written for.
    pub protocol: u32,
    /// The plugin's own version, for display only.
    pub version: Option<String>,
    pub author: Option<String>,
    pub description: Option<String>,
    /// Where the plugin is published, as `owner/repo`, so Notch can look for updates to a copy
    /// that was not installed from GitHub.
    pub repository: Option<String>,
    /// What to run on the operating system Notch is running on.
    pub entry: Entry,
    /// Options the user can change in Notch's settings.
    pub options: Vec<OptionField>,
    /// True when the plugin handles `options` messages, so changed options reach it while it
    /// runs. Otherwise it is restarted to read the new values.
    pub live_options: bool,
}

impl Manifest {
    /// Reads and validates a manifest for the operating system Notch runs on.
    pub fn parse(text: &str) -> Result<Manifest, PluginError> {
        Self::parse_for(text, std::env::consts::OS)
    }

    /// Reads and validates a manifest, resolving the entry for `os` (`windows`, `linux` or `macos`).
    pub fn parse_for(text: &str, os: &str) -> Result<Manifest, PluginError> {
        let value: Value = serde_json::from_str(&json::relax(text))
            .map_err(|e| PluginError::new(format!("{FILE_NAME} could not be read: {e}")))?;
        if value.is_null() {
            return Err(PluginError::new(format!("{FILE_NAME} is empty.")));
        }
        if !value.is_object() {
            return Err(PluginError::new(format!("{FILE_NAME} must hold a JSON object.")));
        }

        let id = require(&value, "id")?;
        if id.len() > MAX_ID_LENGTH || !is_valid_id(&id) {
            return Err(PluginError::new(format!(
                "The id '{id}' is not valid: use lowercase letters and digits, separated by dots or dashes, at most {MAX_ID_LENGTH} characters."
            )));
        }
        let name = require(&value, "name")?;

        let protocol = match value.get("protocol") {
            None => return Err(PluginError::new(format!("{FILE_NAME} is missing \"protocol\"."))),
            Some(Value::Number(n)) => n.as_u64().and_then(|n| u32::try_from(n).ok()).filter(|n| *n >= 1),
            Some(_) => None,
        }
        .ok_or_else(|| PluginError::new("\"protocol\" must be 1 or higher."))?;
        if protocol > PROTOCOL_VERSION {
            return Err(PluginError::new(format!(
                "Needs plugin protocol {protocol}, but this version of Notch speaks {PROTOCOL_VERSION}. Update Notch to use it."
            )));
        }

        Ok(Manifest {
            name,
            protocol,
            version: optional(&value, "version"),
            author: optional(&value, "author"),
            description: optional(&value, "description"),
            repository: optional(&value, "repository"),
            entry: parse_entry(value.get("entry"), os)?,
            options: options::parse_all(value.get("options")),
            live_options: value.get("liveOptions").and_then(Value::as_bool).unwrap_or(false),
            id,
        })
    }
}

/// Lowercase letters and digits in groups separated by single dots or dashes.
pub fn is_valid_id(id: &str) -> bool {
    !id.is_empty()
        && id
            .split(['.', '-'])
            .all(|group| !group.is_empty() && group.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit()))
}

fn require(value: &Value, property: &str) -> Result<String, PluginError> {
    optional(value, property).ok_or_else(|| PluginError::new(format!("{FILE_NAME} is missing \"{property}\".")))
}

fn optional(value: &Value, property: &str) -> Option<String> {
    let text = value.get(property)?.as_str()?.trim();
    (!text.is_empty()).then(|| text.to_owned())
}

/// `"entry"` is an object `{ "command", "args" }`, optionally with `windows`, `linux` and
/// `macos` objects that replace the command and arguments on that system; or an array of strings
/// (the command followed by its arguments).
fn parse_entry(entry: Option<&Value>, os: &str) -> Result<Entry, PluginError> {
    let missing = || PluginError::new(format!("{FILE_NAME} is missing \"entry\", the command that starts the plugin."));
    let shape = || PluginError::new("\"entry\" must be { \"command\": ..., \"args\": [...] } or an array of strings.");
    let entry = entry.ok_or_else(missing)?;

    let chosen = match entry {
        Value::Object(map) => map.get(os).unwrap_or(entry),
        _ => entry,
    };

    let (command, args) = match chosen {
        Value::Array(items) => {
            let strings: Option<Vec<String>> = items.iter().map(|item| item.as_str().map(str::to_owned)).collect();
            let mut strings = strings.ok_or_else(shape)?;
            if strings.is_empty() {
                return Err(missing());
            }
            let command = strings.remove(0);
            (command, strings)
        }
        Value::Object(map) => {
            let Some(command) = map.get("command").and_then(Value::as_str) else {
                return Err(if has_other_system(map, os) {
                    PluginError::new(format!("The plugin has no \"entry\" for this system ({os})."))
                } else {
                    missing()
                });
            };
            let args = match map.get("args") {
                None => Vec::new(),
                Some(Value::Array(items)) => {
                    let strings: Option<Vec<String>> = items.iter().map(|item| item.as_str().map(str::to_owned)).collect();
                    strings.ok_or_else(shape)?
                }
                Some(_) => return Err(shape()),
            };
            (command.to_owned(), args)
        }
        _ => return Err(shape()),
    };

    let command = command.trim().to_owned();
    if command.is_empty() {
        return Err(missing());
    }
    if !is_allowed_command(&command) {
        return Err(PluginError::new(
            "\"entry\" must name a program on the PATH, or a path inside the plugin's folder written with /.",
        ));
    }
    Ok(Entry { command, args })
}

fn has_other_system(map: &Map<String, Value>, os: &str) -> bool {
    SYSTEMS.iter().any(|name| *name != os && map.contains_key(*name))
}

/// A bare program name, or a relative path inside the plugin's folder.
fn is_allowed_command(command: &str) -> bool {
    if command.contains('\\') {
        return false;
    }
    if !command.contains('/') {
        return true;
    }
    let path = command.strip_prefix("./").unwrap_or(command);
    !path.starts_with('/') && !path.contains(':') && path.split('/').all(|part| !part.is_empty() && part != "..")
}

#[cfg(test)]
mod tests {
    use super::*;

    const VALID: &str = r#"{
        // comments and trailing commas are fine
        "id": "acme.build-status",
        "name": "Build status",
        "version": "1.2.0",
        "protocol": 1,
        "entry": { "command": "python", "args": ["main.py"], "windows": { "command": "py", "args": ["-3", "main.py"] } },
    }"#;

    fn error(json: &str) -> String {
        Manifest::parse_for(json, "linux").unwrap_err().0
    }

    #[test]
    fn parses_a_valid_manifest() {
        let manifest = Manifest::parse_for(VALID, "linux").unwrap();
        assert_eq!(manifest.id, "acme.build-status");
        assert_eq!(manifest.name, "Build status");
        assert_eq!(manifest.version.as_deref(), Some("1.2.0"));
        assert_eq!(manifest.protocol, 1);
        assert_eq!(manifest.author, None);
        assert!(!manifest.live_options);
        assert_eq!(manifest.entry, Entry { command: "python".into(), args: vec!["main.py".into()] });
    }

    #[test]
    fn the_entry_can_differ_per_system() {
        let manifest = Manifest::parse_for(VALID, "windows").unwrap();
        assert_eq!(manifest.entry, Entry { command: "py".into(), args: vec!["-3".into(), "main.py".into()] });
        assert_eq!(Manifest::parse_for(VALID, "macos").unwrap().entry.command, "python");
    }

    #[test]
    fn the_entry_can_be_an_array() {
        let manifest =
            Manifest::parse_for(r#"{ "id": "x", "name": "X", "protocol": 1, "entry": ["./bin/x", "--quiet"] }"#, "linux")
                .unwrap();
        assert_eq!(manifest.entry, Entry { command: "./bin/x".into(), args: vec!["--quiet".into()] });
    }

    #[test]
    fn a_plugin_for_other_systems_only_says_so() {
        let json = r#"{ "id": "x", "name": "X", "protocol": 1, "entry": { "windows": { "command": "x.exe" } } }"#;
        assert!(error(json).contains("no \"entry\" for this system"));
        assert!(Manifest::parse_for(json, "windows").is_ok());
    }

    #[test]
    fn rejects_an_unusable_manifest() {
        let cases = [
            ("not json", "could not be read"),
            ("null", "empty"),
            ("[1]", "JSON object"),
            (r#"{ "name": "X", "protocol": 1, "entry": ["x"] }"#, "\"id\""),
            (r#"{ "id": "x", "protocol": 1, "entry": ["x"] }"#, "\"name\""),
            (r#"{ "id": "x", "name": "X", "entry": ["x"] }"#, "\"protocol\""),
            (r#"{ "id": "x", "name": "X", "protocol": 0, "entry": ["x"] }"#, "1 or higher"),
            (r#"{ "id": "x", "name": "X", "protocol": 1 }"#, "\"entry\""),
            (r#"{ "id": "x", "name": "X", "protocol": 1, "entry": {} }"#, "\"entry\""),
            (r#"{ "id": "Has Spaces", "name": "X", "protocol": 1, "entry": ["x"] }"#, "not valid"),
            (r#"{ "id": "..", "name": "X", "protocol": 1, "entry": ["x"] }"#, "not valid"),
            (r#"{ "id": "a..b", "name": "X", "protocol": 1, "entry": ["x"] }"#, "not valid"),
            (r#"{ "id": "x", "name": "X", "protocol": 1, "entry": ["../x"] }"#, "inside the plugin's folder"),
            (r#"{ "id": "x", "name": "X", "protocol": 1, "entry": ["/usr/bin/x"] }"#, "inside the plugin's folder"),
            (r#"{ "id": "x", "name": "X", "protocol": 1, "entry": ["bin\\x"] }"#, "inside the plugin's folder"),
            (r#"{ "id": "x", "name": "X", "protocol": 1, "entry": [1] }"#, "array of strings"),
        ];
        for (json, expected) in cases {
            let message = error(json);
            assert!(message.contains(expected), "{json}: {message}");
        }
    }

    #[test]
    fn rejects_a_plugin_written_for_a_newer_protocol() {
        let json = format!(r#"{{ "id": "x", "name": "X", "protocol": {}, "entry": ["x"] }}"#, PROTOCOL_VERSION + 1);
        assert!(error(&json).contains("Update Notch"));
    }

    #[test]
    fn reads_options_and_the_live_flag() {
        let manifest = Manifest::parse_for(
            r#"{ "id": "x", "name": "X", "protocol": 1, "entry": ["x"], "liveOptions": true,
                 "repository": "acme/hello", "options": [ { "key": "a" } ] }"#,
            "linux",
        )
        .unwrap();
        assert!(manifest.live_options);
        assert_eq!(manifest.repository.as_deref(), Some("acme/hello"));
        assert_eq!(manifest.options.len(), 1);
    }

    #[test]
    fn ids_are_lowercase_groups() {
        assert!(is_valid_id("acme.build-status"));
        assert!(is_valid_id("x"));
        assert!(!is_valid_id(""));
        assert!(!is_valid_id("-a"));
        assert!(!is_valid_id("a."));
        assert!(!is_valid_id("A"));
        assert!(!is_valid_id("a_b"));
    }
}
