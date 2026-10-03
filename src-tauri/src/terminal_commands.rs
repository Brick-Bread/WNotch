//! The commands only the Terminal tab uses; the session ones every tab shares are in `commands`.

use std::path::Path;

use tauri::{AppHandle, Emitter, Manager, State};

use crate::commands::SettingsChanged;
use crate::state::Backend;

/// The terminal exists in the page and has measured itself: the process starts at that size.
#[tauri::command]
pub fn session_start(backend: State<'_, Backend>, id: String, cols: u16, rows: u16) {
    backend.terminals.start(&id, cols, rows);
}

/// The user switched to this session (see `Terminals::activate`).
#[tauri::command]
pub fn session_activate(backend: State<'_, Backend>, id: String) {
    backend.terminals.activate(&id);
}

/// Gives the window the keyboard, which a terminal opened from a click on a closed notch needs.
#[tauri::command]
pub fn focus_window(app: AppHandle) {
    if let Some(Err(e)) = app.get_webview_window("main").map(|window| window.set_focus()) {
        log::debug!("could not focus the window: {e}");
    }
}

/// Makes `folder` the one new sessions start in: it goes to the top of the recent folders.
#[tauri::command]
pub fn set_terminal_folder(app: AppHandle, backend: State<'_, Backend>, folder: String) {
    let snapshot = {
        let mut settings = backend.settings();
        settings.remember_folder(&folder);
        settings.clone()
    };
    backend.store.save(&snapshot);
    if let Err(e) = app.emit("settings", SettingsChanged::new(snapshot)) {
        log::warn!("could not emit settings: {e}");
    }
}

/// The folders among `folders` that still exist, in the order given.
#[tauri::command]
pub fn existing_folders(folders: Vec<String>) -> Vec<String> {
    folders.into_iter().filter(|folder| Path::new(folder).is_dir()).collect()
}
