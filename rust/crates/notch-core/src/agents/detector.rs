//! Finds coding agents among the running programs. Ported from
//! `src/Notch.Core/Agents/AgentDetector.cs`. Pure logic: the scan itself is in the platform layer.

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Utc};
use indexmap::IndexMap;

use super::catalog::{self, AgentDefinition, name_without_extension};
use super::{AgentKind, ignore_case};

/// One running program, as the process scan sees it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessSnapshot {
    pub pid: i32,
    pub parent_pid: i32,
    pub name: String,
    /// Only read for programs that might be an agent; `None` otherwise.
    pub command_line: Option<String>,
    pub executable_path: Option<String>,
    pub started_at: Option<DateTime<Utc>>,
    pub working_directory: Option<String>,
}

impl ProcessSnapshot {
    /// A program with only its ids and name; set the rest on the result.
    pub fn new(pid: i32, parent_pid: i32, name: impl Into<String>) -> Self {
        Self {
            pid,
            parent_pid,
            name: name.into(),
            command_line: None,
            executable_path: None,
            started_at: None,
            working_directory: None,
        }
    }
}

/// An agent found among the running programs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DetectedAgent {
    /// Stable while the agent runs; a reused process id gets a different one.
    pub key: String,
    pub pid: i32,
    pub definition: &'static AgentDefinition,
    pub started_at: Option<DateTime<Utc>>,
    /// The terminal or editor the agent runs in, when it could be found.
    pub host_pid: Option<i32>,
    pub host_name: Option<&'static str>,
}

const MAX_DEPTH: usize = 40;

/// Programs whose window an agent's terminal lives in, with the name to show for them.
fn host_name(process_name: &str) -> Option<&'static str> {
    Some(match name_without_extension(process_name).as_str() {
        "windowsterminal" | "wt" => "Windows Terminal",
        "code" | "code - insiders" => "VS Code",
        "cursor" => "Cursor",
        "windsurf" => "Windsurf",
        "wezterm-gui" => "WezTerm",
        "alacritty" => "Alacritty",
        "kitty" => "kitty",
        "hyper" => "Hyper",
        "tabby" => "Tabby",
        "conhost" => "Console",
        "idea64" => "IntelliJ IDEA",
        "pycharm64" => "PyCharm",
        "rider64" => "Rider",
        "webstorm64" => "WebStorm",
        "devenv" => "Visual Studio",
        _ => return None,
    })
}

/// The processes by id; for a repeated id the last one wins, as in the C# dictionary.
fn by_pid(processes: &[ProcessSnapshot]) -> HashMap<i32, &ProcessSnapshot> {
    processes.iter().map(|p| (p.pid, p)).collect()
}

/// The agents running right now. A program that Notch started itself (anything below `own_pid`)
/// is left out, since Notch's own terminal already reports it; an agent that runs helper
/// processes of its own (a node child of claude.exe) counts once.
///
/// `hidden`: ids of agents the user does not want listed.
pub fn detect<S: AsRef<str>>(processes: &[ProcessSnapshot], own_pid: i32, hidden: &[S]) -> Vec<DetectedAgent> {
    let by_pid = by_pid(processes);

    let mut matches: IndexMap<i32, &'static AgentDefinition> = IndexMap::new();
    for process in processes {
        if let Some(agent) = catalog::matches(process).filter(|a| !hidden.iter().any(|h| h.as_ref() == a.id)) {
            matches.insert(process.pid, agent);
        }
    }

    let mut found = Vec::new();
    for (&pid, &agent) in &matches {
        let process = by_pid[&pid];
        let ancestors = ancestors(process, &by_pid);

        if ancestors.iter().any(|a| a.pid == own_pid) {
            continue;
        }

        // The outermost process of an agent is the agent; its helpers below it are not more agents.
        if ancestors.iter().any(|a| matches.get(&a.pid).is_some_and(|above| above.id == agent.id)) {
            continue;
        }

        let host = ancestors.iter().find_map(|a| host_name(&a.name).map(|name| (a.pid, name)));
        found.push(DetectedAgent {
            key: key_for(process),
            pid,
            definition: agent,
            started_at: process.started_at,
            host_pid: host.map(|(pid, _)| pid),
            host_name: host.map(|(_, name)| name),
        });
    }

    // Stable, as LINQ's OrderBy is; a missing start time sorts last.
    found.sort_by_key(|a| (a.started_at.is_none(), a.started_at, a.pid));
    found
}

