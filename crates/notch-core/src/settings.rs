//! The user's settings, stored as JSON.

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::presets::{profiles_from_lines, TerminalProfile, DEFAULT_PRESETS};

const MAX_RECENT_FOLDERS: usize = 8;

/// A notch attached to the screen edge, or a floating island.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum NotchStyle {
    /// Grows out of the screen edge like a camera notch: flat against it, rounded away from it.
    #[default]
    Notch,
    /// A pill rounded all the way around that floats a little clear of the edge.
    Island,
}

/// Where the notch sits.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum NotchPosition {
    /// Middle of the top edge; the panel opens downwards.
    #[default]
    TopCenter,
    /// In the taskbar's far left corner; the panel opens upwards.
    TaskbarLeft,
}

/// Dark or light colours, or whichever the system is set to for apps.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum NotchTheme {
    /// Black island with light text.
    #[default]
    Dark,
    /// Light island with dark text.
    Light,
    /// Follows the system.
    System,
}

/// Settings as saved. Unknown fields in the file are ignored and missing ones take their defaults.
#[derive(Clone, PartialEq, Eq, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct AppSettings {
    /// Which display hosts the notch, as an index into the system's display list. `None` means the primary display.
    pub display_index: Option<i32>,
    pub style: NotchStyle,
    pub position: NotchPosition,
    /// Open the notch by hovering over it. When off, it opens on click only.
    pub expand_on_hover: bool,
    /// Keys that open and close the notch from any app. Empty, or anything unreadable, means no hotkey.
    /// The default has no Win key, whose combinations Windows keeps taking for itself, and not Ctrl+Alt,
    /// which is how AltGr types letters.
    pub open_hotkey: String,
    pub hide_in_fullscreen: bool,
    pub theme: NotchTheme,
    /// Name of the glow colour preset that tints buttons, bars and the selected tab.
    /// Anything that is not a preset, such as "None", leaves them uncoloured.
    pub accent_color: String,
    /// Light up the pill in a colour and rhythm that matches what is happening.
    pub glow_effects: bool,
    /// Glow brightness in percent of the standard.
    pub glow_intensity: i32,
    /// Download and install new releases without asking.
    pub auto_update: bool,
    /// The buttons that start terminal sessions, one per line as `Name = [VAR=value ...] command [args]`,
    /// for example `Work Claude = CLAUDE_CONFIG_DIR=C:\work claude`.
    pub terminal_presets: Vec<String>,
    /// Folders terminal sessions were started in, most recent first.
    pub recent_folders: Vec<String>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            display_index: None,
            style: NotchStyle::default(),
            position: NotchPosition::default(),
            expand_on_hover: true,
            open_hotkey: "Alt+Shift+N".to_owned(),
            hide_in_fullscreen: true,
            theme: NotchTheme::default(),
            accent_color: "Blue".to_owned(),
            glow_effects: true,
            glow_intensity: 100,
            auto_update: true,
            terminal_presets: DEFAULT_PRESETS.iter().map(|l| (*l).to_owned()).collect(),
            recent_folders: Vec::new(),
        }
    }
}

impl AppSettings {
    /// The launcher buttons the terminal presets describe.
    pub fn profiles(&self) -> Vec<TerminalProfile> {
        profiles_from_lines(&self.terminal_presets)
    }

    /// Puts `folder` first in the recent folders, dropping an earlier spelling of it and anything past the eighth.
    pub fn remember_folder(&mut self, folder: &str) {
        let wanted = folder.to_lowercase();
        self.recent_folders.retain(|f| f.to_lowercase() != wanted);
        self.recent_folders.insert(0, folder.to_owned());
        self.recent_folders.truncate(MAX_RECENT_FOLDERS);
    }
}

/// Loads and saves [`AppSettings`] as JSON. A missing or corrupt file yields defaults.
#[derive(Clone, Debug)]
pub struct SettingsStore {
    path: PathBuf,
}

