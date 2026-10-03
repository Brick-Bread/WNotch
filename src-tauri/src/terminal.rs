//! The built-in terminal: sessions in pseudo consoles, the agent hooks that report on them, and
//! the pill activities that make those reports visible.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use interprocess::local_socket::{prelude::*, GenericNamespaced, ListenerOptions};
use notch_core::agents::{activity_id, agent_activity, hooks, AgentKind, AgentState, AgentStatusTracker};
use notch_core::presets::TerminalProfile;
use serde_json::{json, Value};
use tauri::{AppHandle, Emitter};

use crate::activity_hub::ActivityHub;
use crate::launch;
use crate::output::{encode, OutputQueue};
use crate::pty::{system_spawner, PtyProcess, SpawnRequest, Spawner};

/// A working agent that has printed nothing for this long was a false start.
const QUIET_THRESHOLD: Duration = Duration::from_secs(15);

/// How often sessions are checked for going quiet.
const QUIET_CHECK: Duration = Duration::from_secs(5);

/// If the UI has not said how big a new terminal is by now, it starts at [`FALLBACK_SIZE`].
const START_DEADLINE: Duration = Duration::from_secs(3);

/// The size a terminal starts at when the UI never reports one.
const FALLBACK_SIZE: (u16, u16) = (80, 24);

/// How long the last of a finished process's output is waited for.
const DRAIN_TIMEOUT: Duration = Duration::from_secs(2);

/// The most a hook may send in one report; real ones are a few hundred bytes.
const MAX_HOOK_MESSAGE: u64 = 64 * 1024;

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|e| e.into_inner())
}

/// Where the UI hears about sessions.
pub trait Host: Send + Sync {
    fn emit(&self, event: &str, payload: Value);
}

/// Delivers events to the window.
pub struct TauriHost(pub AppHandle);

impl Host for TauriHost {
    fn emit(&self, event: &str, payload: Value) {
        if let Err(e) = self.0.emit(event, payload) {
            log::warn!("could not emit {event}: {e}");
        }
    }
}

/// How a profile's program is found on this machine.
pub type Resolver = Arc<dyn Fn(&TerminalProfile) -> Option<PathBuf> + Send + Sync>;

/// Everything [`Terminals`] needs from its surroundings.
pub struct Setup {
    pub host: Arc<dyn Host>,
    pub hub: Arc<ActivityHub>,
    pub spawner: Arc<dyn Spawner>,
    pub resolver: Resolver,
    /// The name of the socket hooks report to.
    pub pipe_name: String,
    /// The program agent CLIs run as `<hook_exe> --hook`: the app itself.
    pub hook_exe: PathBuf,
    /// Where the settings JSON handed to `claude --settings` is written.
    pub claude_settings: PathBuf,
}

/// The running process of a session, and how to type into it.
struct Process {
    handle: Arc<dyn PtyProcess>,
    input: Sender<Vec<u8>>,
}

struct Session {
    id: String,
    profile: TerminalProfile,
    folder: String,
    folder_name: String,
    tracker: Mutex<AgentStatusTracker>,
    /// Gone once the process exits or the session closes.
    process: Mutex<Option<Process>>,
    queue: Arc<OutputQueue>,
    flusher: Mutex<Option<JoinHandle<()>>>,
    /// Last time the process printed or the user submitted a line; used to spot a stalled "working" state.
    last_activity: Mutex<Instant>,
    /// The latest size the UI reported, which a process still starting up has not been told yet.
    size: Mutex<(u16, u16)>,
    started: AtomicBool,
    closed: AtomicBool,
}

/// Every open session, and the rules for what their state means to the user.
pub struct Terminals {
    host: Arc<dyn Host>,
    hub: Arc<ActivityHub>,
    spawner: Arc<dyn Spawner>,
    resolver: Resolver,
    sessions: Mutex<HashMap<String, Arc<Session>>>,
    /// Order the tabs were opened in, which is the order activities are counted in.
    order: Mutex<Vec<String>>,
    active: Mutex<Option<String>>,
    viewing: Mutex<bool>,
    pipe_name: String,
    hook_exe: PathBuf,
    claude_settings: PathBuf,
}

