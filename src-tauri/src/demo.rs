//! Demo mode, for seeing every pill state without real system events and for rendering the
//! pictures on the website.
//!
//! `notch --demo` loops through fake activities. `notch --demo --screenshots=<folder>` instead
//! steps through every state the website shows, asks the page to draw itself into a PNG for each
//! (`ui/demo-capture.js`, which works the same on every OS because the page renders itself) and
//! quits when the last picture is saved. The exit code is 0 only when every picture was saved.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use base64::Engine;
use notch_core::activity::Activity;
use notch_core::agents::{activity_id, agent_activity, AgentState};
use notch_core::hud;
use serde_json::json;
use tauri::{AppHandle, Emitter, Manager, State};

use crate::activity_hub::ActivityHub;
use crate::state::Backend;

/// How long each fake activity stays before the next one replaces it.
const SCRIPT_INTERVAL: Duration = Duration::from_secs(3);
/// Time for the page to load before the first picture.
const STARTUP_WAIT: Duration = Duration::from_secs(4);
/// Time for the pill to finish its animation after a change.
const SETTLE: Duration = Duration::from_millis(1500);
/// The Stats tab draws live charts; it needs time to fill them.
const STATS_WAIT: Duration = Duration::from_secs(10);
/// How long a picture may take to come back from the page.
const PICTURE_TIMEOUT: Duration = Duration::from_secs(30);

/// The pictures the website shows, in the order they are taken.
const PICTURES: [&str; 12] = [
    "pill-media",
    "pill-agent-working",
    "pill-agent-needs-input",
    "pill-agent-done",
    "pill-volume",
    "pill-charging",
    "pill-bluetooth",
    "pill-low-battery",
    "pill-caps-lock",
    "home",
    "stats",
    "shelf",
];

const PNG_SIGNATURE: [u8; 8] = [0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];

/// What the command line asked for.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct DemoOptions {
    /// `--demo`: fake activities.
    pub demo: bool,
    /// `--screenshots=<folder>`: save the website's pictures there and quit. Implies `--demo`.
    pub screenshots: Option<PathBuf>,
}

impl DemoOptions {
    /// Reads the switches, without the program name. Names are not case-sensitive.
    pub fn parse<I, S>(args: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let mut options = Self::default();
        for argument in args {
            let argument = argument.as_ref();
            let (name, value) = argument.split_once('=').map_or((argument, None), |(n, v)| (n, Some(v)));
            match (name.to_ascii_lowercase().as_str(), value) {
                ("--demo", None) => options.demo = true,
                ("--screenshots", Some(folder)) if !folder.is_empty() => {
                    options.demo = true;
                    options.screenshots = Some(PathBuf::from(folder.trim_matches('"')));
                }
                _ => {}
            }
        }
        options
    }
}

/// One change to the activities.
#[derive(Clone, Debug, PartialEq)]
enum Change {
    Publish(Activity),
    Remove(String),
}

fn agent(session: &str, name: &str, glyph: &str, state: AgentState, folder: Option<&str>, others: usize) -> Change {
    agent_activity(session, name, glyph, state, folder, others)
        .map_or_else(|| Change::Remove(activity_id(session)), Change::Publish)
}

/// The activities the demo loops through, one group of changes per step.
fn script() -> Vec<Vec<Change>> {
    let claude = |state| agent("demo", "Claude", "\u{2733}", state, Some("notch"), 0);
    vec![
        vec![Change::Publish(hud::volume(0.6, false))],
        vec![claude(AgentState::Working)],
        vec![claude(AgentState::NeedsInput)],
        vec![claude(AgentState::Done)],
        vec![Change::Publish(hud::power(true, 82))],
        vec![Change::Publish(hud::bluetooth("Headphones", true))],
        vec![Change::Publish(hud::brightness(0.4))],
        vec![Change::Publish(hud::caps_lock(true))],
        // Two sessions at once: each says there is another.
        vec![
            agent("demo", "Claude", "\u{2733}", AgentState::Working, Some("notch"), 1),
            agent("demo2", "Codex", "\u{25C8}", AgentState::NeedsInput, Some("website"), 1),
        ],
        vec![Change::Remove(activity_id("demo")), Change::Remove(activity_id("demo2"))],
        vec![Change::Publish(hud::low_battery(9))],
    ]
}

