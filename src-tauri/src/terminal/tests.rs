//! Sessions end to end, with the pseudo terminal and the window replaced by test doubles, and
//! with the real pseudo terminal running small programs.

use std::io::{self, Read};
use std::sync::mpsc::{Receiver, Sender};
use std::sync::atomic::AtomicUsize;

use interprocess::local_socket::{prelude::*, GenericNamespaced, Stream};
use notch_core::presets::parse_preset;

use super::*;
use crate::pty::Spawned;

// Test doubles ----------------------------------------------------------------------------

#[derive(Default)]
struct Recorder(Mutex<Vec<(String, Value)>>);

impl Host for Recorder {
    fn emit(&self, event: &str, payload: Value) {
        lock(&self.0).push((event.to_owned(), payload));
    }
}

impl Recorder {
    fn events(&self, name: &str) -> Vec<Value> {
        lock(&self.0).iter().filter(|(event, _)| event == name).map(|(_, payload)| payload.clone()).collect()
    }

    /// Every event name in order, for checking what came before what.
    fn names(&self) -> Vec<String> {
        lock(&self.0).iter().map(|(event, _)| event.clone()).collect()
    }

    /// What the terminal has been shown so far.
    fn screen(&self) -> String {
        use base64::engine::general_purpose::STANDARD;
        use base64::Engine;
        let bytes: Vec<u8> = self
            .events("session-output")
            .iter()
            .flat_map(|e| STANDARD.decode(e["data"].as_str().unwrap_or_default()).unwrap_or_default())
            .collect();
        String::from_utf8_lossy(&bytes).into_owned()
    }

    fn states(&self) -> Vec<String> {
        self.events("session-state").iter().map(|e| e["state"].as_str().unwrap_or_default().to_owned()).collect()
    }
}

/// Reads whatever the test sends, then ends.
struct ChannelReader {
    receiver: Receiver<Vec<u8>>,
    pending: Vec<u8>,
}

impl Read for ChannelReader {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if self.pending.is_empty() {
            match self.receiver.recv() {
                Ok(data) => self.pending = data,
                Err(_) => return Ok(0),
            }
        }
        let count = self.pending.len().min(buffer.len());
        buffer[..count].copy_from_slice(&self.pending[..count]);
        self.pending.drain(..count);
        Ok(count)
    }
}

/// A process the test controls.
struct FakeProcess {
    written: Mutex<Vec<u8>>,
    sizes: Mutex<Vec<(u16, u16)>>,
    exit: Mutex<Receiver<i64>>,
    exit_sender: Mutex<Sender<i64>>,
    killed: AtomicBool,
}

impl PtyProcess for FakeProcess {
    fn write(&self, data: &[u8]) -> io::Result<()> {
        lock(&self.written).extend_from_slice(data);
        Ok(())
    }

    fn resize(&self, cols: u16, rows: u16) {
        lock(&self.sizes).push((cols, rows));
    }

    fn kill(&self) {
        self.killed.store(true, Ordering::SeqCst);
        let _ = lock(&self.exit_sender).send(137);
    }

    fn wait(&self) -> i64 {
        lock(&self.exit).recv().unwrap_or(-1)
    }
}

/// What the test holds on to after the session has started.
struct Control {
    output: Mutex<Option<Sender<Vec<u8>>>>,
    exit: Sender<i64>,
    process: Arc<FakeProcess>,
}

impl Control {
    fn print(&self, text: &str) {
        if let Some(output) = lock(&self.output).as_ref() {
            let _ = output.send(text.as_bytes().to_vec());
        }
    }

    /// The process ends: it exits and its output stream closes.
    fn exit(&self, code: i64) {
        let _ = self.exit.send(code);
        lock(&self.output).take();
    }
}

struct FakeSpawner {
    next: Mutex<Option<(Receiver<Vec<u8>>, Arc<FakeProcess>)>>,
    requests: Mutex<Vec<SpawnRequest>>,
    failure: Option<String>,
}

impl FakeSpawner {
    fn new() -> (Arc<Self>, Control) {
        let (output, receiver) = mpsc::channel();
        let (exit, exit_receiver) = mpsc::channel();
        let process = Arc::new(FakeProcess {
            written: Mutex::default(),
            sizes: Mutex::default(),
            exit: Mutex::new(exit_receiver),
            exit_sender: Mutex::new(exit.clone()),
            killed: AtomicBool::new(false),
        });
        let spawner = Arc::new(Self {
            next: Mutex::new(Some((receiver, process.clone()))),
            requests: Mutex::default(),
            failure: None,
        });
        (spawner, Control { output: Mutex::new(Some(output)), exit, process })
    }