impl Terminals {
    /// The real thing: listening for hook reports and watching for stalled agents.
    pub fn start(app: AppHandle, hub: Arc<ActivityHub>) -> Arc<Self> {
        let config = notch_core::settings::SettingsStore::default_path();
        let claude_settings =
            config.parent().map_or_else(|| PathBuf::from("."), Path::to_path_buf).join("claude-hooks.json");

        let terminals = Self::new(Setup {
            host: Arc::new(TauriHost(app)),
            hub,
            spawner: system_spawner(),
            resolver: Arc::new(launch::resolve),
            // Unique per process so two copies of the app (e.g. a dev build) never cross wires.
            pipe_name: format!("notch-agent-{}", std::process::id()),
            hook_exe: std::env::current_exe().unwrap_or_default(),
            claude_settings,
        });
        terminals.listen_for_hooks();
        terminals.watch_for_quiet();
        terminals
    }

    pub fn new(setup: Setup) -> Arc<Self> {
        Arc::new(Self {
            host: setup.host,
            hub: setup.hub,
            spawner: setup.spawner,
            resolver: setup.resolver,
            sessions: Mutex::default(),
            order: Mutex::default(),
            active: Mutex::default(),
            viewing: Mutex::default(),
            pipe_name: setup.pipe_name,
            hook_exe: setup.hook_exe,
            claude_settings: setup.claude_settings,
        })
    }

    /// How many sessions are open; restarting the app would close them.
    pub fn session_count(&self) -> usize {
        lock(&self.sessions).len()
    }

    /// Opens a session running `profile` in `folder`. The process starts once the UI has made the
    /// terminal and says how big it is ([`start`](Self::start)), so it never starts at a wrong size.
    /// A program that is not installed still gets a tab, which says so.
    pub fn open(self: &Arc<Self>, profile: TerminalProfile, folder: String) -> String {
        let id = uuid::Uuid::new_v4().simple().to_string();
        let folder_name = Path::new(folder.trim_end_matches(['\\', '/']))
            .file_name()
            .map_or_else(|| folder.clone(), |n| n.to_string_lossy().into_owned());
        let session = Arc::new(Session {
            id: id.clone(),
            tracker: Mutex::new(AgentStatusTracker::new(profile.agent)),
            profile,
            folder,
            folder_name,
            process: Mutex::new(None),
            queue: Arc::new(OutputQueue::default()),
            flusher: Mutex::new(None),
            last_activity: Mutex::new(Instant::now()),
            size: Mutex::new(FALLBACK_SIZE),
            started: AtomicBool::new(false),
            closed: AtomicBool::new(false),
        });

        lock(&self.sessions).insert(id.clone(), session.clone());
        lock(&self.order).push(id.clone());
        *lock(&self.active) = Some(id.clone());

        self.host.emit(
            "session-opened",
            json!({
                "id": session.id,
                "profileId": session.profile.id,
                "displayName": session.profile.display_name,
                "glyph": session.profile.glyph,
                "folder": session.folder,
                "folderName": session.folder_name,
                "agent": session.profile.agent,
            }),
        );
        *lock(&session.flusher) = Some(self.spawn_flusher(&session));

        // A UI that never answers must not leave a dead tab.
        let weak = Arc::downgrade(self);
        let waiting = session.id.clone();
        thread::spawn(move || {
            thread::sleep(START_DEADLINE);
            if let Some(terminals) = weak.upgrade() {
                terminals.start(&waiting, FALLBACK_SIZE.0, FALLBACK_SIZE.1);
            }
        });
        id
    }

    /// The terminal exists in the UI and measured itself: starts the process at that size.
    pub fn start(self: &Arc<Self>, id: &str, cols: u16, rows: u16) {
        let Some(session) = self.find(id) else { return };
        if session.started.swap(true, Ordering::SeqCst) {
            return;
        }
        *lock(&session.size) = (cols.max(1), rows.max(1));

        let terminals = self.clone();
        thread::spawn(move || {
            if let Err(problem) = terminals.start_process(&session) {
                terminals.write_notice(&session, &problem);
            }
        });
    }

