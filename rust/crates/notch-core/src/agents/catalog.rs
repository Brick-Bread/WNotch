//! The agents Notch recognises, and the rules that tell them apart from lookalikes. Ported from
//! `src/Notch.Core/Agents/AgentCatalog.cs`.

use super::detector::ProcessSnapshot;
use super::{AgentKind, ignore_case};
use crate::terminal::windows_path;

/// A coding agent Notch knows how to recognise among the running programs.
#[derive(Debug, PartialEq, Eq)]
pub struct AgentDefinition {
    /// Stable name used in settings, e.g. "claude".
    pub id: &'static str,
    pub display_name: &'static str,
    pub kind: AgentKind,
    pub glyph: &'static str,
    /// Program names (no .exe, lowercase) that are the agent itself.
    pub executable_names: &'static [&'static str],
    /// Text found in the command line when the agent runs inside a generic runtime such as node or
    /// python, e.g. `@anthropic-ai/claude-code`. Lowercase.
    pub command_line_markers: &'static [&'static str],
}

/// Programs that run an agent written in a scripting language; their command line says which.
const RUNTIMES: [&str; 10] = ["node", "bun", "deno", "python", "python3", "pythonw", "py", "uv", "uvx", "npx"];

/// Folders whose programs are not command line agents: the Claude desktop app is also called
/// claude.exe and carries its own copy of Claude Code for its Code tab, and the Codex desktop
/// app keeps its servers in its own folder.
const EXCLUDED_PATHS: [&str; 6] = [
    "\\anthropicclaude\\",
    "\\windowsapps\\",
    "\\claude desktop\\",
    "/applications/claude.app/",
    "\\roaming\\claude\\claude-code\\",
    "\\openai\\codex\\",
];

/// Command line text that means a program runs as a server, or for one answer, and not as a
/// session the user sits in: Codex's app server, an agent another program drives over
/// stdin and stdout, and print mode.
const BACKGROUND_MARKERS: [&str; 11] = [
    " app-server",
    " exec-server",
    " mcp-server",
    " mcp serve",
    " --input-format stream-json",
    " --input-format=stream-json",
    " --output-format stream-json",
    " --output-format=stream-json",
    " --sdk-url",
    " --print",
    " -p ",
];

pub static ALL: [AgentDefinition; 5] = [
    AgentDefinition {
        id: "claude",
        display_name: "Claude",
        kind: AgentKind::Claude,
        glyph: "\u{E99A}",
        executable_names: &["claude"],
        command_line_markers: &[
            "@anthropic-ai/claude-code",
            "@anthropic-ai\\claude-code",
            "claude-code/cli",
            "claude-code\\cli",
        ],
    },
    AgentDefinition {
        id: "codex",
        display_name: "Codex",
        kind: AgentKind::Codex,
        glyph: "\u{E943}",
        executable_names: &["codex"],
        command_line_markers: &["@openai/codex", "@openai\\codex"],
    },
    AgentDefinition {
        id: "gemini",
        display_name: "Gemini",
        kind: AgentKind::Gemini,
        glyph: "\u{E99A}",
        executable_names: &["gemini"],
        command_line_markers: &["@google/gemini-cli", "@google\\gemini-cli", "gemini-cli"],
    },
    AgentDefinition {
        id: "aider",
        display_name: "Aider",
        kind: AgentKind::Aider,
        glyph: "\u{E943}",
        executable_names: &["aider"],
        command_line_markers: &["aider-chat", "-m aider", "scripts\\aider", "bin/aider", "aider\\main", "aider/main"],
    },
    AgentDefinition {
        id: "opencode",
        display_name: "opencode",
        kind: AgentKind::OpenCode,
        glyph: "\u{E756}",
        executable_names: &["opencode"],
        command_line_markers: &["opencode-ai", "@opencode/cli", "@opencode\\cli"],
    },
];

/// opencode programs that are not a session the user sits in: its background server (also what a
/// `--standalone` session starts), one-off runs, and its management commands. Looked at
/// by subcommand rather than as loose text, so a prompt that happens to say "run" changes nothing.
const OPEN_CODE_SUBCOMMANDS: [&str; 18] = [
    "serve",
    "service",
    "acp",
    "api",
    "run",
    "models",
    "stats",
    "session",
    "upgrade",
    "update",
    "uninstall",
    "auth",
    "mcp",
    "plugin",
    "debug",
    "reload",
    "pair",
    "completions",
];