    fn failing(message: &str) -> Arc<Self> {
        Arc::new(Self { next: Mutex::default(), requests: Mutex::default(), failure: Some(message.to_owned()) })
    }
}

impl Spawner for FakeSpawner {
    fn spawn(&self, request: &SpawnRequest) -> Result<Spawned, String> {
        lock(&self.requests).push(request.clone());
        if let Some(message) = &self.failure {
            return Err(message.clone());
        }
        let (receiver, process) = lock(&self.next).take().ok_or("one process per fake")?;
        Ok(Spawned { process, reader: Box::new(ChannelReader { receiver, pending: Vec::new() }) })
    }
}

// Helpers -----------------------------------------------------------------------------------

static UNIQUE: AtomicUsize = AtomicUsize::new(0);

fn unique(prefix: &str) -> String {
    format!("{prefix}-{}-{}", std::process::id(), UNIQUE.fetch_add(1, Ordering::SeqCst))
}

fn profile(line: &str) -> TerminalProfile {
    parse_preset(line, &[]).expect("a valid preset line")
}

fn found(profile: &TerminalProfile) -> Option<PathBuf> {
    Some(PathBuf::from(&profile.command))
}

struct Fixture {
    terminals: Arc<Terminals>,
    host: Arc<Recorder>,
    hub: Arc<ActivityHub>,
    pipe: String,
}

fn fixture(spawner: Arc<dyn Spawner>, resolver: Resolver) -> Fixture {
    let host = Arc::new(Recorder::default());
    let hub = Arc::new(ActivityHub::new());
    let pipe = unique("notch-test");
    let terminals = Terminals::new(Setup {
        host: host.clone(),
        hub: hub.clone(),
        spawner,
        resolver,
        pipe_name: pipe.clone(),
        hook_exe: std::env::current_exe().expect("test executable"),
        claude_settings: std::env::temp_dir().join(unique("notch-claude")).join("claude-hooks.json"),
    });
    Fixture { terminals, host, hub, pipe }
}

fn fake_fixture() -> (Fixture, Control, Arc<FakeSpawner>) {
    let (spawner, control) = FakeSpawner::new();
    (fixture(spawner.clone(), Arc::new(|p| found(p))), control, spawner)
}

fn wait_until(what: &str, condition: impl Fn() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(10);
    while !condition() {
        assert!(Instant::now() < deadline, "timed out waiting for {what}");
        thread::sleep(Duration::from_millis(5));
    }
}

/// Opens a session and has the UI report its size, then waits for the process to exist.
fn start_session(fixture: &Fixture, profile: TerminalProfile, spawner: &FakeSpawner) -> String {
    let id = fixture.terminals.open(profile, "folder".to_owned());
    fixture.terminals.start(&id, 100, 30);
    wait_until("the process to start", || !lock(&spawner.requests).is_empty());
    wait_until("the process to be registered", || {
        fixture.terminals.find(&id).is_some_and(|s| lock(&s.process).is_some())
    });
    id
}

fn pill(hub: &ActivityHub) -> Vec<(String, String)> {
    hub.snapshot().into_iter().map(|a| (a.title, a.detail.unwrap_or_default())).collect()
}

// Sessions ----------------------------------------------------------------------------------

#[test]
fn opening_tells_the_ui_and_waits_for_a_size() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = fixture.terminals.open(profile("Claude = claude"), r"C:\work\notch".to_owned());

    let opened = fixture.host.events("session-opened");
    assert_eq!(opened.len(), 1);
    assert_eq!(opened[0]["id"], id.as_str());
    assert_eq!(opened[0]["profileId"], "claude");
    assert_eq!(opened[0]["displayName"], "Claude");
    assert_eq!(opened[0]["folderName"], "notch");
    assert_eq!(opened[0]["agent"], "claude");
    assert!(lock(&spawner.requests).is_empty(), "the process starts only when the UI reports a size");
    assert_eq!(fixture.terminals.session_count(), 1);
}