    /// Ends a session and takes it out of the pill.
    pub fn close(&self, id: &str) {
        let Some(session) = lock(&self.sessions).remove(id) else { return };
        session.closed.store(true, Ordering::SeqCst);
        lock(&self.order).retain(|s| s != id);
        {
            let mut active = lock(&self.active);
            if active.as_deref() == Some(id) {
                *active = None;
            }
        }
        if let Some(process) = lock(&session.process).take() {
            process.handle.kill();
        }
        session.queue.close();
        self.hub.remove(&activity_id(id));
        self.publish_agents();
    }

    /// Kills every process; the app is quitting.
    pub fn shutdown(&self) {
        let ids: Vec<String> = lock(&self.sessions).keys().cloned().collect();
        for id in ids {
            self.close(&id);
        }
    }

    /// Keystrokes and pastes, already encoded as terminal input.
    pub fn input(&self, id: &str, data: &str) {
        let Some(session) = self.find(id) else { return };
        if let Some(process) = lock(&session.process).as_ref() {
            // Queued, so typing never waits on a process that is not reading.
            let _ = process.input.send(data.as_bytes().to_vec());
        }

        let submitted = data.contains('\r');
        if submitted {
            *lock(&session.last_activity) = Instant::now();
        }
        let changed = lock(&session.tracker).on_user_input(submitted);
        self.after_change(&session, changed);
    }

    pub fn resize(&self, id: &str, cols: u16, rows: u16) {
        let Some(session) = self.find(id) else { return };
        let size = (cols.max(1), rows.max(1));
        *lock(&session.size) = size;
        if let Some(process) = lock(&session.process).as_ref() {
            process.handle.resize(size.0, size.1);
        }
    }

    pub fn bell(&self, id: &str) {
        if let Some(session) = self.find(id) {
            let changed = lock(&session.tracker).on_bell();
            self.after_change(&session, changed);
        }
    }

    /// The user switched to this session. Looking at it, if the terminal is on screen, means it
    /// has nothing new to report.
    pub fn activate(&self, id: &str) {
        let Some(session) = self.find(id) else { return };
        *lock(&self.active) = Some(id.to_owned());
        if *lock(&self.viewing) {
            let changed = lock(&session.tracker).on_viewed();
            self.after_change(&session, changed);
        }
    }

    /// The user is looking at this session, so it has nothing new to report.
    pub fn viewed(&self, id: &str) {
        let Some(session) = self.find(id) else { return };
        *lock(&self.active) = Some(id.to_owned());
        let changed = lock(&session.tracker).on_viewed();
        self.after_change(&session, changed);
    }

    /// The terminal tab came on or went off screen. A turn that finishes in view needs no "done" badge.
    pub fn set_viewing(&self, viewing: bool) {
        *lock(&self.viewing) = viewing;
        if viewing {
            let active = lock(&self.active).clone();
            if let Some(session) = active.and_then(|id| self.find(&id)) {
                let changed = lock(&session.tracker).on_viewed();
                self.after_change(&session, changed);
            }
        }
    }

    fn find(&self, id: &str) -> Option<Arc<Session>> {
        lock(&self.sessions).get(id).cloned()
    }

    /// Dim text on its own line, as the process's last words.
    fn write_notice(&self, session: &Session, text: &str) {
        session.queue.push(format!("\r\n\x1b[90m{text}\x1b[0m\r\n").as_bytes());
    }

    /// The arguments a profile starts with: its own, plus the ones that make an agent CLI report
    /// to the hook. Without a usable hook program the agent still runs, just unobserved.
    fn arguments(&self, profile: &TerminalProfile) -> Vec<String> {
        let mut args = launch::base_arguments(profile);
        if profile.agent == AgentKind::None || !self.hook_exe.is_file() {
            return args;
        }

        let hook_exe = self.hook_exe.to_string_lossy();
        if profile.agent == AgentKind::Claude {
            let written = self
                .claude_settings
                .parent()
                .map_or(Ok(()), std::fs::create_dir_all)
                .and_then(|()| std::fs::write(&self.claude_settings, hooks::build_claude_settings(&hook_exe)));
            if let Err(e) = written {
                log::warn!("agent status will not show: could not write the Claude hook settings: {e}");
                return args;
            }
        }
        args.extend(hooks::build_arguments(profile.agent, &hook_exe, &self.claude_settings.to_string_lossy()));
        args
    }