/// opencode options that take a value, whose value is not a subcommand.
const OPEN_CODE_VALUE_OPTIONS: [&str; 12] = [
    "--session",
    "-s",
    "--prompt",
    "--server",
    "--model",
    "-m",
    "--agent",
    "--log-level",
    "--title",
    "--file",
    "-f",
    "--format",
];

pub fn by_id(id: &str) -> Option<&'static AgentDefinition> {
    ALL.iter().find(|a| ignore_case::eq(a.id, id))
}

pub fn for_kind(kind: AgentKind) -> Option<&'static AgentDefinition> {
    ALL.iter().find(|a| a.kind == kind)
}

/// The agent a command in a terminal button starts, going by the program's file name.
pub fn for_command(command: &str) -> Option<&'static AgentDefinition> {
    let file = windows_path::file_name_without_extension(command);
    ALL.iter().find(|a| a.executable_names.iter().any(|n| ignore_case::contains(file, n)))
}

/// The agent this running program is, or `None` when it is not one.
pub fn matches(process: &ProcessSnapshot) -> Option<&'static AgentDefinition> {
    let name = name_without_extension(&process.name);
    let command_line = process.command_line.as_deref().unwrap_or("").to_lowercase();

    // Chromium and Electron start helper processes of themselves with --type=; those are never an agent.
    if command_line.contains(" --type=") {
        return None;
    }

    // Padded so a marker at the very end of the command line matches too.
    let padded = format!("{command_line} ");
    if BACKGROUND_MARKERS.iter().any(|marker| padded.contains(marker)) {
        return None;
    }

    let path = process.executable_path.as_deref().unwrap_or("");
    if EXCLUDED_PATHS.iter().any(|excluded| ignore_case::contains(path, excluded)) {
        return None;
    }

    if let Some(agent) = ALL.iter().find(|a| a.executable_names.contains(&name.as_str())) {
        return not_in_background(agent, &command_line);
    }

    if !RUNTIMES.contains(&name.as_str()) {
        return None;
    }

    let agent = ALL.iter().find(|a| a.command_line_markers.iter().any(|m| command_line.contains(m)))?;
    not_in_background(agent, &command_line)
}