#[test]
fn the_process_starts_at_the_size_the_ui_reports_with_the_session_environment() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Tool = FOO=bar tool --flag"), &spawner);

    let request = lock(&spawner.requests)[0].clone();
    assert_eq!((request.cols, request.rows), (100, 30));
    assert_eq!(request.folder, "folder");
    assert_eq!(request.arguments, ["--flag"]);
    assert!(request.environment.contains(&("FOO".to_owned(), "bar".to_owned())));
    assert!(request.environment.contains(&(hooks::SESSION_VARIABLE.to_owned(), id)));
    assert!(request.environment.contains(&(hooks::PIPE_VARIABLE.to_owned(), fixture.pipe.clone())));
}

#[test]
fn a_resize_during_start_up_reaches_the_process() {
    let (fixture, control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Tool = tool"), &spawner);
    fixture.terminals.resize(&id, 120, 40);
    assert_eq!(*lock(&control.process.sizes), [(120, 40)]);
}

#[test]
fn agents_get_the_hook_arguments_and_a_plain_shell_does_not() {
    let (fixture, _control, spawner) = fake_fixture();
    start_session(&fixture, profile("Claude = claude --model x"), &spawner);
    let arguments = lock(&spawner.requests)[0].arguments.clone();
    assert_eq!(&arguments[..2], ["--model", "x"]);
    assert_eq!(arguments[2], "--settings");
    let settings = std::fs::read_to_string(&arguments[3]).expect("the hook settings were written");
    assert!(settings.contains("--hook"), "{settings}");

    let (fixture, _control, spawner) = fake_fixture();
    start_session(&fixture, profile("Tool = tool"), &spawner);
    assert!(lock(&spawner.requests)[0].arguments.is_empty());
}

#[test]
fn codex_is_told_to_notify_the_hook() {
    let (fixture, _control, spawner) = fake_fixture();
    start_session(&fixture, profile("Codex = codex"), &spawner);
    let arguments = lock(&spawner.requests)[0].arguments.clone();
    assert_eq!(arguments[0], "-c");
    assert!(arguments[1].starts_with("notify=['") && arguments[1].ends_with("','--hook']"), "{arguments:?}");
}

#[test]
fn output_and_the_exit_notice_reach_the_ui_in_order() {
    let (fixture, control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Tool = tool"), &spawner);
    control.print("hello ");
    control.print("world\r\n");
    control.exit(3);

    wait_until("the exit", || !fixture.host.events("session-exited").is_empty());
    let screen = fixture.host.screen();
    assert!(screen.contains("hello world"), "{screen:?}");
    assert!(screen.ends_with("\r\n\x1b[90mProcess exited with code 3.\x1b[0m\r\n"), "{screen:?}");
    assert_eq!(fixture.host.events("session-exited")[0]["code"], 3);
    assert_eq!(fixture.host.events("session-exited")[0]["id"], id.as_str());

    // The notice is output like any other: nothing is shown after the exit event.
    let names = fixture.host.names();
    assert_eq!(names.last().map(String::as_str), Some("session-exited"));
}

#[test]
fn typing_reaches_the_process_in_order() {
    let (fixture, control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Tool = tool"), &spawner);
    for part in ["he", "llo", "\r"] {
        fixture.terminals.input(&id, part);
    }
    wait_until("the input", || lock(&control.process.written).len() == 6);
    assert_eq!(&*lock(&control.process.written), b"hello\r");
}

#[test]
fn a_program_that_is_not_installed_says_so() {
    let (spawner, _control) = FakeSpawner::new();
    let fixture = fixture(spawner, Arc::new(|_| None));
    let tool = profile("Tool = tool");
    let hint = tool.install_hint.clone();
    let id = fixture.terminals.open(tool, "folder".to_owned());
    fixture.terminals.start(&id, 80, 24);

    wait_until("the hint", || fixture.host.screen().contains(&hint));
    assert!(fixture.host.screen().contains("\x1b[90m"));
}

#[test]
fn a_process_that_cannot_start_says_why() {
    let fixture = fixture(FakeSpawner::failing("The directory name is invalid."), Arc::new(|p| found(p)));
    let id = fixture.terminals.open(profile("Tool = tool"), "nowhere".to_owned());
    fixture.terminals.start(&id, 80, 24);

    wait_until("the reason", || fixture.host.screen().contains("Could not start Tool: The directory name is invalid."));
}

#[test]
fn closing_kills_the_process_and_clears_the_pill() {
    let (fixture, control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);
    fixture.terminals.on_hook(&id, "UserPromptSubmit");
    assert_eq!(pill(&fixture.hub).len(), 1);

    fixture.terminals.close(&id);
    assert!(control.process.killed.load(Ordering::SeqCst));
    assert!(pill(&fixture.hub).is_empty());
    assert_eq!(fixture.terminals.session_count(), 0);
    // The exit that follows the kill is not news.
    thread::sleep(Duration::from_millis(100));
    assert!(fixture.host.events("session-exited").is_empty());
}

#[test]
fn a_session_closed_while_starting_never_runs() {
    let (fixture, control, spawner) = fake_fixture();
    let id = fixture.terminals.open(profile("Tool = tool"), "folder".to_owned());
    fixture.terminals.close(&id);
    fixture.terminals.start(&id, 80, 24);
    thread::sleep(Duration::from_millis(100));
    assert!(lock(&spawner.requests).is_empty());
    assert!(!control.process.killed.load(Ordering::SeqCst));
}

// Agent status ------------------------------------------------------------------------------

#[test]
fn hook_reports_show_in_the_pill_and_the_tab() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);

    fixture.terminals.on_hook(&id, "UserPromptSubmit");
    assert_eq!(pill(&fixture.hub), [("Claude · folder".to_owned(), "Working".to_owned())]);
    fixture.terminals.on_hook(&id, "Notification");
    assert_eq!(pill(&fixture.hub)[0].1, "Needs input");
    fixture.terminals.on_hook(&id, "SessionEnd");
    assert!(pill(&fixture.hub).is_empty());
    assert_eq!(fixture.host.states(), ["working", "needsInput", "idle"]);
}

#[test]
fn a_finished_turn_stays_done_until_the_user_looks() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);

    fixture.terminals.on_hook(&id, "Stop");
    assert_eq!(pill(&fixture.hub)[0].1, "Done");
    fixture.terminals.set_viewing(true);
    assert!(pill(&fixture.hub).is_empty());
    assert_eq!(fixture.host.states(), ["done", "idle"]);
}