fn apply(hub: &ActivityHub, changes: Vec<Change>) {
    for change in changes {
        match change {
            Change::Publish(activity) => hub.publish(activity),
            Change::Remove(id) => hub.remove(&id),
        }
    }
}

/// A picture name is a file name: lower-case words joined by dashes, nothing that could leave the folder.
fn is_picture_name(name: &str) -> bool {
    !name.is_empty() && name.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

/// The PNG inside the page's base64 answer.
fn decode_picture(data: &str) -> Result<Vec<u8>, String> {
    let bytes = base64::engine::general_purpose::STANDARD.decode(data.trim()).map_err(|e| e.to_string())?;
    if bytes.starts_with(&PNG_SIGNATURE) {
        Ok(bytes)
    } else {
        Err("the page did not send a PNG".into())
    }
}

/// How a requested picture ended: its name and what went wrong, if anything.
type Outcome = (String, Result<(), String>);

/// What the picture command needs: where to save, and who is waiting for the answer.
pub struct DemoState {
    folder: Option<PathBuf>,
    saved: Mutex<Option<Sender<Outcome>>>,
}

/// Starts what the command line asked for. Call once the backend is registered.
pub fn start(app: &AppHandle, hub: &Arc<ActivityHub>, options: &DemoOptions) {
    let (sender, receiver) = mpsc::channel();
    app.manage(DemoState { folder: options.screenshots.clone(), saved: Mutex::new(Some(sender)) });

    if !options.demo {
        return;
    }
    let app = app.clone();
    let hub = hub.clone();
    match options.screenshots.clone() {
        Some(folder) => {
            thread::spawn(move || {
                let code = match take_pictures(&app, &hub, &folder, &receiver) {
                    Ok(()) => 0,
                    Err(e) => {
                        log::error!("screenshots failed: {e}");
                        1
                    }
                };
                app.exit(code);
            });
        }
        None => {
            thread::spawn(move || {
                for changes in script().into_iter().cycle() {
                    apply(&hub, changes);
                    thread::sleep(SCRIPT_INTERVAL);
                }
            });
        }
    }
}

/// Saves a picture of every state the website shows.
fn take_pictures(app: &AppHandle, hub: &ActivityHub, folder: &Path, saved: &Receiver<Outcome>) -> Result<(), String> {
    fs::create_dir_all(folder).map_err(|e| format!("cannot create {}: {e}", folder.display()))?;
    thread::sleep(STARTUP_WAIT);

    let shoot = |name: &str| -> Result<(), String> {
        thread::sleep(SETTLE);
        app.emit("demo-capture", json!({ "name": name })).map_err(|e| e.to_string())?;
        loop {
            let (answered, result) = saved.recv_timeout(PICTURE_TIMEOUT).map_err(|_| format!("no picture arrived for {name}"))?;
            if answered == name {
                return result.map_err(|e| format!("{name}: {e}"));
            }
        }
    };
    // A state shown on its own: published, pictured, taken away again.
    let compact = |name: &str, activity: Activity| -> Result<(), String> {
        let id = activity.id.clone();
        hub.publish(activity);
        let result = shoot(name);
        hub.remove(&id);
        result
    };
    let claude = |state| agent_activity("shot", "Claude", "\u{2733}", state, None, 0);

    shoot("pill-media")?;
    for (name, state) in [
        ("pill-agent-working", AgentState::Working),
        ("pill-agent-needs-input", AgentState::NeedsInput),
        ("pill-agent-done", AgentState::Done),
    ] {
        compact(name, claude(state).ok_or("no agent activity")?)?;
    }
    compact("pill-volume", hud::volume(0.6, false))?;
    compact("pill-charging", hud::power(true, 82))?;
    compact("pill-bluetooth", hud::bluetooth("Headphones", true))?;
    compact("pill-low-battery", hud::low_battery(9))?;
    compact("pill-caps-lock", hud::caps_lock(true))?;

    // The expanded notch stays open until the hotkey would close it, which nobody presses.
    app.state::<Backend>().window.request_expanded(true, false, true);
    for (tab, name, wait) in [("home", "home", None), ("stats", "stats", Some(STATS_WAIT)), ("shelf", "shelf", None)] {
        app.emit("demo-view", json!({ "tab": tab })).map_err(|e| e.to_string())?;
        if let Some(wait) = wait {
            thread::sleep(wait);
        }
        shoot(name)?;
    }
    Ok(())
}

/// The page's answer to `demo-capture`: the picture as base64, or what went wrong. Saves it as `<name>.png`.
#[tauri::command]
pub fn demo_save_picture(state: State<'_, DemoState>, name: String, data: String, error: Option<String>) -> Result<(), String> {
    let result = save_picture(state.folder.as_deref(), &name, &data, error);
    let sender = state.saved.lock().unwrap_or_else(|e| e.into_inner()).clone();
    if let Some(sender) = sender {
        // Nobody listens outside a screenshot run, which is fine.
        let _ = sender.send((name, result.clone()));
    }
    result
}

fn save_picture(folder: Option<&Path>, name: &str, data: &str, error: Option<String>) -> Result<(), String> {
    let folder = folder.ok_or("not taking screenshots")?;
    if !is_picture_name(name) {
        return Err(format!("{name:?} is not a picture name"));
    }
    if let Some(error) = error {
        return Err(error);
    }
    let png = decode_picture(data)?;
    fs::write(folder.join(format!("{name}.png")), png).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_switches() {
        let options = DemoOptions::parse(["--pin-open", "--DEMO", "--screenshots=\"C:\\out dir\""]);
        assert!(options.demo);
        assert_eq!(options.screenshots, Some(PathBuf::from("C:\\out dir")));
    }

    #[test]
    fn nothing_is_asked_for_by_default() {
        assert_eq!(DemoOptions::parse(["--settings", "--screenshots="]), DemoOptions::default());
    }

    #[test]
    fn screenshots_imply_the_demo() {
        assert!(DemoOptions::parse(["--screenshots=out"]).demo);
    }

    #[test]
    fn the_website_gets_twelve_pictures() {
        assert_eq!(PICTURES.len(), 12);
        assert!(PICTURES.iter().all(|name| is_picture_name(name)));
    }

    #[test]
    fn picture_names_cannot_leave_the_folder() {
        for name in ["", "../x", "a/b", "a\\b", "A", "x.png"] {
            assert!(!is_picture_name(name), "{name}");
        }
        assert!(is_picture_name("pill-agent-needs-input"));
    }

    #[test]
    fn the_script_ends_clean_and_every_step_changes_something() {
        let steps = script();
        assert!(steps.iter().all(|step| !step.is_empty()));
        assert!(matches!(steps.first().and_then(|s| s.first()), Some(Change::Publish(a)) if a.id == "hud.volume"));
    }

    #[test]
    fn only_png_data_is_saved() {
        let png = base64::engine::general_purpose::STANDARD.encode([&PNG_SIGNATURE[..], b"rest"].concat());
        assert!(decode_picture(&png).is_ok());
        assert!(decode_picture("aGVsbG8=").is_err());
        assert!(decode_picture("not base64!").is_err());
    }

    #[test]
    fn saving_needs_a_screenshot_run() {
        assert!(save_picture(None, "home", "", None).is_err());
    }

    #[test]
    fn a_picture_is_written_under_its_name() {
        let folder = std::env::temp_dir().join(format!("notch-demo-test-{}", std::process::id()));
        fs::create_dir_all(&folder).unwrap();
        let png = base64::engine::general_purpose::STANDARD.encode([&PNG_SIGNATURE[..], b"rest"].concat());

        assert_eq!(save_picture(Some(&folder), "home", &png, None), Ok(()));
        assert!(folder.join("home.png").is_file());
        assert!(save_picture(Some(&folder), "home", &png, Some("boom".into())).is_err());
        fs::remove_dir_all(&folder).unwrap();
    }
}
