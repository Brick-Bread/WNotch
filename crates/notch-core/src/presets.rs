//! The launcher buttons of the Terminal tab. A line is
//! `Name = [VARIABLE=value ...] command [arguments]`, for example
//! `Work Claude = CLAUDE_CONFIG_DIR=C:\work\.claude claude`. Arguments with spaces go in double quotes.
//! A command with "claude" or "codex" in its name is treated as that agent, so forks get status tracking too.
//!
//! The command word `shell` stands for the platform shell; the app resolves it, the core does not.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

use crate::agents::AgentKind;

/// As many buttons as fit beside the folder button.
const MAX_PRESETS: usize = 6;

/// The preset list new installs start with, in the form [`parse_preset`] reads.
pub const DEFAULT_PRESETS: [&str; 3] = ["Claude = claude", "Codex = codex", "Shell = shell"];

/// Something the built-in terminal can launch.
#[derive(Clone, PartialEq, Eq, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalProfile {
    pub id: String,
    pub display_name: String,
    /// Bare command name, resolved through PATH when the session starts.
    pub command: String,
    pub glyph: String,
    pub agent: AgentKind,
    /// Shown when `command` is not installed.
    pub install_hint: String,
    /// Passed to `command` ahead of the ones Notch adds for its hooks.
    pub arguments: Vec<String>,
    /// Variables set for this session only, such as a different config folder per account.
    pub environment: BTreeMap<String, String>,
}

/// The presets a settings list describes: unreadable lines skipped, no more than fit, and the defaults when nothing is left.
pub fn profiles_from_lines(lines: &[String]) -> Vec<TerminalProfile> {
    let mut profiles: Vec<TerminalProfile> = Vec::new();
    for line in lines {
        if profiles.len() >= MAX_PRESETS {
            break;
        }
        let taken: Vec<&str> = profiles.iter().map(|p| p.id.as_str()).collect();
        if let Some(profile) = parse_preset(line, &taken) {
            profiles.push(profile);
        }
    }

    if profiles.is_empty() {
        let defaults: Vec<String> = DEFAULT_PRESETS.iter().map(|l| (*l).to_owned()).collect();
        return profiles_from_lines(&defaults);
    }
    profiles
}

/// Reads one preset line, or `None` when it is not one.
///
/// `taken_ids` are the ids already in use, so that two presets with the same name stay apart.
pub fn parse_preset(line: &str, taken_ids: &[&str]) -> Option<TerminalProfile> {
    let equals = line.find('=').filter(|&i| i > 0)?;
    let name = line[..equals].trim();
    let words = split(&line[equals + 1..])?;
    if name.is_empty() || words.is_empty() {
        return None;
    }

    let mut environment = BTreeMap::new();
    let mut next = 0;
    while next + 1 < words.len() {
        let Some((variable, value)) = read_variable(&words[next]) else {
            break;
        };
        // Variable names are case-insensitive: the last spelling wins.
        environment.retain(|k: &String, _| !k.eq_ignore_ascii_case(variable));
        environment.insert(variable.to_owned(), value.to_owned());
        next += 1;
    }

    let command = words[next].clone();
    let agent = agent_for(&command);
    let install_hint =
        format!("{command} was not found on your PATH. Check the command for \"{name}\" in the settings.");

    Some(TerminalProfile {
        id: unique_id(name, taken_ids),
        display_name: name.to_owned(),
        glyph: glyph_for(agent).to_owned(),
        command,
        agent,
        install_hint,
        arguments: words[next + 1..].to_vec(),
        environment,
    })
}

/// Back to the line a profile came from, for showing it in the settings.
pub fn to_line(profile: &TerminalProfile) -> String {
    let mut parts: Vec<String> = profile
        .environment
        .iter()
        .map(|(key, value)| quote(&format!("{key}={value}")))
        .collect();
    parts.push(quote(&profile.command));
    parts.extend(profile.arguments.iter().map(|a| quote(a)));
    format!("{} = {}", profile.display_name, parts.join(" "))
}

fn agent_for(command: &str) -> AgentKind {
    // Both separators, whatever the platform: a preset written on Windows may be read elsewhere.
    let file = command.rsplit(['/', '\\']).next().unwrap_or(command);
    let stem = file.rfind('.').map_or(file, |dot| &file[..dot]).to_lowercase();
    if stem.contains("claude") {
        AgentKind::Claude
    } else if stem.contains("codex") {
        AgentKind::Codex
    } else {
        AgentKind::None
    }
}

fn glyph_for(agent: AgentKind) -> &'static str {
    match agent {
        AgentKind::Claude => "✳",
        AgentKind::Codex => "◈",
        AgentKind::None => "▸",
    }
}

fn unique_id(name: &str, taken_ids: &[&str]) -> String {
    let slug: String = name
        .to_lowercase()
        .chars()
        .map(|c| if c.is_alphanumeric() { c } else { '-' })
        .collect();
    let slug = slug.trim_matches('-');
    let slug = if slug.is_empty() { "terminal" } else { slug };

    let mut id = slug.to_owned();
    let mut n = 2;
    while taken_ids.contains(&id.as_str()) {
        id = format!("{slug}-{n}");
        n += 1;
    }
    id
}

