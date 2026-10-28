//! Reads the sessions the Claude and ChatGPT desktop apps keep on disk. Ported from
//! `src/Notch.Core/Agents/AppSessions.cs`. Pure logic: the files are read in the platform layer,
//! and only their text comes here.
//!
//! Where C# throws on a line or property of an unexpected shape (a transcript line that is an
//! array, a `message` that is a string, a millisecond time past year 9999), the value here is
//! simply missing.

use std::collections::HashMap;
use std::time::Duration;

use chrono::{DateTime, NaiveDate, NaiveDateTime, TimeDelta, Utc};
use serde_json::{Map, Value};

use super::catalog::{self, AgentDefinition, name_without_extension};
use super::detector::ProcessSnapshot;
use super::{AgentKind, AgentState, ignore_case};
use crate::terminal::windows_path;

/// The desktop apps whose own sessions Notch can read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DesktopApp {
    /// The Claude app, whose Code tab runs Claude Code sessions.
    Claude,
    /// The ChatGPT app (OpenAI's Codex package), whose coding threads Codex runs.
    ChatGpt,
}

/// Where a session's last turn stands, as its transcript tells it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TurnState {
    /// Nothing in the transcript says.
    Unknown,
    Working,
    /// The agent asked the user something and waits for the answer.
    NeedsInput,
    /// The turn finished on its own.
    Ended,
    /// The user stopped the turn.
    Stopped,
}

/// What a transcript's last lines say: the turn's state and when the transcript last said anything.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TurnReading {
    pub state: TurnState,
    pub at: Option<DateTime<Utc>>,
}

/// What the Claude app keeps about one Code tab session, in its own file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClaudeAppSession {
    pub id: String,
    /// The Claude Code session id, which names the transcript.
    pub transcript_id: String,
    pub title: Option<String>,
    pub folder: Option<String>,
    pub archived: bool,
    pub last_activity: Option<DateTime<Utc>>,
    pub last_focused: Option<DateTime<Utc>>,
    /// The app holds the turn until the user answers it.
    pub awaiting_person: bool,
}

/// The first line of a Codex thread's transcript, which says who started it and where.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CodexThreadMeta {
    pub id: String,
    pub folder: Option<String>,
    /// Started by the desktop app, not by the Codex command line.
    pub from_app: bool,
    /// A helper thread (a guardian review) under another thread, not one the user sees.
    pub subagent: bool,
}

/// A running desktop app as the process scan sees it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RunningApp {
    pub app: DesktopApp,
    /// The app's main process, which owns its windows.
    pub pid: i32,
    /// How many Claude Code programs the app runs, one for each session it has open; 0 for ChatGPT.
    pub workers: usize,
}

/// Tools that stop a Claude turn until the user answers them.
const ASKING_TOOLS: [&str; 2] = ["AskUserQuestion", "ExitPlanMode"];

/// Program names, lowercase without .exe, whose path must be read to tell which app they are.
const APP_PROGRAMS: [&str; 3] = ["claude", "chatgpt", "codex"];

pub fn display_name(app: DesktopApp) -> &'static str {
    match app {
        DesktopApp::Claude => "Claude",
        DesktopApp::ChatGpt => "ChatGPT",
    }
}

/// The agent the app's sessions are, for its id and glyph.
pub fn agent(app: DesktopApp) -> &'static AgentDefinition {
    let kind = if app == DesktopApp::Claude { AgentKind::Claude } else { AgentKind::Codex };
    // Both kinds are in the catalog.
    catalog::for_kind(kind).unwrap_or(&catalog::ALL[0])
}

/// Whether the process scan should read this program's path, since it may be one of the apps.
pub fn is_app_program(process_name: &str) -> bool {
    APP_PROGRAMS.contains(&name_without_extension(process_name).as_str())
}

/// The desktop apps running right now. Helper processes of Chromium and Electron (--type=) are not the app.
pub fn running(processes: &[ProcessSnapshot]) -> Vec<RunningApp> {
    let mut result = Vec::new();
    let claude_workers = processes.iter().filter(|p| is_claude_worker(p)).count();

    if let Some(claude) = main_process(processes, is_claude_app) {
        result.push(RunningApp { app: DesktopApp::Claude, pid: claude.pid, workers: claude_workers });
    }

    if let Some(chat_gpt) = main_process(processes, is_chat_gpt_app) {
        result.push(RunningApp { app: DesktopApp::ChatGpt, pid: chat_gpt.pid, workers: 0 });
    }

    result
}

