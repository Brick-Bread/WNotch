//! Notch.exe. Ported from `src/Notch.App`.

// A release build is a GUI program; a debug build keeps its console for logs.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod shell;
mod view;

use std::sync::{Arc, Mutex, OnceLock};
use std::time::Instant;

use notch_core::media::{MediaService, publisher};
use notch_core::settings::{DataFolders, NotchPosition, NotchStyle, SettingsStore};
use notch_core::shell::island::Shape;
use notch_core::shell::opening::Holds;
use notch_platform::hud::SystemHudSources;
use notch_platform::media::GsmtcMediaService;
use serde::Deserialize;
use tauri::{Manager, State};

use crate::shell::{Activities, Overrides, POLL, Shell};

type SharedShell = Arc<Mutex<Shell>>;

fn lock(shell: &SharedShell) -> std::sync::MutexGuard<'_, Shell> {
    shell.lock().unwrap_or_else(|p| p.into_inner())
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct HoldsArgs {
    drag_out: bool,
    menu_open: bool,
    home_drag: bool,
    sheet_open: bool,
    keyboard_in_use: bool,
}

#[tauri::command]
fn shell_ready(shell: State<'_, SharedShell>) {
    lock(&shell).resend();
}

#[tauri::command]
fn shape_target(shell: State<'_, SharedShell>, width: f64, height: f64, radius: f64) {
    lock(&shell).shape_target(Shape::new(width, height, radius));
}

#[tauri::command]
fn island_clicked(shell: State<'_, SharedShell>) {
    lock(&shell).island_clicked();
}

#[tauri::command]
fn set_holds(shell: State<'_, SharedShell>, holds: HoldsArgs) {
    lock(&shell).set_holds(Holds {
        drag_out: holds.drag_out,
        menu_open: holds.menu_open,
        home_drag: holds.home_drag,
        sheet_open: holds.sheet_open,
        keyboard_in_use: holds.keyboard_in_use,
    });
}

/// `--name=value`, matched ignoring case; the first one wins.
fn option<'a>(arguments: &'a [String], name: &str) -> Option<&'a str> {
    let prefix = format!("--{name}=");
    arguments
        .iter()
        .find_map(|a| a.get(..prefix.len()).filter(|p| p.eq_ignore_ascii_case(&prefix)).map(|_| &a[prefix.len()..]))
}

fn flag(arguments: &[String], name: &str) -> bool {
    arguments.iter().any(|a| a.eq_ignore_ascii_case(&format!("--{name}")))
}

fn overrides(arguments: &[String]) -> Overrides {
    Overrides {
        // --display is 1-based, as Settings lists the displays.
        display: option(arguments, "display").and_then(|d| d.parse::<usize>().ok()).and_then(|d| d.checked_sub(1)),
        style: option(arguments, "style").and_then(NotchStyle::from_name),
        position: option(arguments, "position").and_then(NotchPosition::from_name),
        pin_open: flag(arguments, "pin-open"),
    }
}

fn main() {
    let arguments: Vec<String> = std::env::args().collect();
    let folders = DataFolders::current();
    folders.seed_settings();
    let settings = SettingsStore::new(folders.settings_file()).load();

    let activities = Activities::new();
    activities.with(|manager| manager.set_suppressed(settings.suppressed_activity_ids()));

    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![shell_ready, shape_target, island_clicked, set_holds])
        .setup(move |app| {
            let window = app.get_webview_window("notch").expect("the notch window is declared in tauri.conf.json");
            let shell: SharedShell =
                Arc::new(Mutex::new(Shell::new(window, settings, overrides(&arguments), activities.clone())));
            app.manage(Arc::clone(&shell));

            tauri::async_runtime::spawn(async move {
                loop {
                    lock(&shell).tick(Instant::now());
                    tokio::time::sleep(POLL).await;
                }
            });

            start_sources(&activities);
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("Notch could not start its window");
}

/// The system's HUDs and the media session, publishing into the activities. Started off the UI
/// thread: their COM callbacks could not be delivered there.
fn start_sources(activities: &Activities) {
    let hud = activities.clone();
    std::thread::spawn(move || {
        let sources = SystemHudSources::start(Arc::new(move |activity| {
            hud.with(|manager| manager.publish(activity, Instant::now()));
        }));
        // They live as long as the app.
        std::mem::forget(sources);
    });

    let media = activities.clone();
    std::thread::spawn(move || {
        static SERVICE: OnceLock<GsmtcMediaService> = OnceLock::new();
        let started = GsmtcMediaService::start(move || {
            if let Some(service) = SERVICE.get() {
                let current = service.current();
                media.with(|manager| publisher::publish(current.as_ref(), manager, Instant::now()));
            }
        });
        if let Ok(service) = started {
            let _ = SERVICE.set(service);
        }
    });
}