#[test]
fn a_turn_that_finishes_in_view_needs_no_badge() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);
    fixture.terminals.set_viewing(true);

    fixture.terminals.on_hook(&id, "UserPromptSubmit");
    fixture.terminals.on_hook(&id, "Stop");
    assert!(pill(&fixture.hub).is_empty());
    assert_eq!(fixture.host.states(), ["working", "idle"], "the tab never shows done either");
}

#[test]
fn a_turn_finishing_in_a_session_that_is_not_in_front_stays_done() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);
    fixture.terminals.set_viewing(true);
    // Another session takes the front.
    let other = fixture.terminals.open(profile("Other = other"), "folder".to_owned());
    fixture.terminals.activate(&other);

    fixture.terminals.on_hook(&id, "Stop");
    assert_eq!(pill(&fixture.hub)[0].1, "Done");

    fixture.terminals.activate(&id);
    assert!(pill(&fixture.hub).is_empty(), "switching to it counts as looking");
}

#[test]
fn switching_while_the_terminal_is_hidden_does_not_clear_done() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);
    fixture.terminals.on_hook(&id, "Stop");
    fixture.terminals.activate(&id);
    assert_eq!(pill(&fixture.hub)[0].1, "Done");
}

#[test]
fn the_pill_counts_the_other_sessions_with_something_to_report() {
    let (fixture, _control, spawner) = fake_fixture();
    let first = start_session(&fixture, profile("Claude = claude"), &spawner);
    let second = fixture.terminals.open(profile("Claude = claude"), "elsewhere".to_owned());
    let third = fixture.terminals.open(profile("Claude = claude"), "third".to_owned());

    fixture.terminals.on_hook(&first, "UserPromptSubmit");
    assert_eq!(pill(&fixture.hub), [("Claude · folder".to_owned(), "Working".to_owned())]);

    fixture.terminals.on_hook(&second, "Notification");
    let by_title: HashMap<String, String> = pill(&fixture.hub).into_iter().collect();
    assert_eq!(by_title["Claude · folder"], "Working · +1");
    assert_eq!(by_title["Claude · elsewhere"], "Needs input · +1");

    fixture.terminals.on_hook(&third, "Stop");
    let by_title: HashMap<String, String> = pill(&fixture.hub).into_iter().collect();
    assert_eq!(by_title["Claude · folder"], "Working · +2");

    // The count drops when one of them is closed.
    fixture.terminals.close(&second);
    fixture.terminals.close(&third);
    assert_eq!(pill(&fixture.hub), [("Claude · folder".to_owned(), "Working".to_owned())]);
}

