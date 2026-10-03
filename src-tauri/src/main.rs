// A release build is a Windows GUI program, so it opens no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod activity_hub;
mod autostart;
mod commands;
mod demo;
mod fullscreen;
mod hook;
mod hotkey;
mod hud;
mod launch;
mod media;
mod options;
mod output;
mod pty;
mod shell_commands;
mod shelf;
mod state;
mod terminal;
mod terminal_commands;
mod tray;
mod updater;
mod widgets;
mod window;

use std::sync::Arc;

use notch_core::settings::SettingsStore;
use tauri::{Emitter, Manager, RunEvent};

use crate::activity_hub::ActivityHub;
use crate::hotkey::HotkeyStatus;
use crate::options::StartupOptions;
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
            app.state::<Backend>().window.request_expanded(true, true, false);
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
            terminal_commands::session_start,
            terminal_commands::session_activate,
            terminal_commands::focus_window,
            terminal_commands::set_terminal_folder,
            terminal_commands::existing_folders,
            commands::pick_folder,
            commands::set_expanded,
            commands::quit,
            shell_commands::list_displays,
            shell_commands::hotkey_status,
            shell_commands::get_autostart,
            shell_commands::set_autostart,
            shell_commands::pointer_position,
            shell_commands::validate_settings,
            shelf::shelf_items,
            shelf::shelf_add,
            shelf::shelf_remove,
            shelf::shelf_clear,
            shelf::shelf_open,
            shelf::shelf_reveal,
            shelf::shelf_thumbnail,
            shelf::shelf_start_drag,
            media::get_media,
            media::media_control,
            media::media_seek,
            updater::check_updates_now,
            updater::update_status,
            demo::demo_save_picture,
            widgets::timer_start,
            widgets::timer_pomodoro,
            widgets::timer_pause,
            widgets::timer_resume,
            widgets::timer_reset,
            widgets::timer_get,
            widgets::calendar_refresh,
            widgets::calendar_get,
            widgets::set_stats_visible,
        ])
        .setup(|app| {
            let store = SettingsStore::new(SettingsStore::default_path());
            let settings = store.load();

            let hub = Arc::new(ActivityHub::new());
            let terminals = Terminals::start(app.handle().clone(), hub.clone());
            let options = StartupOptions::parse(std::env::args().skip(1));
            let window = WindowController::new(app.handle().clone(), &settings, options.clone());
            app.manage(options);
            app.manage(HotkeyStatus::default());
            hub.spawn_emitter(app.handle().clone(), window.clone());
            hub.set_suppressed(&settings.suppressed_activity_ids());
            hud::start(hub.clone());

            app.manage(media::MediaService::start(app.handle().clone(), hub.clone()));
            app.manage(widgets::Widgets::start(app.handle().clone(), hub.clone(), &settings));

            let hotkey_text = settings.open_hotkey.clone();
            app.manage(Backend::new(window.clone(), hub, terminals, store, settings));

            let backend = app.state::<Backend>();
            hotkey::apply(app.handle(), &backend.hotkey, &hotkey_text);
            tray::create(app.handle())?;
            shelf::ShelfService::setup(app.handle());
            let updater = updater::Updater::start(app.handle());
            app.manage(updater);
            demo::start(app.handle(), &backend.hub, &demo::DemoOptions::parse(std::env::args().skip(1)));
            window.show();
            window.start_housekeeping();
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
    app.state::<Backend>().window.request_expanded(true, true, false);
    if let Err(e) = app.emit("open-settings", ()) {
        log::warn!("could not emit open-settings: {e}");
    }
}
