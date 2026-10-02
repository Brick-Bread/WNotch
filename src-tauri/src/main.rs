// A release build is a Windows GUI program, so it opens no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod activity_hub;
mod commands;
mod hook;
mod hotkey;
mod launch;
mod output;
mod state;
mod terminal;
mod tray;
mod window;

use std::sync::Arc;

use notch_core::settings::SettingsStore;
use tauri::{Emitter, Manager, RunEvent};

use crate::activity_hub::ActivityHub;
use crate::state::Backend;
use crate::terminal::Terminals;
use crate::window::WindowController;

fn main() {
    // The agent CLIs run `notch --hook` to report what they are doing; that must not start the app.
    if std::env::args().any(|argument| argument == "--hook") {
        hook::run();
        return;
    }

    env_logger::init();
    if let Err(e) = run() {
        log::error!("Notch could not start: {e}");
    }
}

fn run() -> tauri::Result<()> {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            // Starting Notch again opens the one that is running.
            app.state::<Backend>().window.request_expanded(true);
        }))
        .plugin(tauri_plugin_global_shortcut::Builder::new().build())
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            commands::get_state,
            commands::save_settings,
            commands::open_session,
            commands::session_input,
            commands::session_resize,
            commands::session_bell,
            commands::close_session,
            commands::session_viewed,
            commands::set_viewing_terminal,
            commands::pick_folder,
            commands::set_expanded,
            commands::quit,
        ])
        .setup(|app| {
            let store = SettingsStore::new(SettingsStore::default_path());
            let settings = store.load();

            let hub = Arc::new(ActivityHub::new());
            let terminals = Terminals::start(app.handle().clone(), hub.clone());
            let window = WindowController::new(app.handle().clone(), settings.display_index);
            hub.spawn_emitter(app.handle().clone(), window.clone());

            let hotkey_text = settings.open_hotkey.clone();
            app.manage(Backend::new(window.clone(), hub, terminals, store, settings));

            let backend = app.state::<Backend>();
            hotkey::apply(app.handle(), &backend.hotkey, &hotkey_text);
            tray::create(app.handle())?;
            window.show();
            Ok(())
        })
        .build(tauri::generate_context!())?;

    app.run(|app, event| {
        if let RunEvent::Exit = event {
            app.state::<Backend>().terminals.shutdown();
        }
    });
    Ok(())
}

/// What the tray's Settings entry does: open the notch on its settings panel.
pub fn show_settings(app: &tauri::AppHandle) {
    app.state::<Backend>().window.request_expanded(true);
    if let Err(e) = app.emit("open-settings", ()) {
        log::warn!("could not emit open-settings: {e}");
    }
}