#[test]
fn codex_infers_work_from_the_terminal_and_gives_up_when_it_goes_quiet() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Codex = codex"), &spawner);

    fixture.terminals.input(&id, "fix it\r");
    assert_eq!(pill(&fixture.hub)[0].1, "Working");

    // Still printing: not stalled.
    fixture.terminals.check_for_stalled_agents(Instant::now() + Duration::from_secs(10));
    assert_eq!(pill(&fixture.hub)[0].1, "Working");

    fixture.terminals.check_for_stalled_agents(Instant::now() + Duration::from_secs(16));
    assert!(pill(&fixture.hub).is_empty());
}

#[test]
fn output_keeps_a_working_codex_from_going_quiet() {
    let (fixture, control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Codex = codex"), &spawner);
    fixture.terminals.input(&id, "go\r");
    thread::sleep(Duration::from_millis(30));
    control.print("spinner");
    wait_until("the output", || fixture.host.screen().contains("spinner"));

    let session = fixture.terminals.find(&id).expect("session");
    let printed = *lock(&session.last_activity);
    fixture.terminals.check_for_stalled_agents(printed + Duration::from_secs(14));
    assert_eq!(pill(&fixture.hub)[0].1, "Working");
    fixture.terminals.check_for_stalled_agents(printed + Duration::from_secs(16));
    assert!(pill(&fixture.hub).is_empty());
}

#[test]
fn a_bell_means_a_working_codex_needs_input_and_typing_answers_it() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Codex = codex"), &spawner);
    fixture.terminals.input(&id, "go\r");
    fixture.terminals.bell(&id);
    assert_eq!(pill(&fixture.hub)[0].1, "Needs input");
    fixture.terminals.input(&id, "y");
    assert_eq!(pill(&fixture.hub)[0].1, "Working");
}

#[test]
fn a_plain_shell_reports_nothing() {
    let (fixture, _control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Tool = tool"), &spawner);
    fixture.terminals.input(&id, "ls\r");
    fixture.terminals.bell(&id);
    assert!(pill(&fixture.hub).is_empty());
    assert!(fixture.host.states().is_empty());
}

#[test]
fn an_exiting_agent_stops_reporting() {
    let (fixture, control, spawner) = fake_fixture();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);
    fixture.terminals.on_hook(&id, "UserPromptSubmit");
    control.exit(0);
    wait_until("the exit", || !fixture.host.events("session-exited").is_empty());
    assert!(pill(&fixture.hub).is_empty());
    assert_eq!(fixture.host.states().last().map(String::as_str), Some("idle"));
}

// The hook socket -----------------------------------------------------------------------------

fn send_to_socket(pipe: &str, line: &str) {
    let name = pipe.to_ns_name::<GenericNamespaced>().expect("socket name");
    let mut stream = Stream::connect(name).expect("the hook socket accepts connections");
    std::io::Write::write_all(&mut stream, format!("{line}\n").as_bytes()).expect("write");
}

