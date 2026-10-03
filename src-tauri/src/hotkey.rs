//! The global hotkey that opens and closes the notch from any app.

use std::str::FromStr;
use std::sync::Mutex;

use notch_core::shell::{Hotkey, HotkeyKey};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager};
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};

use crate::state::Backend;

/// What is wrong with the hotkey in the settings, for telling the user; `None` when it works or none is wanted.
#[derive(Default)]
pub struct HotkeyStatus(Mutex<Option<String>>);

impl HotkeyStatus {
    pub fn problem(&self) -> Option<String> {
        self.0.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    fn set(&self, problem: Option<String>) {
        *self.0.lock().unwrap_or_else(|e| e.into_inner()) = problem;
    }
}

/// Payload of the `hotkey-status` event.
#[derive(Serialize, Clone)]
pub struct HotkeyStatusChanged {
    problem: Option<String>,
}

/// Reads a hotkey written the way it is typed in settings, `Alt+Shift+N`; see [`Hotkey::parse`].
pub fn parse(text: &str) -> Option<Shortcut> {
    Hotkey::parse(text).map(|hotkey| to_shortcut(&hotkey))
}

fn to_shortcut(hotkey: &Hotkey) -> Shortcut {
    let mut modifiers = Modifiers::empty();
    for (held, modifier) in [
        (hotkey.ctrl, Modifiers::CONTROL),
        (hotkey.alt, Modifiers::ALT),
        (hotkey.shift, Modifiers::SHIFT),
        (hotkey.win, Modifiers::SUPER),
    ] {
        if held {
            modifiers |= modifier;
        }
    }
    let name = match hotkey.key {
        HotkeyKey::Letter(c) => format!("Key{c}"),
        HotkeyKey::Digit(c) => format!("Digit{c}"),
        HotkeyKey::Function(n) => format!("F{n}"),
        HotkeyKey::Space => "Space".to_owned(),
    };
    // The names above are all codes the plugin knows; Space is the fallback that cannot be reached.
    Shortcut::new(Some(modifiers), Code::from_str(&name).unwrap_or(Code::Space))
}

/// The sentence the settings and the post-save notice show for a hotkey that cannot be used.
pub fn unreadable(text: &str) -> String {
    format!("\"{text}\" is not a hotkey Notch can use.")
}

/// The sentence for a hotkey the system would not hand over.
fn taken(hotkey: &Hotkey) -> String {
    let by = if cfg!(windows) { "Windows or another app" } else { "the system or another app" };
    format!("{hotkey} is already used by {by}.")
}

/// Makes `text` the open hotkey, replacing the one registered before. Empty text means none;
/// text that cannot be read or registered leaves the notch without a hotkey and is recorded in
/// [`HotkeyStatus`] and sent as the `hotkey-status` event, so the settings can say so.
pub fn apply(app: &AppHandle, registered: &Mutex<Option<Shortcut>>, text: &str) {
    let problem = register(app, registered, text.trim());
    if let Some(problem) = &problem {
        log::warn!("{problem}");
    }
    app.state::<HotkeyStatus>().set(problem.clone());
    if let Err(e) = app.emit("hotkey-status", HotkeyStatusChanged { problem }) {
        log::warn!("could not emit hotkey-status: {e}");
    }
}

fn register(app: &AppHandle, registered: &Mutex<Option<Shortcut>>, text: &str) -> Option<String> {
    let shortcuts = app.global_shortcut();
    let mut current = registered.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(previous) = current.take() {
        if let Err(e) = shortcuts.unregister(previous) {
            log::warn!("could not release the previous hotkey: {e}");
        }
    }

    if text.is_empty() {
        return None;
    }
    let Some(hotkey) = Hotkey::parse(text) else {
        return Some(unreadable(text));
    };

    let shortcut = to_shortcut(&hotkey);
    let registration = shortcuts.on_shortcut(shortcut, |app, _, event| {
        if event.state() == ShortcutState::Pressed {
            app.state::<Backend>().window.toggle();
        }
    });
    match registration {
        Ok(()) => {
            *current = Some(shortcut);
            log::debug!("hotkey {hotkey} registered");
            None
        }
        Err(e) => {
            log::debug!("hotkey {hotkey} refused: {e}");
            Some(taken(&hotkey))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_default() {
        let expected = Shortcut::new(Some(Modifiers::ALT | Modifiers::SHIFT), Code::KeyN);
        assert_eq!(parse("Alt+Shift+N"), Some(expected));
    }

    #[test]
    fn is_case_and_space_tolerant() {
        let expected = Shortcut::new(Some(Modifiers::CONTROL | Modifiers::SUPER), Code::F12);
        assert_eq!(parse(" control + WIN + f12 "), Some(expected));
    }

    #[test]
    fn accepts_digits_and_space() {
        assert_eq!(parse("Ctrl+5"), Some(Shortcut::new(Some(Modifiers::CONTROL), Code::Digit5)));
        assert_eq!(parse("Alt+Space"), Some(Shortcut::new(Some(Modifiers::ALT), Code::Space)));
    }

    #[test]
    fn every_function_key_has_a_code() {
        for number in 1..=24 {
            let expected = Code::from_str(&format!("F{number}")).ok();
            assert_eq!(parse(&format!("Ctrl+F{number}")), expected.map(|c| Shortcut::new(Some(Modifiers::CONTROL), c)));
        }
    }

    #[test]
    fn rejects_unusable_text() {
        for text in ["", "N", "Shift+N", "Alt+", "Alt+Shift", "Alt+F25", "Alt+F0", "Foo+N", "Alt+Enter"] {
            assert!(parse(text).is_none(), "{text:?} should not parse");
        }
    }

    #[test]
    fn the_notices_name_the_hotkey() {
        assert_eq!(unreadable("Foo"), "\"Foo\" is not a hotkey Notch can use.");
        let hotkey = Hotkey::parse("alt+shift+n").map(|h| taken(&h));
        assert!(hotkey.is_some_and(|text| text.starts_with("Alt+Shift+N is already used by ")));
    }
}