/// The agent, unless this is opencode run as a server or a one-off command.
fn not_in_background(agent: &'static AgentDefinition, command_line: &str) -> Option<&'static AgentDefinition> {
    if agent.kind == AgentKind::OpenCode && is_open_code_background(command_line) { None } else { Some(agent) }
}

/// Whether an opencode command line is a subcommand other than the interactive session.
pub(crate) fn is_open_code_background(command_line: &str) -> bool {
    let mut skip_next = false;

    // The first token is the program itself (or, for a script, the runtime that runs it).
    for token in tokens(command_line).skip(1) {
        let word = token.trim_matches('"');
        if skip_next {
            skip_next = false;
            continue;
        }

        if word.starts_with('-') {
            skip_next = OPEN_CODE_VALUE_OPTIONS.iter().any(|o| ignore_case::eq(o, word)) && !word.contains('=');
            continue;
        }

        // The first word that is not an option: a subcommand, or the folder to start in.
        if OPEN_CODE_SUBCOMMANDS.iter().any(|s| ignore_case::eq(s, word)) {
            return true;
        }

        if word.contains(['\\', '/', ':', '.']) {
            // A path or file name (the folder to start in, or the script a runtime was given): keep looking.
            continue;
        }

        return false;
    }

    false
}

/// The matches of the C# regex `"[^"]*"|\S+`: a quoted run, or else a run of non-space characters.
fn tokens(text: &str) -> impl Iterator<Item = &str> {
    let mut rest = text;
    std::iter::from_fn(move || {
        rest = rest.trim_start();
        if rest.is_empty() {
            return None;
        }

        let end = rest
            .strip_prefix('"')
            .and_then(|after| after.find('"'))
            .map(|close| close + 2)
            .unwrap_or_else(|| rest.find(char::is_whitespace).unwrap_or(rest.len()));
        let (token, after) = rest.split_at(end);
        rest = after;
        Some(token)
    })
}

/// Whether the name could be a runtime whose command line must be read to tell what it runs.
pub fn is_runtime(process_name: &str) -> bool {
    RUNTIMES.contains(&name_without_extension(process_name).as_str())
}

/// Whether the name is one of the agents' own programs.
pub fn is_agent_program(process_name: &str) -> bool {
    let name = name_without_extension(process_name);
    ALL.iter().any(|a| a.executable_names.contains(&name.as_str()))
}

pub(crate) fn name_without_extension(name: &str) -> String {
    let name = name.trim().to_lowercase();
    match name.strip_suffix(".exe") {
        Some(stem) => stem.to_owned(),
        None => name,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tokens_follow_the_regex() {
        let found: Vec<&str> = tokens(r#""C:\a b\x.exe" --prompt "run it"  a"b c" "open"#).collect();
        assert_eq!(found, [r#""C:\a b\x.exe""#, "--prompt", r#""run it""#, r#"a"b"#, r#"c""#, r#""open"#]);
    }

    #[test]
    fn ids_and_kinds_are_looked_up() {
        assert_eq!(by_id("CODEX").map(|a| a.kind), Some(AgentKind::Codex));
        assert_eq!(ALL.iter().map(|a| a.id).collect::<Vec<_>>(), ["claude", "codex", "gemini", "aider", "opencode"]);
        assert!(is_runtime("Node.EXE") && !is_runtime("claude.exe"));
        assert!(is_agent_program(" Claude.exe ") && !is_agent_program("node"));
    }

    // From AgentDetectionTests.cs.

    #[test]
    fn command_lines_name_the_agent_in_a_terminal_button() {
        assert_eq!(for_command(r"C:\bin\claude.cmd").map(|a| a.id), Some("claude"));
        assert_eq!(for_command("aider").map(|a| a.id), Some("aider"));
        assert_eq!(for_command("powershell"), None);
    }

    // From OpenCodeTests.cs.

    const EXE: &str = r"C:\Users\me\AppData\Roaming\npm\node_modules\@opencode\cli\bin\opencode.exe";

    fn process(name: &str, command_line: &str) -> ProcessSnapshot {
        ProcessSnapshot {
            command_line: Some(command_line.into()),
            executable_path: name.ends_with(".exe").then(|| EXE.into()),
            ..ProcessSnapshot::new(10, 1, name)
        }
    }

    #[test]
    fn an_opencode_session_is_recognised() {
        for arguments in [
            "",
            r" C:\work\project",
            " --continue",
            " -s ses_123",
            " --prompt \"please run the tests\"",
            " --prompt run",
            " --standalone",
        ] {
            let command_line = format!("\"{EXE}\"{arguments}");
            assert_eq!(
                matches(&process("opencode.exe", &command_line)).map(|a| a.kind),
                Some(AgentKind::OpenCode),
                "{command_line}"
            );
        }
    }

    #[test]
    fn the_background_server_and_one_off_commands_are_not_sessions() {
        for arguments in [
            " serve --stdio --port 0",
            " service run",
            " run \"fix the bug\"",
            " --standalone run hello",
            " acp",
            " models",
            " plugin list",
            " api GET /session",
        ] {
            let command_line = format!("\"{EXE}\"{arguments}");
            assert_eq!(matches(&process("opencode.exe", &command_line)), None, "{command_line}");
        }
    }

    #[test]
    fn an_opencode_script_run_by_node_is_recognised_unless_it_is_a_subcommand() {
        let script = r"node C:\Users\me\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode";

        assert_eq!(matches(&process("node.exe", script)).map(|a| a.kind), Some(AgentKind::OpenCode));
        assert_eq!(matches(&process("node.exe", &format!("{script} serve"))), None);
    }

    #[test]
    fn other_agents_are_not_affected_by_the_words_opencode_treats_as_subcommands() {
        let claude = ProcessSnapshot {
            command_line: Some("claude \"please run the tests and serve the docs\"".into()),
            executable_path: Some(r"C:\tools\claude.exe".into()),
            ..ProcessSnapshot::new(11, 1, "claude.exe")
        };

        assert_eq!(matches(&claude).map(|a| a.kind), Some(AgentKind::Claude));
    }
}