#[test]
fn reports_arriving_on_the_socket_move_the_right_session() {
    let (fixture, _control, spawner) = fake_fixture();
    fixture.terminals.listen_for_hooks();
    let id = start_session(&fixture, profile("Claude = claude"), &spawner);

    send_to_socket(&fixture.pipe, &hooks::format_message(&id, r#"{"hook_event_name":"UserPromptSubmit"}"#));
    wait_until("the pill", || pill(&fixture.hub).first().is_some_and(|p| p.1 == "Working"));

    // Garbage and reports for sessions that do not exist change nothing.
    send_to_socket(&fixture.pipe, "not json");
    send_to_socket(&fixture.pipe, &hooks::format_message("nobody", r#"{"hook_event_name":"Stop"}"#));
    send_to_socket(&fixture.pipe, &hooks::format_message(&id, r#"{"type":"agent-turn-complete"}"#));
    wait_until("done", || pill(&fixture.hub).first().is_some_and(|p| p.1 == "Done"));
    assert_eq!(fixture.host.states(), ["working", "done"]);
}

// The real pseudo terminal ----------------------------------------------------------------------

/// A preset line that prints `text`, then exits with `code`.
fn print_and_exit(text: &str, code: i32) -> String {
    if cfg!(windows) {
        format!("Echo = cmd /c \"echo {text}& exit {code}\"")
    } else {
        format!("Echo = sh -c \"echo {text}; exit {code}\"")
    }
}

fn real_fixture() -> Fixture {
    fixture(system_spawner(), Arc::new(|p| launch::resolve(p)))
}

fn run_for_real(line: &str) -> Fixture {
    let fixture = real_fixture();
    let id = fixture.terminals.open(profile(line), std::env::temp_dir().to_string_lossy().into_owned());
    fixture.terminals.start(&id, 100, 30);
    wait_until("the process to exit", || !fixture.host.events("session-exited").is_empty());
    fixture
}

#[test]
fn a_real_process_prints_and_its_exit_code_is_reported() {
    let fixture = run_for_real(&print_and_exit("hello-notch", 7));
    let screen = fixture.host.screen();
    assert!(screen.contains("hello-notch"), "{screen:?}");
    assert!(screen.contains("Process exited with code 7."), "{screen:?}");
    assert_eq!(fixture.host.events("session-exited")[0]["code"], 7);
}

#[test]
fn a_real_process_gets_the_hook_environment_and_the_folder() {
    let line = if cfg!(windows) {
        "Env = cmd /c \"echo [%NOTCH_SESSION%][%NOTCH_PIPE%][%CD%]\""
    } else {
        "Env = sh -c \"echo [$NOTCH_SESSION][$NOTCH_PIPE][$PWD]\""
    };
    let fixture = real_fixture();
    let folder = std::env::temp_dir().canonicalize().expect("temp dir");
    let folder_text = folder.to_string_lossy().trim_start_matches(r"\\?\").trim_end_matches(['\\', '/']).to_owned();
    let id = fixture.terminals.open(profile(line), folder_text.clone());
    fixture.terminals.start(&id, 100, 30);
    wait_until("the process to exit", || !fixture.host.events("session-exited").is_empty());

    let screen = fixture.host.screen();
    assert!(screen.contains(&format!("[{id}][{}]", fixture.pipe)), "{screen:?}");
    assert!(screen.to_lowercase().contains(&folder_text.to_lowercase()), "{screen:?}");
}

#[test]
fn a_real_process_can_be_typed_to() {
    let line = if cfg!(windows) { "Cat = cmd /q" } else { "Cat = sh" };
    let fixture = real_fixture();
    let id = fixture.terminals.open(profile(line), std::env::temp_dir().to_string_lossy().into_owned());
    fixture.terminals.start(&id, 100, 30);
    fixture.terminals.input(&id, "echo typed-by-notch\r");
    wait_until("the echo", || fixture.host.screen().matches("typed-by-notch").count() >= 2);
    fixture.terminals.input(&id, "exit 5\r");
    wait_until("the exit", || !fixture.host.events("session-exited").is_empty());
    assert_eq!(fixture.host.events("session-exited")[0]["code"], 5);
}

#[test]
fn a_real_session_closed_midway_ends_its_process() {
    let line = if cfg!(windows) { "Sleep = cmd /c \"ping -n 60 127.0.0.1\"" } else { "Sleep = sleep 60" };
    let fixture = real_fixture();
    let id = fixture.terminals.open(profile(line), std::env::temp_dir().to_string_lossy().into_owned());
    fixture.terminals.start(&id, 100, 30);
    wait_until("the process", || fixture.terminals.find(&id).is_some_and(|s| lock(&s.process).is_some()));

    let session = fixture.terminals.find(&id).expect("session");
    let handle = lock(&session.process).as_ref().map(|p| p.handle.clone()).expect("process");
    let started = Instant::now();
    fixture.terminals.close(&id);
    handle.wait();
    assert!(started.elapsed() < Duration::from_secs(10), "closing must not wait for the process");
}

/// Windows only: npm's launchers are batch files, and a folder with spaces is the case that
/// fails when arguments are quoted twice.
#[cfg(windows)]
#[test]
fn a_real_batch_file_in_a_folder_with_spaces_gets_its_arguments_intact() {
    let folder = std::env::temp_dir().join(unique("notch dir with spaces"));
    std::fs::create_dir_all(&folder).expect("temp folder");
    let script = folder.join("show args.cmd");
    std::fs::write(&script, "@echo off\r\necho first[%~1] second[%~2]\r\n").expect("script");

    let line = format!("Batch = \"{}\" \"a b\"", script.display());
    let fixture = run_for_real(&line);
    let screen = fixture.host.screen();
    assert!(screen.contains("first[a b]"), "{screen:?}");
    assert!(screen.contains("Process exited with code 0."), "{screen:?}");
    let _ = std::fs::remove_dir_all(&folder);
}