/// Stable for one run of a program: the process id plus its start time, since ids are reused.
/// The time is written as .NET ticks (100 ns since 0001-01-01), as C# writes it.
pub fn key_for(process: &ProcessSnapshot) -> String {
    const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;
    let ticks = process
        .started_at
        .map_or(0, |at| UNIX_EPOCH_TICKS + at.timestamp() * 10_000_000 + i64::from(at.timestamp_subsec_nanos() / 100));
    format!("pid-{}-{ticks}", process.pid)
}

/// Whether two folder names are the same folder, whatever the slashes, trailing slash or capitals.
pub fn same_folder(a: Option<&str>, b: Option<&str>) -> bool {
    let (Some(a), Some(b)) = (a, b) else {
        return false;
    };
    if a.trim().is_empty() || b.trim().is_empty() {
        return false;
    }

    let normal = |path: &str| path.trim().replace('/', "\\").trim_end_matches('\\').to_owned();
    ignore_case::eq(&normal(a), &normal(b))
}

/// The opencode windows an event about `folder` is for. opencode's plugin lives in a background
/// service, so its events cannot name the window they came from; the folder is what they have. A
/// window whose folder could not be read is only chosen when it is the only one.
pub fn open_code_in(
    folder: Option<&str>,
    processes: &[ProcessSnapshot],
    detected: &[DetectedAgent],
) -> Vec<DetectedAgent> {
    let open_code: Vec<&DetectedAgent> = detected.iter().filter(|d| d.definition.kind == AgentKind::OpenCode).collect();
    let folder_of = |agent: &DetectedAgent| -> Option<&str> {
        processes.iter().find(|p| p.pid == agent.pid).and_then(|p| p.working_directory.as_deref())
    };

    let there: Vec<DetectedAgent> =
        open_code.iter().filter(|a| same_folder(folder_of(a), folder)).map(|&a| a.clone()).collect();
    if !there.is_empty() {
        return there;
    }

    match open_code.as_slice() {
        [only] if folder_of(only).is_none() => vec![(*only).clone()],
        _ => Vec::new(),
    }
}

/// The detected agent `pid` runs under (or is), as when a hook program reports itself.
pub fn agent_of(pid: i32, processes: &[ProcessSnapshot], detected: &[DetectedAgent]) -> Option<DetectedAgent> {
    let by_pid = by_pid(processes);
    let start = *by_pid.get(&pid)?;

    std::iter::once(start)
        .chain(ancestors(start, &by_pid))
        .find_map(|step| detected.iter().find(|d| d.pid == step.pid))
        .cloned()
}

/// Processes that may own a window the agent can be reached through: the agent's parents, and
/// the programs those started (the console host a command prompt window is made of).
pub fn window_candidates(agent_pid: i32, processes: &[ProcessSnapshot]) -> Vec<i32> {
    let by_pid = by_pid(processes);
    let Some(agent) = by_pid.get(&agent_pid) else {
        return Vec::new();
    };

    let ancestors = ancestors(agent, &by_pid);
    let mut candidates = Vec::new();

    // Known hosts first, so Windows Terminal wins over the shell inside it.
    candidates.extend(ancestors.iter().filter(|a| host_name(&a.name).is_some()).map(|a| a.pid));
    for ancestor in &ancestors {
        candidates.push(ancestor.pid);
        candidates.extend(
            processes
                .iter()
                .filter(|p| {
                    p.parent_pid == ancestor.pid
                        && matches!(name_without_extension(&p.name).as_str(), "conhost" | "openconsole")
                })
                .map(|p| p.pid),
        );
    }

    candidates.push(agent_pid);
    let mut seen = HashSet::new();
    candidates.retain(|pid| seen.insert(*pid));
    candidates
}