/// The app process whose parent is not the same app: the one the others were started from.
fn main_process(processes: &[ProcessSnapshot], is_app: fn(&ProcessSnapshot) -> bool) -> Option<&ProcessSnapshot> {
    let app: Vec<&ProcessSnapshot> = processes.iter().filter(|p| is_app(p)).collect();
    let pids: Vec<i32> = app.iter().map(|p| p.pid).collect();
    app.iter()
        .find(|p| !pids.contains(&p.parent_pid) && !is_helper(p))
        .or_else(|| app.iter().find(|p| !is_helper(p)))
        .copied()
}

fn is_helper(process: &ProcessSnapshot) -> bool {
    ignore_case::contains(process.command_line.as_deref().unwrap_or(""), " --type=")
}

pub(crate) fn is_claude_app(process: &ProcessSnapshot) -> bool {
    name_without_extension(&process.name) == "claude"
        && process.executable_path.as_deref().is_some_and(|path| {
            ignore_case::contains(path, "\\windowsapps\\claude_") || ignore_case::contains(path, "\\anthropicclaude\\")
        })
}

/// A Claude Code program the Claude app runs for one of its sessions.
pub(crate) fn is_claude_worker(process: &ProcessSnapshot) -> bool {
    name_without_extension(&process.name) == "claude"
        && process.executable_path.as_deref().is_some_and(|path| ignore_case::contains(path, "\\claude\\claude-code\\"))
        && ignore_case::contains(process.command_line.as_deref().unwrap_or(""), "--input-format stream-json")
}

pub(crate) fn is_chat_gpt_app(process: &ProcessSnapshot) -> bool {
    matches!(name_without_extension(&process.name).as_str(), "chatgpt" | "codex")
        && process.executable_path.as_deref().is_some_and(|path| {
            ignore_case::contains(path, "\\windowsapps\\openai.codex_")
                || ignore_case::contains(path, "\\windowsapps\\openai.chatgpt")
        })
}

/// Reads one session file of the Claude app (`claude-code-sessions\…\local_*.json`).
pub fn read_claude_session(json: &str) -> Option<ClaudeAppSession> {
    let node = parse(json)?;
    let id = text(node.get("sessionId"))?;
    let transcript = text(node.get("cliSessionId"))?;

    Some(ClaudeAppSession {
        id,
        transcript_id: transcript,
        title: text(node.get("title")),
        folder: text(node.get("cwd")),
        archived: node.get("isArchived").and_then(Value::as_bool).unwrap_or(false),
        last_activity: millis(node.get("lastActivityAt")),
        last_focused: millis(node.get("lastFocusedAt")),
        awaiting_person: node.get("interruptedAwaitingPerson").is_some_and(Value::is_object),
    })
}

/// What the last lines of a Claude Code transcript say about its turn. Lines the turn does not
/// turn on (attachments, summaries, hook reports, a subagent's own lines) are passed over.
pub fn read_claude_turn<S: AsRef<str>>(lines: &[S]) -> TurnReading {
    let mut at = None;
    for line in lines.iter().rev() {
        let Some(node) = parse(line.as_ref()) else {
            continue;
        };

        at = at.or_else(|| time(node.get("timestamp")));
        let kind = text(node.get("type"));
        let is_sidechain = node.get("isSidechain").and_then(Value::as_bool).unwrap_or(false);
        if !matches!(kind.as_deref(), Some("user" | "assistant")) || is_sidechain {
            continue;
        }

        let message = node.get("message");
        let content = message.and_then(|m| m.get("content"));
        if kind.as_deref() == Some("assistant") {
            let tool = content.and_then(Value::as_array).and_then(|blocks| {
                blocks.iter().filter(|b| b.is_object()).rfind(|b| text(b.get("type")).as_deref() == Some("tool_use"))
            });
            if let Some(tool) = tool {
                let asking = text(tool.get("name")).is_some_and(|name| ASKING_TOOLS.contains(&name.as_str()));
                return TurnReading { state: if asking { TurnState::NeedsInput } else { TurnState::Working }, at };
            }

            let ended = matches!(
                text(message.and_then(|m| m.get("stop_reason"))).as_deref(),
                Some("end_turn" | "stop_sequence")
            );
            return TurnReading { state: if ended { TurnState::Ended } else { TurnState::Working }, at };
        }

        // A user line: a prompt or a tool's result, both of which the agent is now working on.
        if node.get("isMeta").and_then(Value::as_bool).unwrap_or(false) {
            continue;
        }

        let said = match content {
            Some(Value::Array(parts)) => {
                parts.iter().filter(|p| p.is_object()).map(|p| text(p.get("text")).unwrap_or_default()).collect()
            }
            other => text(other).unwrap_or_default(),
        };
        let stopped = said.starts_with("[Request interrupted by user");
        return TurnReading { state: if stopped { TurnState::Stopped } else { TurnState::Working }, at };
    }

    TurnReading { state: TurnState::Unknown, at }
}