    fn start_process(self: &Arc<Self>, session: &Arc<Session>) -> Result<(), String> {
        let profile = &session.profile;
        let executable = (self.resolver)(profile).ok_or_else(|| profile.install_hint.clone())?;

        let mut environment: Vec<(String, String)> =
            profile.environment.iter().map(|(name, value)| (name.clone(), value.clone())).collect();
        environment.push((hooks::PIPE_VARIABLE.to_owned(), self.pipe_name.clone()));
        environment.push((hooks::SESSION_VARIABLE.to_owned(), session.id.clone()));

        let (cols, rows) = *lock(&session.size);
        let request = SpawnRequest {
            executable,
            arguments: self.arguments(profile),
            folder: session.folder.clone(),
            environment,
            cols,
            rows,
        };
        let spawned = self
            .spawner
            .spawn(&request)
            .map_err(|problem| format!("Could not start {}: {problem}", profile.display_name))?;

        if session.closed.load(Ordering::SeqCst) {
            spawned.process.kill();
            return Ok(());
        }

        let (input, typed) = mpsc::channel::<Vec<u8>>();
        let writer = spawned.process.clone();
        thread::spawn(move || {
            for data in typed {
                if writer.write(&data).is_err() {
                    break;
                }
            }
        });

        *lock(&session.process) = Some(Process { handle: spawned.process.clone(), input });

        // The UI may have resized while the process was starting.
        let latest = *lock(&session.size);
        if latest != (cols, rows) {
            spawned.process.resize(latest.0, latest.1);
        }

        let reader = self.spawn_reader(session, spawned.reader);
        let terminals = self.clone();
        let waiting = session.clone();
        let process = spawned.process;
        thread::spawn(move || {
            let code = process.wait();
            wait_for(&reader, DRAIN_TIMEOUT);
            terminals.on_exit(&waiting, &process, code);
        });
        Ok(())
    }

    fn spawn_reader(&self, session: &Arc<Session>, mut reader: Box<dyn Read + Send>) -> JoinHandle<()> {
        let session = session.clone();
        thread::spawn(move || {
            let mut buffer = [0u8; 8192];
            while let Ok(read) = reader.read(&mut buffer) {
                if read == 0 {
                    break;
                }
                *lock(&session.last_activity) = Instant::now();
                session.queue.push(&buffer[..read]);
            }
        })
    }

    /// Hands output to the UI in few, large events.
    fn spawn_flusher(&self, session: &Arc<Session>) -> JoinHandle<()> {
        let queue = session.queue.clone();
        let host = self.host.clone();
        let id = session.id.clone();
        thread::spawn(move || {
            while let Some(batch) = queue.next_batch() {
                host.emit("session-output", json!({ "id": id, "data": encode(&batch) }));
            }
        })
    }

    fn on_exit(&self, session: &Arc<Session>, process: &Arc<dyn PtyProcess>, code: i64) {
        {
            let mut slot = lock(&session.process);
            match slot.as_ref() {
                Some(current) if Arc::ptr_eq(&current.handle, process) => {
                    slot.take();
                }
                // Closed meanwhile.
                _ => return,
            }
        }

        // What the process printed last reaches the screen before the exit notice.
        self.write_notice(session, &format!("Process exited with code {code}."));
        session.queue.close();
        let flusher = lock(&session.flusher).take();
        if let Some(flusher) = flusher {
            let _ = flusher.join();
        }

        let changed = lock(&session.tracker).reset();
        self.host.emit("session-exited", json!({ "id": session.id, "code": code }));
        self.after_change(session, changed);
    }