fn ancestors<'a>(process: &'a ProcessSnapshot, by_pid: &HashMap<i32, &'a ProcessSnapshot>) -> Vec<&'a ProcessSnapshot> {
    let mut result = Vec::new();
    let mut seen = HashSet::from([process.pid]);
    let mut process = process;
    let mut parent = process.parent_pid;
    while result.len() < MAX_DEPTH && parent > 0 && seen.insert(parent) {
        let Some(&next) = by_pid.get(&parent) else {
            break;
        };

        // A parent that started after its child is a recycled id, not the real parent.
        if matches!((process.started_at, next.started_at), (Some(started), Some(parent_started)) if parent_started > started)
        {
            break;
        }

        result.push(next);
        process = next;
        parent = next.parent_pid;
    }

    result
}

#[cfg(test)]
mod tests {
    use chrono::TimeZone;

    use super::*;

    fn t0() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 1, 1, 12, 0, 0).unwrap()
    }

    fn p(
        pid: i32,
        parent: i32,
        name: &str,
        command_line: Option<&str>,
        path: Option<&str>,
        minute: i64,
    ) -> ProcessSnapshot {
        ProcessSnapshot {
            command_line: command_line.map(Into::into),
            executable_path: path.map(Into::into),
            started_at: Some(t0() + chrono::Duration::minutes(minute)),
            ..ProcessSnapshot::new(pid, parent, name)
        }
    }

    fn n(pid: i32, parent: i32, name: &str) -> ProcessSnapshot {
        p(pid, parent, name, None, None, 0)
    }

    fn c(pid: i32, parent: i32, name: &str, command_line: &str) -> ProcessSnapshot {
        p(pid, parent, name, Some(command_line), None, 0)
    }

    const NONE: [&str; 0] = [];

    #[test]
    fn a_native_agent_and_a_node_wrapped_one_are_both_found() {
        let processes = [
            n(1, 0, "explorer.exe"),
            n(10, 1, "WindowsTerminal.exe"),
            n(11, 10, "OpenConsole.exe"),
            n(12, 11, "pwsh.exe"),
            c(13, 12, "claude.exe", "claude"),
            n(20, 1, "Code.exe"),
            n(21, 20, "pwsh.exe"),
            c(22, 21, "node.exe", r"node C:\Users\me\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js"),
        ];

        let found = detect(&processes, 999, &NONE);

        let mut ids: Vec<&str> = found.iter().map(|a| a.definition.id).collect();
        ids.sort();
        assert_eq!(ids, ["claude", "codex"]);
        let claude = found.iter().find(|a| a.definition.id == "claude").unwrap();
        assert_eq!(claude.pid, 13);
        assert_eq!(claude.host_name, Some("Windows Terminal"));
        assert_eq!(claude.host_pid, Some(10));
        assert_eq!(found.iter().find(|a| a.definition.id == "codex").unwrap().host_name, Some("VS Code"));
    }

    #[test]
    fn an_agent_with_helper_processes_counts_once() {
        let processes = [
            n(1, 0, "cmd.exe"),
            p(2, 1, "node.exe", Some(r"node C:\x\node_modules\@anthropic-ai\claude-code\cli.js"), None, 1),
            p(3, 2, "node.exe", Some(r"node C:\x\node_modules\@anthropic-ai\claude-code\cli.js --worker"), None, 2),
            p(4, 2, "claude.exe", Some("claude"), None, 2),
        ];

        let found = detect(&processes, 999, &NONE);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].pid, 2);
    }

    #[test]
    fn agents_started_by_notchs_own_terminal_are_left_to_it() {
        let processes = [
            n(100, 1, "Notch.exe"),
            n(101, 100, "pwsh.exe"),
            c(102, 101, "claude.exe", "claude"),
            n(200, 1, "cmd.exe"),
            c(201, 200, "codex.exe", "codex"),
        ];

        let found = detect(&processes, 100, &NONE);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].definition.id, "codex");
    }

    #[test]
    fn lookalikes_are_not_agents() {
        let cases: [(&str, &str, Option<&str>); 14] = [
            (
                "claude.exe",
                r#""C:\Users\me\AppData\Local\AnthropicClaude\app-1.0\claude.exe""#,
                Some(r"C:\Users\me\AppData\Local\AnthropicClaude\app-1.0\claude.exe"),
            ),
            ("claude.exe", "claude.exe --type=renderer --lang=en", Some(r"C:\Program Files\Claude\claude.exe")),
            ("claude.exe", "claude.exe", Some(r"C:\Program Files\WindowsApps\Claude_1\claude.exe")),
            ("node.exe", "node server.js", None),
            ("python.exe", "python manage.py runserver", None),
            ("code.exe", "code .", None),
            ("notepad.exe", "notepad claude.txt", None),
            ("claude.exe", "claude.exe --output-format stream-json --verbose --input-format stream-json", None),
            ("claude.exe", "claude -p fix the build", None),
            ("claude.exe", "claude --print hello", None),
            ("claude.exe", "claude.exe", Some(r"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1.0\x\claude.exe")),
            ("codex.exe", "codex.exe -c features.x=true app-server --analytics", None),
            ("codex.exe", "codex.exe exec-server --remote https://example", None),
            ("codex.exe", "codex.exe", Some(r"C:\Users\me\AppData\Local\OpenAI\Codex\bin\abc\codex.exe")),
        ];
        for (name, command_line, path) in cases {
            assert!(detect(&[p(5, 1, name, Some(command_line), path, 0)], 999, &NONE).is_empty(), "{command_line}");
        }
    }

    #[test]
    fn the_other_agents_are_recognised() {
        for (name, command_line, id) in [
            ("python.exe", "python -m aider --model x", "aider"),
            ("node.exe", r"node C:\n\@google\gemini-cli\dist\index.js", "gemini"),
            ("gemini.exe", "gemini", "gemini"),
            ("aider.exe", "aider", "aider"),
        ] {
            let found = detect(&[c(5, 1, name, command_line)], 999, &NONE);
            assert_eq!(found.len(), 1, "{command_line}");
            assert_eq!(found[0].definition.id, id);
        }
    }

    #[test]
    fn hidden_agents_are_not_listed() {
        let processes = [c(5, 1, "claude.exe", "claude"), c(6, 1, "codex.exe", "codex")];
        let found = detect(&processes, 999, &["claude"]);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].definition.id, "codex");
    }

    #[test]
    fn a_recycled_parent_id_is_not_followed() {
        // Pid 10 now belongs to a newer Notch; the agent's real parent has long gone.
        let processes = [p(10, 1, "Notch.exe", None, None, 50), p(11, 10, "claude.exe", Some("claude"), None, 5)];
        assert_eq!(detect(&processes, 10, &NONE).len(), 1);
    }

    #[test]
    fn parent_loops_do_not_hang() {
        let processes = [n(1, 2, "a.exe"), c(2, 1, "claude.exe", "claude")];
        assert_eq!(detect(&processes, 999, &NONE).len(), 1);
    }

    #[test]
    fn a_hook_program_is_traced_to_the_agent_that_started_it() {
        let processes = [
            n(1, 0, "cmd.exe"),
            c(2, 1, "claude.exe", "claude"),
            n(3, 2, "bash.exe"),
            n(4, 3, "Notch.Hook.exe"),
            n(9, 1, "Notch.Hook.exe"),
        ];
        let detected = detect(&processes, 999, &NONE);

        assert_eq!(agent_of(4, &processes, &detected).map(|a| a.pid), Some(2));
        assert_eq!(agent_of(2, &processes, &detected).map(|a| a.pid), Some(2));
        assert_eq!(agent_of(9, &processes, &detected), None);
        assert_eq!(agent_of(777, &processes, &detected), None);
    }

    #[test]
    fn window_candidates_put_the_known_host_first() {
        let processes = [
            n(10, 1, "WindowsTerminal.exe"),
            n(11, 10, "OpenConsole.exe"),
            n(12, 11, "pwsh.exe"),
            c(13, 12, "claude.exe", "claude"),
        ];
        let candidates = window_candidates(13, &processes);
        assert_eq!(candidates[0], 10);
        assert!(candidates.contains(&12));
        assert!(candidates.contains(&13));
    }

    #[test]
    fn keys_carry_the_start_time_in_dotnet_ticks() {
        assert_eq!(key_for(&n(7, 1, "x")), "pid-7-639028656000000000");
        assert_eq!(key_for(&ProcessSnapshot::new(7, 1, "x")), "pid-7-0");
    }

    // From OpenCodeTests.cs.

    fn open_code() -> &'static AgentDefinition {
        catalog::for_kind(AgentKind::OpenCode).unwrap()
    }

    fn window(pid: i32) -> DetectedAgent {
        DetectedAgent {
            key: format!("opencode:{pid}"),
            pid,
            definition: open_code(),
            started_at: None,
            host_pid: None,
            host_name: None,
        }
    }

    fn process(pid: i32, folder: Option<&str>) -> ProcessSnapshot {
        ProcessSnapshot {
            command_line: Some("opencode".into()),
            executable_path: Some(r"C:\tools\opencode.exe".into()),
            working_directory: folder.map(Into::into),
            ..ProcessSnapshot::new(pid, 1, "opencode.exe")
        }
    }

    fn pids(agents: &[DetectedAgent]) -> Vec<i32> {
        agents.iter().map(|a| a.pid).collect()
    }

    #[test]
    fn folders_are_compared_by_where_they_are_not_how_they_are_spelled() {
        for (a, b, same) in [
            (Some(r"C:\work\app"), Some(r"C:\work\app"), true),
            (Some(r"C:\work\app"), Some(r"c:\WORK\app\"), true),
            (Some(r"C:\work\app"), Some("C:/work/app"), true),
            (Some(r"C:\work\app"), Some(r"C:\work\other"), false),
            (Some(r"C:\work\app"), Some(r"C:\work\app\sub"), false),
            (None, Some(r"C:\work\app"), false),
            (Some(r"C:\work\app"), Some(""), false),
        ] {
            assert_eq!(same_folder(a, b), same, "{a:?} {b:?}");
        }
    }

    #[test]
    fn an_event_goes_to_the_window_in_its_folder() {
        let processes = [process(1, Some(r"C:\work\a")), process(2, Some(r"C:\work\b"))];
        let detected = [window(1), window(2)];

        assert_eq!(pids(&open_code_in(Some(r"C:\work\b"), &processes, &detected)), [2]);
    }

    #[test]
    fn two_windows_in_the_same_folder_both_get_the_event() {
        let processes =
            [process(1, Some(r"C:\work\a")), process(2, Some(r"C:\work\a")), process(3, Some(r"C:\work\b"))];
        let detected = [window(1), window(2), window(3)];

        assert_eq!(pids(&open_code_in(Some(r"C:\work\a"), &processes, &detected)), [1, 2]);
    }

    #[test]
    fn an_event_for_a_folder_nobody_is_in_goes_nowhere() {
        let processes = [process(1, Some(r"C:\work\a")), process(2, Some(r"C:\work\b"))];

        assert!(open_code_in(Some(r"C:\elsewhere"), &processes, &[window(1), window(2)]).is_empty());
    }

    #[test]
    fn a_window_whose_folder_is_unknown_is_chosen_only_when_it_is_the_only_one() {
        assert_eq!(open_code_in(Some(r"C:\work\a"), &[process(1, None)], &[window(1)]).len(), 1);

        // With two windows and no way to tell them apart, guessing would show the wrong one as done.
        assert!(
            open_code_in(Some(r"C:\work\a"), &[process(1, None), process(2, None)], &[window(1), window(2)]).is_empty()
        );
        assert!(
            open_code_in(
                Some(r"C:\work\a"),
                &[process(1, None), process(2, Some(r"C:\work\b"))],
                &[window(1), window(2)]
            )
            .is_empty()
        );
    }

    #[test]
    fn other_agents_are_never_chosen() {
        let claude = catalog::for_kind(AgentKind::Claude).unwrap();
        let claude_window = DetectedAgent {
            key: "claude:5".into(),
            pid: 5,
            definition: claude,
            started_at: None,
            host_pid: None,
            host_name: None,
        };

        assert!(open_code_in(Some(r"C:\work\a"), &[process(5, Some(r"C:\work\a"))], &[claude_window]).is_empty());
    }

    #[test]
    fn an_event_without_a_folder_is_chosen_only_for_a_single_window() {
        assert_eq!(open_code_in(None, &[process(1, None)], &[window(1)]).len(), 1);
        assert!(
            open_code_in(None, &[process(1, Some(r"C:\a")), process(2, Some(r"C:\b"))], &[window(1), window(2)])
                .is_empty()
        );
    }
}
