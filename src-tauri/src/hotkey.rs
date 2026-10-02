//! The global hotkey that opens and closes the notch from any app.

use std::str::FromStr;
use std::sync::Mutex;

use tauri::{AppHandle, Manager};
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};

use crate::state::Backend;

/// The highest function key the settings accept.
const LAST_FUNCTION_KEY: u32 = 24;

/// Reads a hotkey written the way it is typed in settings, `Alt+Shift+N`: modifiers and one key
/// joined by `+`. The key is a letter, a digit, F1 to F24 or Space, and at least one of Ctrl,
/// Alt and Win is needed since Shift alone would take a key away from typing.
pub fn parse(text: &str) -> Result<Shortcut, String> {
    let mut parts: Vec<&str> = text.split('+').map(str::trim).collect();
    let key = parts.pop().filter(|k| !k.is_empty()).ok_or("no key")?;

    let mut modifiers = Modifiers::empty();
    for part in parts {
        modifiers |= match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => Modifiers::CONTROL,
            "alt" => Modifiers::ALT,
            "shift" => Modifiers::SHIFT,
            "win" | "windows" => Modifiers::SUPER,
            _ => return Err(format!("unknown modifier '{part}'")),
        };
    }

    if modifiers.difference(Modifiers::SHIFT).is_empty() {
        return Err("needs at least one of Ctrl, Alt and Win".to_owned());
    }

    let code = key_code(key).ok_or_else(|| format!("unsupported key '{key}'"))?;
    Ok(Shortcut::new(Some(modifiers), code))
}

fn key_code(name: &str) -> Option<Code> {
    let key = name.to_ascii_uppercase();
    let mut chars = key.chars();
    let name = match (chars.next(), chars.next()) {
        (Some(c @ 'A'..='Z'), None) => format!("Key{c}"),
        (Some(c @ '0'..='9'), None) => format!("Digit{c}"),
        _ if key == "SPACE" => "Space".to_owned(),
        _ => {
            let number: u32 = key.strip_prefix('F')?.parse().ok()?;
            if !(1..=LAST_FUNCTION_KEY).contains(&number) {
                return None;
            }
            format!("F{number}")
        }
    };
    Code::from_str(&name).ok()
}

/// Makes `text` the open hotkey, replacing the one registered before. Empty text means none;
/// text that cannot be read or registered is logged and leaves the notch without a hotkey.
pub fn apply(app: &AppHandle, registered: &Mutex<Option<Shortcut>>, text: &str) {
    let shortcuts = app.global_shortcut();
    let mut current = registered.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(previous) = current.take() {
        if let Err(e) = shortcuts.unregister(previous) {
            log::warn!("could not release the previous hotkey: {e}");
        }
    }

    if text.trim().is_empty() {
        return;
    }

    let shortcut = match parse(text) {
        Ok(shortcut) => shortcut,
        Err(problem) => {
            log::warn!("hotkey '{text}' ignored: {problem}");
            return;
        }
    };

    let registration = shortcuts.on_shortcut(shortcut, |app, _, event| {
        if event.state() == ShortcutState::Pressed {
            app.state::<Backend>().window.toggle();
        }
    });
    match registration {
        Ok(()) => *current = Some(shortcut),
        Err(e) => log::warn!("hotkey '{text}' could not be registered: {e}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_default() {
        let expected = Shortcut::new(Some(Modifiers::ALT | Modifiers::SHIFT), Code::KeyN);
        assert_eq!(parse("Alt+Shift+N"), Ok(expected));
    }

    #[test]
    fn is_case_and_space_tolerant() {
        let expected = Shortcut::new(Some(Modifiers::CONTROL | Modifiers::SUPER), Code::F12);
        assert_eq!(parse(" control + WIN + f12 "), Ok(expected));
    }

    #[test]
    fn accepts_digits_and_space() {
        assert_eq!(parse("Ctrl+5"), Ok(Shortcut::new(Some(Modifiers::CONTROL), Code::Digit5)));
        assert_eq!(parse("Alt+Space"), Ok(Shortcut::new(Some(Modifiers::ALT), Code::Space)));
    }

    #[test]
    fn rejects_unusable_text() {
        for text in ["", "N", "Shift+N", "Alt+", "Alt+Shift", "Alt+F25", "Alt+F0", "Foo+N", "Alt+Enter"] {
            assert!(parse(text).is_err(), "{text:?} should not parse");
        }
    }
}