/// Reads the first line of a Codex transcript (`sessions\YYYY\MM\DD\rollout-*.jsonl`).
pub fn read_codex_meta(first_line: &str) -> Option<CodexThreadMeta> {
    let node = parse(first_line)?;
    if text(node.get("type")).as_deref() != Some("session_meta") {
        return None;
    }

    let payload = node.get("payload").and_then(Value::as_object)?;
    let id = text(payload.get("id"))?;

    let subagent = text(payload.get("parent_thread_id")).is_some()
        || payload.get("source").is_some_and(Value::is_object)
        || text(payload.get("thread_source")).is_some_and(|source| source != "user");
    let from_app = ignore_case::contains(&text(payload.get("originator")).unwrap_or_default(), "desktop");
    Some(CodexThreadMeta { id, folder: text(payload.get("cwd")), from_app, subagent })
}

/// What the last lines of a Codex transcript say about its turn: its last task event.
pub fn read_codex_turn<S: AsRef<str>>(lines: &[S]) -> TurnReading {
    let mut at = None;
    for line in lines.iter().rev() {
        let Some(node) = parse(line.as_ref()) else {
            continue;
        };

        at = at.or_else(|| time(node.get("timestamp")));
        if text(node.get("type")).as_deref() != Some("event_msg") {
            continue;
        }

        let state = match text(node.get("payload").and_then(|p| p.get("type"))).as_deref() {
            Some("task_started") => TurnState::Working,
            Some("task_complete") => TurnState::Ended,
            Some("turn_aborted") => TurnState::Stopped,
            _ => continue,
        };
        return TurnReading { state, at };
    }

    TurnReading { state: TurnState::Unknown, at }
}

/// The names the ChatGPT app gave its threads (`session_index.jsonl`), the latest name winning.
pub fn read_codex_titles<S: AsRef<str>>(lines: &[S]) -> HashMap<String, String> {
    let mut titles = HashMap::new();
    for line in lines {
        let Some(node) = parse(line.as_ref()) else {
            continue;
        };

        if let (Some(id), Some(name)) = (text(node.get("id")), text(node.get("thread_name"))) {
            titles.insert(id, name);
        }
    }

    titles
}

/// What the board shows for a session. A finished turn is "Done" until the user has looked at
/// the session since it finished; a turn said to be running that has been quiet for longer
/// than `stale` is taken to have died with its program.
///
/// `viewed`: when the user last looked at the session, if known.
pub fn state_for(
    turn: TurnReading,
    viewed: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
    stale: Duration,
    awaiting_person: bool,
) -> AgentState {
    if awaiting_person {
        return AgentState::NeedsInput;
    }

    let stale = TimeDelta::from_std(stale).unwrap_or(TimeDelta::MAX);
    let quiet = turn.at.is_none_or(|at| now - at > stale);
    match turn.state {
        TurnState::Working if !quiet => AgentState::Working,
        TurnState::NeedsInput if !quiet => AgentState::NeedsInput,
        TurnState::Ended if turn.at.is_some_and(|ended| viewed.is_none_or(|seen| seen < ended)) => AgentState::Done,
        _ => AgentState::Idle,
    }
}

/// The folder's own name, for telling sessions apart when they have no title.
pub fn folder_name(folder: Option<&str>) -> Option<String> {
    let folder = folder.filter(|f| !f.trim().is_empty())?;
    let name = windows_path::file_name(folder.trim().trim_end_matches(['\\', '/']));
    Some(if name.is_empty() { folder } else { name }.to_owned())
}

