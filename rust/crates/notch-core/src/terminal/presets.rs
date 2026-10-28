//! Reads the launcher buttons of the Terminal tab from settings. Ported from
//! `src/Notch.Core/Terminal/TerminalPresets.cs`.
//!
//! A line is `Name = [VARIABLE=value ...] command [arguments]`, for example
//! `Work Claude = CLAUDE_CONFIG_DIR=C:\work\.claude claude`. Arguments with spaces go in double
//! quotes. A command with "claude" or "codex" in its name is treated as that agent, so forks get
//! status tracking too.

use indexmap::IndexMap;

use super::{DEFAULT_PRESETS, TerminalProfile, windows_path};
use crate::agents::{AgentKind, catalog, ignore_case};

/// As many buttons as fit beside the folder button.
pub const MAX_PRESETS: usize = 6;

/// The presets a settings list describes: unreadable lines skipped, no more than fit, and the defaults when nothing is left.
pub fn from_lines<S: AsRef<str>>(lines: &[S]) -> Vec<TerminalProfile> {
    let mut profiles: Vec<TerminalProfile> = Vec::new();
    for line in lines {
        if profiles.len() < MAX_PRESETS {
            let taken: Vec<&str> = profiles.iter().map(|p| p.id.as_str()).collect();
            if let Some(profile) = try_parse(line.as_ref(), &taken) {
                profiles.push(profile);
            }
        }
    }

    if profiles.is_empty() { from_lines(&DEFAULT_PRESETS) } else { profiles }
}

/// `taken_ids`: ids already in use, so that two presets with the same name stay apart.
pub fn try_parse(line: &str, taken_ids: &[&str]) -> Option<TerminalProfile> {
    let equals = line.find('=').filter(|&i| i > 0)?;

    let name = line[..equals].trim();
    let words = split(&line[equals + 1..])?;
    if name.is_empty() || words.is_empty() {
        return None;
    }

    let mut environment: IndexMap<String, String> = IndexMap::new();
    let mut next = 0;
    while next < words.len() - 1 {
        let Some((variable, value)) = read_variable(&words[next]) else {
            break;
        };

        // The C# dictionary ignores case: a repeat keeps the first spelling and takes the new value.
        match environment.keys().position(|key| ignore_case::eq(key, variable)) {
            Some(index) => environment[index] = value.to_owned(),
            None => {
                environment.insert(variable.to_owned(), value.to_owned());
            }
        }
        next += 1;
    }

    let command = words[next].clone();
    let agent = agent_for(&command);
    let id = unique_id(name, taken_ids);
    let install_hint =
        format!("{command} was not found on your PATH. Check the command for \"{name}\" in the settings.");

    Some(TerminalProfile {
        id,
        display_name: name.to_owned(),
        glyph: glyph_for(agent).to_owned(),
        agent,
        install_hint,
        arguments: words[next + 1..].to_vec(),
        environment,
        command,
    })
}

/// Back to the line a profile came from, for showing it in the settings.
pub fn to_line(profile: &TerminalProfile) -> String {
    let mut parts: Vec<String> = profile.environment.iter().map(|(k, v)| quote(&format!("{k}={v}"))).collect();
    parts.push(quote(&profile.command));
    parts.extend(profile.arguments.iter().map(|a| quote(a)));
    format!("{} = {}", profile.display_name, parts.join(" "))
}

fn agent_for(command: &str) -> AgentKind {
    let file = windows_path::file_name_without_extension(command);
    if ignore_case::contains(file, "claude") {
        return AgentKind::Claude;
    }

    // Any other agent Notch knows by name (Codex, Gemini, Aider).
    catalog::for_command(command).map_or(AgentKind::None, |a| a.kind)
}

fn glyph_for(agent: AgentKind) -> &'static str {
    match agent {
        AgentKind::Claude | AgentKind::Gemini => "\u{E99A}",
        AgentKind::Codex | AgentKind::Aider => "\u{E943}",
        _ => "\u{E756}",
    }
}

fn unique_id(name: &str, taken_ids: &[&str]) -> String {
    let mut slug = String::new();
    for c in name.to_lowercase().chars() {
        // C# looks at UTF-16 units, so each half of a character outside the BMP becomes a '-'.
        match c.len_utf16() {
            1 if c.is_alphanumeric() => slug.push(c),
            units => slug.push_str(&"-".repeat(units)),
        }
    }

    let mut slug = slug.trim_matches('-').to_owned();
    if slug.is_empty() {
        slug = "terminal".into();
    }

    let mut id = slug.clone();
    let mut n = 2;
    while taken_ids.contains(&id.as_str()) {
        id = format!("{slug}-{n}");
        n += 1;
    }

    id
}

