//! Notch.exe. Ported from `src/Notch.App`.

// A release build is a GUI program; a debug build keeps its console for logs.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    tauri::Builder::default().run(tauri::generate_context!()).expect("Notch could not start its window");
}