/// A JSON object, or `None` for anything else: the first line of a tail read from the middle of
/// the file, or one still being written.
fn parse(line: &str) -> Option<Map<String, Value>> {
    if line.trim().is_empty() {
        return None;
    }

    match serde_json::from_str(line) {
        Ok(Value::Object(map)) => Some(map),
        _ => None,
    }
}

/// A string value that is not blank.
fn text(node: Option<&Value>) -> Option<String> {
    node.and_then(Value::as_str).filter(|s| !s.trim().is_empty()).map(str::to_owned)
}

fn millis(node: Option<&Value>) -> Option<DateTime<Utc>> {
    let ms = node.and_then(Value::as_i64).filter(|&ms| ms > 0)?;
    DateTime::from_timestamp_millis(ms).filter(|at| at.timestamp_millis() <= MAX_DATE_MILLIS)
}

/// `DateTimeOffset.MaxValue`, 9999-12-31T23:59:59.999Z.
const MAX_DATE_MILLIS: i64 = 253_402_300_799_999;

/// `DateTimeOffset.TryParse` with the invariant culture, reading a time without an offset as UTC.
/// The transcripts write ISO 8601; that, with or without an offset, a `T` or the time, is what is read.
fn time(node: Option<&Value>) -> Option<DateTime<Utc>> {
    let text = text(node)?;
    let text = text.trim();
    if let Ok(at) = DateTime::parse_from_rfc3339(text) {
        return Some(at.with_timezone(&Utc));
    }

    ["%Y-%m-%dT%H:%M:%S%.f", "%Y-%m-%d %H:%M:%S%.f", "%Y-%m-%dT%H:%M", "%Y-%m-%d %H:%M"]
        .iter()
        .find_map(|format| NaiveDateTime::parse_from_str(text, format).ok())
        .or_else(|| NaiveDate::parse_from_str(text, "%Y-%m-%d").ok().and_then(|d| d.and_hms_opt(0, 0, 0)))
        .map(|at| at.and_utc())
}

#[cfg(test)]
mod tests {
    use chrono::TimeZone;

    use super::*;
    use crate::agents::detector;