/// `NAME=value`, where NAME looks like an environment variable rather than a path or a flag.
fn read_variable(word: &str) -> Option<(&str, &str)> {
    let (name, value) = word.split_once('=')?;
    let valid = !name.is_empty()
        && !name.starts_with(|c: char| c.is_numeric())
        && name.chars().all(|c| c.is_alphanumeric() || c == '_');
    valid.then_some((name, value))
}

/// Splits on whitespace outside double quotes; `None` when a quote is left open.
fn split(text: &str) -> Option<Vec<String>> {
    let mut words = Vec::new();
    let mut current = String::new();
    let mut in_word = false;
    let mut quoted = false;

    for c in text.chars() {
        if c == '"' {
            quoted = !quoted;
            in_word = true;
        } else if c.is_whitespace() && !quoted {
            if in_word {
                words.push(std::mem::take(&mut current));
                in_word = false;
            }
        } else {
            current.push(c);
            in_word = true;
        }
    }

    if quoted {
        return None;
    }
    if in_word {
        words.push(current);
    }
    Some(words)
}

fn quote(word: &str) -> String {
    if word.is_empty() || word.chars().any(char::is_whitespace) {
        format!("\"{word}\"")
    } else {
        word.to_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lines(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| (*s).to_owned()).collect()
    }

    fn parse(line: &str) -> Option<TerminalProfile> {
        parse_preset(line, &[])
    }

    #[test]
    fn reads_environment_arguments_and_agent() {
        let profile = parse(r#"Work Claude = CLAUDE_CONFIG_DIR="C:\my work\.claude" claude --model opus"#)
            .expect("parses");

        assert_eq!(profile.display_name, "Work Claude");
        assert_eq!(profile.id, "work-claude");
        assert_eq!(profile.command, "claude");
        assert_eq!(profile.agent, AgentKind::Claude);
        assert_eq!(profile.glyph, "✳");
        assert_eq!(profile.arguments, ["--model", "opus"]);
        assert_eq!(profile.environment["CLAUDE_CONFIG_DIR"], r"C:\my work\.claude");

        let fork = profiles_from_lines(&lines(&[r"Fork = C:\bin\my-codex.exe"]));
        assert_eq!(fork[0].agent, AgentKind::Codex);
        assert_eq!(fork[0].glyph, "◈");
    }

    #[test]
    fn plain_commands_are_not_agents() {
        let profile = parse("Shell = shell").expect("parses");
        assert_eq!((profile.agent, profile.glyph.as_str()), (AgentKind::None, "▸"));
        // A directory called "claude" does not make the command one.
        assert_eq!(parse("X = /opt/claude/bin/tool").expect("parses").agent, AgentKind::None);
    }

    #[test]
    fn round_trips_and_rejects_bad_lines() {
        let line = r#"Work Claude = "A=b c" claude "two words""#;
        let profile = parse(line).expect("parses");
        assert_eq!(to_line(&profile), line);

        assert!(parse("no equals sign").is_none());
        assert!(parse("Empty =").is_none());
        assert!(parse("= claude").is_none());
        assert!(parse(r#"Open = claude "quote"#).is_none());
    }

    #[test]
    fn last_word_is_the_command_even_if_it_looks_like_a_variable() {
        let profile = parse("V = A=b").expect("parses");
        assert_eq!(profile.command, "A=b");
        assert!(profile.environment.is_empty());

        let profile = parse("V = a=1 A=2 run 3=x").expect("parses");
        assert_eq!(profile.environment.len(), 1);
        assert_eq!(profile.environment["A"], "2");
        assert_eq!(profile.arguments, ["3=x"]);
    }

    #[test]
    fn keeps_ids_apart_and_falls_back_to_the_defaults() {
        let ids = |l: &[&str]| -> Vec<String> {
            profiles_from_lines(&lines(l)).into_iter().map(|p| p.id).collect()
        };
        assert_eq!(ids(&["A = x", "A = y"]), ["a", "a-2"]);
        assert_eq!(ids(&["garbage"]), ["claude", "codex", "shell"]);
        assert_eq!(ids(&[]), ["claude", "codex", "shell"]);
        assert_eq!(ids(&["!!! = x"]), ["terminal"]);

        let many: Vec<String> = (0..20).map(|i| format!("P{i} = x")).collect();
        assert_eq!(profiles_from_lines(&many).len(), MAX_PRESETS);
    }

    #[test]
    fn serialises_for_the_ui() {
        let profile = parse("Claude = claude").expect("parses");
        let json = serde_json::to_value(&profile).expect("serialises");
        assert_eq!(json["displayName"], "Claude");
        assert_eq!(json["agent"], "claude");
        assert!(json["installHint"].is_string());
    }
}
