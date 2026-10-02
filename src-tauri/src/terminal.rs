//! The built-in terminal: sessions in pseudo consoles, the agent hooks that report on them, and
//! the pill activities that make those reports visible.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, Instant};

use interprocess::local_socket::{prelude::*, GenericNamespaced, ListenerOptions};
use notch_core::agents::{activity_id, agent_activity, hooks, AgentKind, AgentState, AgentStatusTracker};
use notch_core::presets::TerminalProfile;
use portable_pty::{native_pty_system, ChildKiller, CommandBuilder, MasterPty, PtySize};
use serde::Serialize;
use tauri::{AppHandle, Emitter};

use crate::activity_hub::ActivityHub;
use crate::launch;
use crate::output::{encode, OutputQueue};

/// A working agent that has printed nothing for this long was a false start.
const QUIET_THRESHOLD: Duration = Duration::from_secs(15);

/// How often sessions are checked for going quiet.
const QUIET_CHECK: Duration = Duration::from_secs(5);

/// The size a terminal starts at, until the UI reports the real one.
const START_SIZE: PtySize = PtySize { rows: 24, cols: 80, pixel_width: 0, pixel_height: 0 };

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|e| e.into_inner())
}

/// What the UI is told when a session opens.
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct SessionOpened<'a> {
    id: &'a str,
    profile_id: &'a str,
    display_name: &'a str,
    glyph: &'a str,
    folder: &'a str,
    folder_name: &'a str,
    agent: AgentKind,
}

/// The running process of a session; gone once it exits or the session closes.
struct Process {
    master: Box<dyn MasterPty + Send>,
    writer: Box<dyn Write + Send>,
    killer: Box<dyn ChildKiller + Send + Sync>,
}

struct Session {
    id: String,
    profile: TerminalProfile,
    folder: String,
    folder_name: String,
    tracker: Mutex<AgentStatusTracker>,
    process: Mutex<Option<Process>>,
    queue: Arc<OutputQueue>,
    last_activity: Mutex<Instant>,
}

/// Every open session, and the rules for what their state means to the user.
pub struct Terminals {
    app: AppHandle,
    hub: Arc<ActivityHub>,
    sessions: Mutex<HashMap<String, Arc<Session>>>,
    /// Order the tabs were opened in, which is the order activities are counted in.
    order: Mutex<Vec<String>>,
    active: Mutex<Option<String>>,
    viewing: Mutex<bool>,
    pipe_name: String,
    hook_exe: String,
    claude_settings: PathBuf,
}

impl Terminals {
    /// Starts listening for hook reports and watching for stalled agents.
    pub fn start(app: AppHandle, hub: Arc<ActivityHub>) -> Arc<Self> {
        let config = notch_core::settings::SettingsStore::default_path();
        let claude_settings = config.parent().map_or_else(|| PathBuf::from("."), Path::to_path_buf).join("claude-hooks.json");
        let hook_exe = std::env::current_exe().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();

        let terminals = Arc::new(Self {
            app,
            hub,
            sessions: Mutex::default(),
            order: Mutex::default(),
            active: Mutex::default(),
            viewing: Mutex::default(),
            pipe_name: format!("notch-{}-{}", std::process::id(), uuid::Uuid::new_v4().simple()),
            hook_exe,
            claude_settings,
        });
        terminals.listen_for_hooks();
        terminals.watch_for_quiet();
        terminals
    }

