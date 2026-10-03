//! The commands the UI invokes. See `docs/rust-rewrite.md` for the contract.

use notch_core::activity::Activity;
use notch_core::presets::TerminalProfile;
use notch_core::settings::AppSettings;
use serde::Serialize;
use tauri::{AppHandle, Emitter, State};
use tauri_plugin_dialog::DialogExt;

use crate::hotkey;
use crate::options::StartupOptions;
use crate::state::Backend;
use crate::window::Placement;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    settings: AppSettings,
    profiles: Vec<TerminalProfile>,
    folder: String,
    activities: Vec<Activity>,
    version: &'static str,
    /// The command-line switches this run was started with.
    startup_options: StartupOptions,
    /// How the island meets the screen edge.
    placement: Placement,
    /// `windows`, `linux` or `macos`, for wording that names the system.
    platform: &'static str,
}

#[derive(Serialize, Clone)]
pub struct SettingsChanged {
    settings: AppSettings,
    profiles: Vec<TerminalProfile>,
}

impl SettingsChanged {
    pub fn new(settings: AppSettings) -> Self {
        Self { profiles: settings.profiles(), settings }
    }
}

#[derive(Serialize)]
pub struct Opened {
    id: String,
}

#[tauri::command]
pub fn get_state(backend: State<'_, Backend>, options: State<'_, StartupOptions>) -> Snapshot {
    let settings = backend.settings().clone();
    Snapshot {
        profiles: settings.profiles(),
        settings,
        folder: backend.default_folder(),
        activities: backend.hub.snapshot(),
        version: env!("CARGO_PKG_VERSION"),
        startup_options: options.inner().clone(),
        placement: backend.window.placement(),
        platform: std::env::consts::OS,
    }
}

/// Keeps new settings, applies what takes effect at once, and tells the UI what was kept (the
/// terminal presets are tidied on the way).
#[tauri::command]
pub fn save_settings(app: AppHandle, backend: State<'_, Backend>, settings: AppSettings) -> SettingsChanged {
    let saved = {
        let mut current = backend.settings();
        let mut next = settings;
        // The UI's copy of the recent folders may be stale; the backend is who adds to them.
        next.recent_folders = current.recent_folders.clone();
        *current = next;
        current.clone()
    };
    backend.store.save(&saved);

    hotkey::apply(&app, &backend.hotkey, &saved.open_hotkey);
    backend.window.apply_settings(&saved);
    backend.hub.set_suppressed(&saved.suppressed_activity_ids());

    let changed = SettingsChanged { profiles: saved.profiles(), settings: saved };
    if let Err(e) = app.emit("settings", &changed) {
        log::warn!("could not emit settings: {e}");
    }
    changed
}

#[tauri::command]
pub fn open_session(app: AppHandle, backend: State<'_, Backend>, profile_id: String, folder: String) -> Result<Opened, String> {
    let (profile, snapshot) = {
        let mut settings = backend.settings();
        let profile = settings
            .profiles()
            .into_iter()
            .find(|p| p.id == profile_id)
            .ok_or_else(|| format!("There is no terminal button called {profile_id}."))?;
        settings.remember_folder(&folder);
        (profile, settings.clone())
    };
    backend.store.save(&snapshot);

    let changed = SettingsChanged { profiles: snapshot.profiles(), settings: snapshot };
    if let Err(e) = app.emit("settings", &changed) {
        log::warn!("could not emit settings: {e}");
    }

    Ok(Opened { id: backend.terminals.open(profile, folder) })
}

#[tauri::command]
pub fn session_input(backend: State<'_, Backend>, id: String, data: String) {
    backend.terminals.input(&id, &data);
}

#[tauri::command]
pub fn session_resize(backend: State<'_, Backend>, id: String, cols: u16, rows: u16) {
    backend.terminals.resize(&id, cols, rows);
}

#[tauri::command]
pub fn session_bell(backend: State<'_, Backend>, id: String) {
    backend.terminals.bell(&id);
}

#[tauri::command]
pub fn close_session(backend: State<'_, Backend>, id: String) {
    backend.terminals.close(&id);
}

#[tauri::command]
pub fn session_viewed(backend: State<'_, Backend>, id: String) {
    backend.terminals.viewed(&id);
}

#[tauri::command]
pub fn set_viewing_terminal(backend: State<'_, Backend>, viewing: bool) {
    backend.terminals.set_viewing(viewing);
}

#[tauri::command]
pub async fn pick_folder(app: AppHandle) -> Option<String> {
    tauri::async_runtime::spawn_blocking(move || {
        app.dialog()
            .file()
            .set_title("Folder for new terminal sessions")
            .blocking_pick_folder()
            .and_then(|path| path.into_path().ok())
            .map(|path| path.to_string_lossy().into_owned())
    })
    .await
    .ok()
    .flatten()
}

#[tauri::command]
pub fn set_expanded(backend: State<'_, Backend>, expanded: bool) {
    backend.window.set_expanded(expanded);
}

#[tauri::command]
pub fn quit(app: AppHandle) {
    app.exit(0);
}