impl SettingsStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    /// `settings.json` in a `notch` folder of the platform's config directory.
    pub fn default_path() -> PathBuf {
        dirs::config_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("notch")
            .join("settings.json")
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> AppSettings {
        fs::read_to_string(&self.path)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default()
    }

    /// Settings are a convenience; failing to persist them must not take the app down, so errors are swallowed.
    pub fn save(&self, settings: &AppSettings) {
        let _ = self.try_save(settings);
    }

    fn try_save(&self, settings: &AppSettings) -> std::io::Result<()> {
        if let Some(dir) = self.path.parent() {
            fs::create_dir_all(dir)?;
        }
        let json = serde_json::to_string_pretty(settings).map_err(std::io::Error::other)?;

        // Write to a temporary file first so a crash cannot leave a half-written settings file.
        let mut temporary = self.path.clone().into_os_string();
        temporary.push(".tmp");
        let temporary = PathBuf::from(temporary);
        fs::write(&temporary, json)?;
        fs::rename(&temporary, &self.path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(tag: &str) -> Self {
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos());
            let dir = std::env::temp_dir().join(format!("notch-test-{tag}-{}-{nanos}", std::process::id()));
            Self(dir)
        }

        fn file(&self) -> PathBuf {
            self.0.join("settings.json")
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn defaults_match_the_contract() {
        let s = AppSettings::default();
        assert_eq!(s.display_index, None);
        assert_eq!((s.style, s.position, s.theme), (NotchStyle::Notch, NotchPosition::TopCenter, NotchTheme::Dark));
        assert!(s.expand_on_hover && s.hide_in_fullscreen && s.glow_effects && s.auto_update);
        assert_eq!((s.open_hotkey.as_str(), s.accent_color.as_str(), s.glow_intensity), ("Alt+Shift+N", "Blue", 100));
        assert_eq!(s.profiles().len(), 3);
    }

    #[test]
    fn remember_folder_dedupes_ignoring_case_and_caps_the_list() {
        let mut s = AppSettings::default();
        s.remember_folder(r"C:\a");
        s.remember_folder(r"C:\b");
        s.remember_folder(r"c:\A");
        assert_eq!(s.recent_folders, [r"c:\A", r"C:\b"]);

        for i in 0..20 {
            s.remember_folder(&format!("/f{i}"));
        }
        assert_eq!(s.recent_folders.len(), MAX_RECENT_FOLDERS);
        assert_eq!(s.recent_folders[0], "/f19");
    }

    #[test]
    fn partial_files_load_with_defaults_and_unknown_fields_are_ignored() {
        let s: AppSettings = serde_json::from_str(
            r#"{"theme":"light","position":"taskbarLeft","displayIndex":1,"showMedia":false,"timerPresets":[1]}"#,
        )
        .expect("loads");
        assert_eq!((s.theme, s.position, s.display_index), (NotchTheme::Light, NotchPosition::TaskbarLeft, Some(1)));
        assert!(s.expand_on_hover);
        assert_eq!(s.terminal_presets, DEFAULT_PRESETS);
    }

    #[test]
    fn round_trips_and_survives_a_corrupt_file() {
        let dir = TempDir::new("settings");
        let store = SettingsStore::new(dir.file());
        assert!(store.load().recent_folders.is_empty());

        let mut settings = AppSettings {
            style: NotchStyle::Island,
            display_index: Some(2),
            ..AppSettings::default()
        };
        settings.remember_folder(r"C:\a");
        settings.remember_folder(r"C:\b");
        settings.remember_folder(r"c:\A");
        store.save(&settings);

        let loaded = store.load();
        assert_eq!(loaded, settings);
        assert_eq!(loaded.recent_folders, [r"c:\A", r"C:\b"]);
        assert!(!dir.0.join("settings.json.tmp").exists());

        fs::write(dir.file(), "{ broken").expect("writes");
        assert!(store.load().recent_folders.is_empty());
        assert_eq!(store.load(), AppSettings::default());
    }

    #[test]
    fn a_failed_save_is_swallowed() {
        let dir = TempDir::new("blocked");
        fs::create_dir_all(&dir.0).expect("creates");
        // A file where the settings folder should be makes every write fail.
        let blocker = dir.0.join("blocker");
        fs::write(&blocker, "x").expect("writes");
        SettingsStore::new(blocker.join("settings.json")).save(&AppSettings::default());
    }

    #[test]
    fn default_path_ends_in_notch_settings() {
        let path = SettingsStore::default_path();
        assert!(path.ends_with(Path::new("notch").join("settings.json")));
    }
}