    /// Opens a session running `profile` in `folder`. A program that is not installed still gets a
    /// tab, which says so.
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
            last_activity: Mutex::new(Instant::now()),
        });

        lock(&self.sessions).insert(id.clone(), session.clone());
        lock(&self.order).push(id.clone());
        *lock(&self.active) = Some(id.clone());

        self.emit(
            "session-opened",
            &SessionOpened {
                id: &session.id,
                profile_id: &session.profile.id,
                display_name: &session.profile.display_name,
                glyph: &session.profile.glyph,
                folder: &session.folder,
                folder_name: &session.folder_name,
                agent: session.profile.agent,
            },
        );
        self.spawn_flusher(&session);

        match self.start_process(&session) {
            Ok(()) => {}
            Err(problem) => {
                session.queue.push(format!("\r\n\x1b[90m{problem}\x1b[0m\r\n").as_bytes());
                session.queue.close();
            }
        }
        id
    }

    /// Ends a session and takes it out of the pill.
    pub fn close(&self, id: &str) {
        let Some(session) = lock(&self.sessions).remove(id) else { return };
        lock(&self.order).retain(|s| s != id);
        {
            let mut active = lock(&self.active);
            if active.as_deref() == Some(id) {
                *active = None;
            }
        }
        if let Some(mut process) = lock(&session.process).take() {
            let _ = process.killer.kill();
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

    pub fn input(&self, id: &str, data: &str) {
        let Some(session) = self.find(id) else { return };
        if let Some(process) = lock(&session.process).as_mut() {
            let _ = process.writer.write_all(data.as_bytes());
            let _ = process.writer.flush();
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
        if let Some(process) = lock(&session.process).as_ref() {
            let size = PtySize { rows: rows.max(1), cols: cols.max(1), pixel_width: 0, pixel_height: 0 };
            let _ = process.master.resize(size);
        }
    }

    pub fn bell(&self, id: &str) {
        if let Some(session) = self.find(id) {
            let changed = lock(&session.tracker).on_bell();
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

    /// The terminal tab came on or went off screen.
    pub fn set_viewing(&self, viewing: bool) {
        *lock(&self.viewing) = viewing;
        if viewing {
            if let Some(session) = lock(&self.active).clone().and_then(|id| self.find(&id)) {
                let changed = lock(&session.tracker).on_viewed();
                self.after_change(&session, changed);
            }
        }
    }

    pub fn has_sessions(&self) -> bool {
        !lock(&self.sessions).is_empty()
    }

    fn find(&self, id: &str) -> Option<Arc<Session>> {
        lock(&self.sessions).get(id).cloned()
    }

    fn emit<S: Serialize + Clone>(&self, event: &str, payload: &S) {
        if let Err(e) = self.app.emit(event, payload) {
            log::warn!("could not emit {event}: {e}");
        }
    }

    fn start_process(self: &Arc<Self>, session: &Arc<Session>) -> Result<(), String> {
        let profile = &session.profile;
        let executable = launch::resolve(profile).ok_or_else(|| profile.install_hint.clone())?;

        let mut args = launch::base_arguments(profile);
        if profile.agent != AgentKind::None && Path::new(&self.hook_exe).is_file() {
            if profile.agent == AgentKind::Claude {
                if let Some(directory) = self.claude_settings.parent() {
                    let _ = std::fs::create_dir_all(directory);
                }
                std::fs::write(&self.claude_settings, hooks::build_claude_settings(&self.hook_exe))
                    .map_err(|e| format!("Could not write the Claude hook settings: {e}"))?;
            }
            args.extend(hooks::build_arguments(profile.agent, &self.hook_exe, &self.claude_settings.to_string_lossy()));
        }

        let launch = launch::launch_command(&executable, &args);
        let mut command = CommandBuilder::new(&launch.program);
        command.args(&launch.args);
        command.cwd(&session.folder);
        for (name, value) in &profile.environment {
            command.env(name, value);
        }
        command.env(hooks::PIPE_VARIABLE, &self.pipe_name);
        command.env(hooks::SESSION_VARIABLE, &session.id);

        let failed = |what: &str, e: &dyn std::fmt::Display| format!("Could not start {}: {what}: {e}", profile.display_name);
        let pair = native_pty_system().openpty(START_SIZE).map_err(|e| failed("terminal", &e))?;
        let mut child = pair.slave.spawn_command(command).map_err(|e| failed("process", &e))?;
        drop(pair.slave);
        let mut reader = pair.master.try_clone_reader().map_err(|e| failed("reader", &e))?;
        let writer = pair.master.take_writer().map_err(|e| failed("writer", &e))?;

        *lock(&session.process) = Some(Process { master: pair.master, writer, killer: child.clone_killer() });

        let queue = session.queue.clone();
        let reader_session = session.clone();
        thread::spawn(move || {
            let mut buffer = [0u8; 8192];
            while let Ok(read) = reader.read(&mut buffer) {
                if read == 0 {
                    break;
                }
                *lock(&reader_session.last_activity) = Instant::now();
                queue.push(&buffer[..read]);
            }
        });

        let terminals = self.clone();
        let waiting = session.clone();
        thread::spawn(move || {
            let code = child.wait().map_or(-1, |status| i64::from(status.exit_code()));
            terminals.on_exit(&waiting, code);
        });
        Ok(())
    }

    /// Hands output to the UI in few, large events.
    fn spawn_flusher(&self, session: &Arc<Session>) {
        let queue = session.queue.clone();
        let app = self.app.clone();
        let id = session.id.clone();
        thread::spawn(move || {
            while let Some(batch) = queue.next_batch() {
                let payload = serde_json::json!({ "id": id, "data": encode(&batch) });
                if let Err(e) = app.emit("session-output", payload) {
                    log::warn!("could not emit session output: {e}");
                }
            }
        });
    }

    fn on_exit(&self, session: &Arc<Session>, code: i64) {
        // Let what the process printed last reach the screen before the exit notice.
        thread::sleep(Duration::from_millis(100));
        if lock(&session.process).take().is_none() {
            return;
        }
        session.queue.close();
        let changed = lock(&session.tracker).reset();
        self.emit("session-exited", &serde_json::json!({ "id": session.id, "code": code }));
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

        self.emit("session-state", &serde_json::json!({ "id": session.id, "state": state }));
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

    fn listen_for_hooks(self: &Arc<Self>) {
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

        let terminals = self.clone();
        thread::spawn(move || {
            for connection in listener.incoming().flatten() {
                let terminals = terminals.clone();
                thread::spawn(move || {
                    for line in BufReader::new(connection).lines().map_while(Result::ok) {
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
        let terminals = self.clone();
        thread::spawn(move || loop {
            thread::sleep(QUIET_CHECK);
            let sessions: Vec<Arc<Session>> = lock(&terminals.sessions).values().cloned().collect();
            for session in sessions {
                let stalled = *lock(&session.last_activity) + QUIET_THRESHOLD < Instant::now();
                if stalled && lock(&session.tracker).state() == AgentState::Working {
                    let changed = lock(&session.tracker).on_output_quiet();
                    terminals.after_change(&session, changed);
                }
            }
        });
    }
}
