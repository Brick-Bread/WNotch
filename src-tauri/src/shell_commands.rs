//! The shell's commands: displays, the hotkey's status, starting with the system, and the checks
//! the settings panel runs before it saves.

use notch_core::presets::parse_preset;
use notch_core::settings::AppSettings;
use notch_core::shell::Hotkey;
use serde::Serialize;
use tauri::State;

use crate::autostart;
use crate::hotkey::{self, HotkeyStatus};
use crate::state::Backend;
use crate::window::DisplayEntry;

/// What [`validate_settings`] found.
#[derive(Serialize, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Validation {
    /// The first thing that cannot be saved, if any.
    pub problem: Option<SettingsProblem>,
    /// The open hotkey in its tidy spelling (`Ctrl+Alt+Shift+Win` order); as typed when it is empty or unreadable.
    pub open_hotkey: String,
}

/// Something in the settings that cannot be saved, worded for the user.
#[derive(Serialize, Debug, PartialEq, Eq)]
pub struct SettingsProblem {
    /// The settings field the problem is in: `terminalPresets` or `openHotkey`.
    pub field: &'static str,
    pub message: String,
}

/// The displays the settings list after "Primary display".
#[tauri::command]
pub fn list_displays(backend: State<'_, Backend>) -> Vec<DisplayEntry> {
    backend.window.displays()
}

/// What is wrong with the registered hotkey, or `None` when it works or none is wanted.
#[tauri::command]
pub fn hotkey_status(status: State<'_, HotkeyStatus>) -> Option<String> {
    status.problem()
}

/// Whether Notch starts with the system.
#[tauri::command]
pub fn get_autostart() -> bool {
    autostart::is_enabled()
}

/// Turns starting with the system on or off; the error says why it could not.
#[tauri::command]
pub fn set_autostart(enabled: bool) -> Result<(), String> {
    autostart::set_enabled(enabled).map_err(|e| format!("Could not change the start-with-system setting: {e}"))
}

/// The pointer in the window's logical pixels; `None` when the system will not say.
#[tauri::command]
pub fn pointer_position(backend: State<'_, Backend>) -> Option<(f64, f64)> {
    backend.window.pointer_position()
}

/// Checks what the settings panel is about to save and says what is wrong with the first thing
/// that cannot be read, worded as the C# message boxes were. Also tidies the hotkey's spelling.
#[tauri::command]
pub fn validate_settings(settings: AppSettings) -> Validation {
    let typed = settings.open_hotkey.trim();
    let open_hotkey = Hotkey::parse(typed).map_or_else(|| typed.to_owned(), |hotkey| hotkey.to_string());
    Validation { problem: first_problem(&settings), open_hotkey }
}

fn first_problem(settings: &AppSettings) -> Option<SettingsProblem> {
    let mut ids: Vec<String> = Vec::new();
    for line in settings.terminal_presets.iter().map(|l| l.trim()).filter(|l| !l.is_empty()) {
        let taken: Vec<&str> = ids.iter().map(String::as_str).collect();
        let Some(profile) = parse_preset(line, &taken) else {
            return Some(SettingsProblem {
                field: "terminalPresets",
                message: format!(
                    "This terminal button could not be read:\n\n{line}\n\nUse a name, an equals sign and a command, for example \"Codex fork = my-codex --model o3\". Put arguments that contain spaces in double quotes."
                ),
            });
        };
        ids.push(profile.id);
    }

    let hotkey = settings.open_hotkey.trim();
    if !hotkey.is_empty() && hotkey::parse(hotkey).is_none() {
        return Some(SettingsProblem {
            field: "openHotkey",
            message: format!(
                "This hotkey could not be read:\n\n{hotkey}\n\nUse Ctrl, Alt or Win (and Shift if you like) with a letter, a digit, F1 to F24 or Space, for example Alt+Shift+N or Ctrl+Alt+F12. Leave the box empty for no hotkey."
            ),
        });
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_settings_have_no_problem() {
        assert_eq!(first_problem(&AppSettings::default()), None);
    }

    #[test]
    fn an_unreadable_terminal_button_is_named() {
        let mut settings = AppSettings::default();
        settings.terminal_presets = vec!["Shell = shell".to_owned(), "no equals sign".to_owned()];
        let problem = first_problem(&settings);
        assert_eq!(problem.as_ref().map(|p| p.field), Some("terminalPresets"));
        assert!(problem.is_some_and(|p| p.message.starts_with("This terminal button could not be read:\n\nno equals sign\n\n")));
    }

    #[test]
    fn the_hotkey_is_tidied() {
        let mut settings = AppSettings::default();
        settings.open_hotkey = " win + shift+n ".to_owned();
        assert_eq!(validate_settings(settings).open_hotkey, "Shift+Win+N");
    }

    #[test]
    fn an_unreadable_hotkey_is_named_and_empty_is_fine() {
        let mut settings = AppSettings::default();
        settings.open_hotkey = "Shift+N".to_owned();
        assert_eq!(first_problem(&settings).map(|p| p.field), Some("openHotkey"));
        settings.open_hotkey = "  ".to_owned();
        assert_eq!(first_problem(&settings), None);
    }
}