    fn t0() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 10, 16, 9, 0, 0).unwrap()
    }

    fn line(json: &str) -> String {
        json.replace('\'', "\"")
    }

    fn process(pid: i32, parent: i32, name: &str, command_line: &str, path: &str) -> ProcessSnapshot {
        ProcessSnapshot {
            command_line: Some(command_line.into()),
            executable_path: Some(path.into()),
            ..ProcessSnapshot::new(pid, parent, name)
        }
    }

    #[test]
    fn the_claude_app_and_its_sessions_are_found_among_the_programs() {
        let app = r"C:\Program Files\WindowsApps\Claude_2.42304.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe";
        let worker = r"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1.299\f0\claude.exe";
        let processes = [
            ProcessSnapshot::new(1, 0, "explorer.exe"),
            process(10, 1, "claude.exe", &format!("\"{app}\""), app),
            process(11, 10, "claude.exe", &format!("\"{app}\" --type=renderer"), app),
            process(
                12,
                10,
                "claude.exe",
                &format!("{worker} --output-format stream-json --verbose --input-format stream-json"),
                worker,
            ),
            process(
                13,
                10,
                "claude.exe",
                &format!("{worker} --output-format stream-json --verbose --input-format stream-json --resume=abc"),
                worker,
            ),
        ];

        let found = running(&processes);
        assert_eq!(found, [RunningApp { app: DesktopApp::Claude, pid: 10, workers: 2 }]);

        // The app's own programs are not command line agents.
        assert!(detector::detect(&processes, 999, &[] as &[&str]).is_empty());
    }

    #[test]
    fn the_chatgpt_app_is_found_by_its_package_folder() {
        let app = r"C:\Program Files\WindowsApps\OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        let processes = [
            process(20, 1, "ChatGPT.exe", &format!("\"{app}\""), app),
            process(21, 20, "ChatGPT.exe", &format!("\"{app}\" --type=gpu-process"), app),
            process(30, 1, "codex.exe", "codex", r"C:\Users\me\AppData\Roaming\npm\codex.exe"),
        ];

        let found = running(&processes);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].app, DesktopApp::ChatGpt);
        assert_eq!(found[0].pid, 20);
    }

    #[test]
    fn a_claude_session_file_is_read() {
        let session = read_claude_session(&line(concat!(
            r"{'sessionId':'local_1','cliSessionId':'abc','cwd':'C:\\code\\notch','title':'Bug fix','isArchived':false,",
            "'lastActivityAt':1792142078801,'lastFocusedAt':1792142144516,'interruptedAwaitingPerson':null}"
        )))
        .unwrap();

        assert_eq!(session.transcript_id, "abc");
        assert_eq!(session.title.as_deref(), Some("Bug fix"));
        assert_eq!(session.folder.as_deref(), Some(r"C:\code\notch"));
        assert!(!session.archived);
        assert!(!session.awaiting_person);
        assert!(session.last_focused > session.last_activity);
    }

    #[test]
    fn a_claude_session_file_of_the_wrong_shape_is_not_read() {
        assert_eq!(read_claude_session("[1]"), None);
        assert_eq!(read_claude_session(&line("{'sessionId':'a','cliSessionId':' '}")), None);
        let far = read_claude_session(&line("{'sessionId':'a','cliSessionId':'b','lastActivityAt':999999999999999}"))
            .unwrap();
        assert_eq!(far.last_activity, None);
    }

    #[test]
    fn a_claude_turn_that_ended_is_told_from_one_still_running() {
        let ended = [
            line("{'type':'user','message':{'role':'user','content':'fix it'},'timestamp':'2026-10-16T09:00:00Z'}"),
            line(
                "{'type':'assistant','message':{'stop_reason':'end_turn','content':[{'type':'text','text':'Fixed.'}]},'timestamp':'2026-10-16T09:01:00Z'}",
            ),
            line("{'type':'system','subtype':'stop_hook_summary','timestamp':'2026-10-16T09:01:02Z'}"),
        ];
        let reading = read_claude_turn(&ended);
        assert_eq!(reading.state, TurnState::Ended);
        assert_eq!(reading.at, Some(t0() + TimeDelta::minutes(1) + TimeDelta::seconds(2)));

        let tool = [
            line(
                "{'type':'assistant','message':{'stop_reason':'tool_use','content':[{'type':'tool_use','name':'Bash'}]},'timestamp':'2026-10-16T09:00:00Z'}",
            ),
            line("{'type':'attachment','timestamp':'2026-10-16T09:00:01Z'}"),
        ];
        assert_eq!(read_claude_turn(&tool).state, TurnState::Working);

        let mut result = tool.to_vec();
        result.push(line("{'type':'user','message':{'content':[{'type':'tool_result','content':'ok'}]},'timestamp':'2026-10-16T09:00:02Z'}"));
        assert_eq!(read_claude_turn(&result).state, TurnState::Working);
    }

    #[test]
    fn a_claude_question_needs_input_and_an_interruption_stops_the_turn() {
        let asking = [line(
            "{'type':'assistant','message':{'content':[{'type':'tool_use','name':'AskUserQuestion'}]},'timestamp':'2026-10-16T09:00:00Z'}",
        )];
        assert_eq!(read_claude_turn(&asking).state, TurnState::NeedsInput);

        let stopped = [
            line(
                "{'type':'assistant','message':{'content':[{'type':'tool_use','name':'Bash'}]},'timestamp':'2026-10-16T09:00:00Z'}",
            ),
            line(
                "{'type':'user','message':{'content':[{'type':'text','text':'[Request interrupted by user for tool use]'}]},'timestamp':'2026-10-16T09:00:05Z'}",
            ),
        ];
        assert_eq!(read_claude_turn(&stopped).state, TurnState::Stopped);
    }

    #[test]
    fn a_subagents_lines_and_a_half_read_line_are_passed_over() {
        let lines = [
            "ext\":\"cut off in the middle\"}]}}".to_owned(),
            line(
                "{'type':'assistant','message':{'stop_reason':'end_turn','content':[{'type':'text','text':'Done.'}]},'timestamp':'2026-10-16T09:00:00Z'}",
            ),
            line(
                "{'type':'user','isSidechain':true,'message':{'content':'subagent prompt'},'timestamp':'2026-10-16T09:00:01Z'}",
            ),
            "[1]".to_owned(),
        ];

        assert_eq!(read_claude_turn(&lines).state, TurnState::Ended);
    }

    #[test]
    fn codex_threads_of_the_app_are_told_from_the_command_line_and_from_helpers() {
        let app = read_codex_meta(&line(
            r"{'type':'session_meta','payload':{'id':'t1','cwd':'C:\\code','originator':'Codex Desktop','source':'vscode','parent_thread_id':null,'thread_source':'user'}}",
        ))
        .unwrap();
        assert!(app.from_app);
        assert!(!app.subagent);
        assert_eq!(app.folder.as_deref(), Some(r"C:\code"));

        let cli = read_codex_meta(&line(
            "{'type':'session_meta','payload':{'id':'t2','originator':'codex-tui','source':'cli','thread_source':'user'}}",
        ));
        assert!(!cli.unwrap().from_app);

        let guardian = read_codex_meta(&line(
            "{'type':'session_meta','payload':{'id':'t3','originator':'Codex Desktop','source':{'subagent':{'other':'guardian'}},'parent_thread_id':'t1','thread_source':'guardian_review'}}",
        ));
        assert!(guardian.unwrap().subagent);
    }

    #[test]
    fn a_codex_turn_is_read_from_its_last_task_event() {
        let started = line("{'type':'event_msg','payload':{'type':'task_started'},'timestamp':'2026-10-16T09:00:00Z'}");
        let item = line("{'type':'response_item','payload':{'type':'message'},'timestamp':'2026-10-16T09:00:30Z'}");
        let complete =
            line("{'type':'event_msg','payload':{'type':'task_complete'},'timestamp':'2026-10-16T09:01:00Z'}");
        let aborted = line("{'type':'event_msg','payload':{'type':'turn_aborted'},'timestamp':'2026-10-16T09:01:00Z'}");

        let working = read_codex_turn(&[&started, &item]);
        assert_eq!(working.state, TurnState::Working);
        assert_eq!(working.at, Some(t0() + TimeDelta::seconds(30)));
        assert_eq!(read_codex_turn(&[&started, &item, &complete]).state, TurnState::Ended);
        assert_eq!(read_codex_turn(&[&started, &aborted]).state, TurnState::Stopped);
    }

    #[test]
    fn a_thread_keeps_the_latest_name_it_was_given() {
        let titles = read_codex_titles(&[
            line("{'id':'t1','thread_name':'First name'}"),
            line("{'id':'t1','thread_name':'Renamed'}"),
            String::new(),
        ]);

        assert_eq!(titles["t1"], "Renamed");
    }

    #[test]
    fn done_lasts_until_the_session_is_looked_at_and_a_quiet_turn_is_not_working() {
        let stale = Duration::from_secs(10 * 60);
        let minutes = TimeDelta::minutes;
        let ended = TurnReading { state: TurnState::Ended, at: Some(t0()) };

        assert_eq!(state_for(ended, Some(t0() - minutes(1)), t0() + minutes(5), stale, false), AgentState::Done);
        assert_eq!(state_for(ended, Some(t0() + minutes(1)), t0() + minutes(5), stale, false), AgentState::Idle);

        let working = TurnReading { state: TurnState::Working, at: Some(t0()) };
        assert_eq!(state_for(working, None, t0() + minutes(1), stale, false), AgentState::Working);
        assert_eq!(state_for(working, None, t0() + minutes(30), stale, false), AgentState::Idle);

        assert_eq!(state_for(ended, Some(t0() + minutes(1)), t0() + minutes(5), stale, true), AgentState::NeedsInput);
    }

    #[test]
    fn times_are_read_as_utc_unless_they_say_otherwise() {
        let at = |s: &str| time(Some(&Value::String(s.into())));
        assert_eq!(at("2026-10-16T09:00:00Z"), Some(t0()));
        assert_eq!(at("2026-10-16T11:00:00+02:00"), Some(t0()));
        assert_eq!(at("2026-10-16 09:00:00"), Some(t0()));
        assert_eq!(at("2026-10-16"), Some(t0() - TimeDelta::hours(9)));
        assert_eq!(at("nope"), None);
    }

    #[test]
    fn folder_names_are_the_last_part() {
        assert_eq!(folder_name(Some(r" C:\code\notch\ ")).as_deref(), Some("notch"));
        assert_eq!(folder_name(Some("C:/code/web/")).as_deref(), Some("web"));
        assert_eq!(folder_name(Some(r"\")).as_deref(), Some(r"\"));
        assert_eq!(folder_name(Some("  ")), None);
        assert_eq!(display_name(DesktopApp::ChatGpt), "ChatGPT");
        assert_eq!(agent(DesktopApp::ChatGpt).id, "codex");
        assert!(is_app_program("ChatGPT.exe") && !is_app_program("node.exe"));
    }
}