/// NAME=value, where NAME looks like an environment variable rather than a path or a flag.
fn read_variable(word: &str) -> Option<(&str, &str)> {
    let (name, value) = word.split_once('=')?;
    let valid = !name.is_empty()
        && !name.starts_with(|c: char| c.is_numeric())
        && name.chars().all(|c| (c.len_utf16() == 1 && c.is_alphanumeric()) || c == '_');
    valid.then_some((name, value))
}

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
    if word.is_empty() || word.chars().any(char::is_whitespace) { format!("\"{word}\"") } else { word.to_owned() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn presets_read_environment_arguments_and_agent() {
        let profile =
            try_parse(r#"Work Claude = CLAUDE_CONFIG_DIR="C:\my work\.claude" claude --model opus"#, &[]).unwrap();

        assert_eq!(
            (profile.display_name.as_str(), profile.id.as_str(), profile.command.as_str(), profile.agent),
            ("Work Claude", "work-claude", "claude", AgentKind::Claude)
        );
        assert_eq!(profile.arguments, ["--model", "opus"]);
        assert_eq!(profile.environment["CLAUDE_CONFIG_DIR"], r"C:\my work\.claude");
        assert_eq!(from_lines(&[r"Fork = C:\bin\my-codex.exe"])[0].agent, AgentKind::Codex);
    }

    #[test]
    fn presets_round_trip_and_reject_bad_lines() {
        let line = r#"Work Claude = "A=b c" claude "two words""#;
        let profile = try_parse(line, &[]).unwrap();
        assert_eq!(to_line(&profile), line);

        assert!(try_parse("no equals sign", &[]).is_none());
        assert!(try_parse("Empty =", &[]).is_none());
        assert!(try_parse("Open = claude \"quote", &[]).is_none());
    }

    #[test]
    fn presets_keep_ids_apart_and_fall_back_to_the_defaults() {
        let ids = |lines: &[&str]| from_lines(lines).into_iter().map(|p| p.id).collect::<Vec<_>>();
        assert_eq!(ids(&["A = x", "A = y"]), ["a", "a-2"]);
        assert_eq!(ids(&["garbage"]), ["claude", "codex", "opencode", "shell"]);
        let many: Vec<String> = (0..20).map(|i| format!("P{i} = x")).collect();
        assert_eq!(from_lines(&many).len(), MAX_PRESETS);
    }

    #[test]
    fn a_repeated_variable_keeps_its_first_spelling() {
        let profile = try_parse("X = a=1 A=2 b=3 cmd", &[]).unwrap();
        assert_eq!(
            profile.environment.into_iter().collect::<Vec<_>>(),
            [("a".into(), "2".into()), ("b".into(), "3".into())]
        );
        assert_eq!(try_parse("x𝐀 = cmd", &[]).unwrap().id, "x");
        assert_eq!(try_parse("𝐀 = cmd", &[]).unwrap().id, "terminal");
    }

    // From OpenCodeTests.cs.

    #[test]
    fn a_launcher_button_for_opencode_is_an_opencode_agent() {
        assert_eq!(try_parse("opencode = opencode", &[]).unwrap().agent, AgentKind::OpenCode);

        let work = try_parse(r"Work = OPENCODE_CONFIG_DIR=C:\work\oc opencode --continue", &[]).unwrap();
        assert_eq!(work.agent, AgentKind::OpenCode);
        assert_eq!(catalog::for_command("opencode").map(|a| a.kind), Some(AgentKind::OpenCode));
    }

    #[test]
    fn new_installs_get_an_opencode_button_and_the_built_in_profile_matches_it() {
        assert!(DEFAULT_PRESETS.contains(&"opencode = opencode"));
        assert_eq!(TerminalProfile::open_code().agent, AgentKind::OpenCode);
        assert!(TerminalProfile::all().contains(&TerminalProfile::open_code()));
        assert!(TerminalProfile::open_code().install_hint.contains("opencode.ai"));
        assert_eq!(from_lines(&DEFAULT_PRESETS).len(), DEFAULT_PRESETS.len());
    }
}
