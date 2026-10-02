//! What the commands, the hotkey and the tray share.

use std::path::PathBuf;
use std::sync::{Arc, Mutex, MutexGuard};

use notch_core::settings::{AppSettings, SettingsStore};
use tauri_plugin_global_shortcut::Shortcut;

use crate::activity_hub::ActivityHub;
use crate::terminal::Terminals;
use crate::window::WindowController;

/// The app's long-lived parts, registered once with Tauri's state.
pub struct Backend {
    pub window: WindowController,
    pub hub: Arc<ActivityHub>,
    pub terminals: Arc<Terminals>,
    pub store: SettingsStore,
    pub hotkey: Mutex<Option<Shortcut>>,
    settings: Mutex<AppSettings>,
}

impl Backend {
    pub fn new(
        window: WindowController,
        hub: Arc<ActivityHub>,
        terminals: Arc<Terminals>,
        store: SettingsStore,
        settings: AppSettings,
    ) -> Self {
        Self { window, hub, terminals, store, hotkey: Mutex::new(None), settings: Mutex::new(settings) }
    }

    /// The current settings. Keep the guard short: commands and the hotkey thread both come here.
    pub fn settings(&self) -> MutexGuard<'_, AppSettings> {
        self.settings.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// The folder new sessions start in: the most recent one that still exists, else the home folder.
    pub fn default_folder(&self) -> String {
        let remembered = self.settings().recent_folders.iter().find(|f| PathBuf::from(f).is_dir()).cloned();
        remembered
            .or_else(|| dirs::home_dir().map(|p| p.to_string_lossy().into_owned()))
            .unwrap_or_default()
    }
}