    /// Tells the UI and the pill about a state change. Finishing in front of the user's eyes needs no "done" badge.
    fn after_change(&self, session: &Arc<Session>, changed: bool) {
        if !changed {
            return;
        }

        let state = lock(&session.tracker).state();
        let in_view = *lock(&self.viewing) && lock(&self.active).as_deref() == Some(session.id.as_str());
        if state == AgentState::Done && in_view {
            let cleared = lock(&session.tracker).on_viewed();
            if cleared {
                self.after_change(session, true);
            }
            return;
        }

        self.host.emit("session-state", json!({ "id": session.id, "state": state }));
        self.publish_agents();
    }

    /// Puts every session's state in the pill. All of them, not just the one that changed: each
    /// says how many others have something to report, since the pill only shows one.
    fn publish_agents(&self) {
        let sessions: Vec<Arc<Session>> = {
            let order = lock(&self.order);
            let all = lock(&self.sessions);
            order.iter().filter_map(|id| all.get(id).cloned()).collect()
        };
        let states: Vec<AgentState> = sessions.iter().map(|s| lock(&s.tracker).state()).collect();
        let reporting = states.iter().filter(|s| **s != AgentState::Idle).count();

        for (session, state) in sessions.iter().zip(states) {
            let activity = agent_activity(
                &session.id,
                &session.profile.display_name,
                &session.profile.glyph,
                state,
                Some(&session.folder_name),
                reporting.saturating_sub(1),
            );
            match activity {
                Some(activity) => self.hub.publish(activity),
                None => self.hub.remove(&activity_id(&session.id)),
            }
        }
    }

    /// Listens for what `notch --hook` forwards from the agent CLIs.
    pub fn listen_for_hooks(self: &Arc<Self>) {
        let listener = self
            .pipe_name
            .as_str()
            .to_ns_name::<GenericNamespaced>()
            .and_then(|name| ListenerOptions::new().name(name).create_sync());
        let listener = match listener {
            Ok(listener) => listener,
            Err(e) => {
                log::warn!("agent status will not show: no hook socket: {e}");
                return;
            }
        };

        let terminals = Arc::downgrade(self);
        thread::spawn(move || {
            for connection in listener.incoming().flatten() {
                let terminals = terminals.clone();
                // Handed off so a slow client cannot hold up the next one.
                thread::spawn(move || {
                    let reader = BufReader::new(connection.take(MAX_HOOK_MESSAGE));
                    for line in reader.lines().map_while(Result::ok) {
                        let Some(terminals) = terminals.upgrade() else { return };
                        if let Some((session, event)) = hooks::try_parse_message(&line) {
                            terminals.on_hook(&session, &event);
                        }
                    }
                });
            }
        });
    }

    fn on_hook(&self, session_id: &str, event: &str) {
        if let Some(session) = self.find(session_id) {
            let changed = lock(&session.tracker).on_hook_event(event);
            self.after_change(&session, changed);
        }
    }

    fn watch_for_quiet(self: &Arc<Self>) {
        let terminals: Weak<Self> = Arc::downgrade(self);
        thread::spawn(move || loop {
            thread::sleep(QUIET_CHECK);
            let Some(terminals) = terminals.upgrade() else { return };
            terminals.check_for_stalled_agents(Instant::now());
        });
    }

    /// An agent that shows as working but has printed nothing for [`QUIET_THRESHOLD`] was a false start.
    fn check_for_stalled_agents(&self, now: Instant) {
        let sessions: Vec<Arc<Session>> = lock(&self.sessions).values().cloned().collect();
        for session in sessions {
            let quiet = now.saturating_duration_since(*lock(&session.last_activity)) > QUIET_THRESHOLD;
            if quiet && lock(&session.tracker).state() == AgentState::Working {
                let changed = lock(&session.tracker).on_output_quiet();
                self.after_change(&session, changed);
            }
        }
    }
}

/// Waits for a thread to finish, but not forever: a grandchild that kept the terminal open must
/// not keep a finished session from saying so.
fn wait_for(handle: &JoinHandle<()>, timeout: Duration) {
    let deadline = Instant::now() + timeout;
    while !handle.is_finished() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(10));
    }
}

#[cfg(test)]
mod tests;
