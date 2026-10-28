//! Ported from `src/Notch.Core/Shell/Hotkey.cs`.

use std::fmt;
use std::ops::BitOr;

/// The keys held with a hotkey's main key. The values are the ones Windows uses when registering one.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct HotkeyModifiers(pub u32);

impl HotkeyModifiers {
    pub const NONE: Self = Self(0);
    pub const ALT: Self = Self(1);
    pub const CTRL: Self = Self(2);
    pub const SHIFT: Self = Self(4);
    pub const WIN: Self = Self(8);

    pub fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }
}

impl BitOr for HotkeyModifiers {
    type Output = Self;

    fn bitor(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }
}

/// A key combination that works from any app, written the way it is typed in settings: `Alt+Shift+N`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Hotkey {
    pub modifiers: HotkeyModifiers,
    /// The Windows virtual-key code of the main key.
    pub virtual_key: i32,
}

const SPACE: i32 = 0x20;
const F1: i32 = 0x70;
const LAST_FUNCTION_KEY: i32 = 24;

impl Hotkey {
    pub fn new(modifiers: HotkeyModifiers, virtual_key: i32) -> Self {
        Self { modifiers, virtual_key }
    }

    /// Reads modifiers and one key joined by "+": a letter, a digit, F1 to F24 or Space, with at
    /// least one of Ctrl, Alt and Win, since Shift alone would take a key away from typing.
    pub fn parse(text: Option<&str>) -> Option<Self> {
        let text = text.filter(|text| !text.trim().is_empty())?;

        let parts: Vec<&str> = text.split('+').map(str::trim).collect();
        let (key, modifier_parts) = parts.split_last()?;
        let mut modifiers = HotkeyModifiers::NONE;
        for part in modifier_parts {
            modifiers = modifiers
                | match part.to_lowercase().as_str() {
                    "ctrl" | "control" => HotkeyModifiers::CTRL,
                    "alt" => HotkeyModifiers::ALT,
                    "shift" => HotkeyModifiers::SHIFT,
                    "win" | "windows" => HotkeyModifiers::WIN,
                    _ => return None,
                };
        }

        if modifiers.0 & !HotkeyModifiers::SHIFT.0 == 0 {
            return None;
        }

        Some(Self::new(modifiers, key_code(key)?))
    }
}

/// The way [`Hotkey::parse`] reads it, tidied: `Ctrl+Alt+Shift+Win` order, the key last.
impl fmt::Display for Hotkey {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        let names = [
            (HotkeyModifiers::CTRL, "Ctrl"),
            (HotkeyModifiers::ALT, "Alt"),
            (HotkeyModifiers::SHIFT, "Shift"),
            (HotkeyModifiers::WIN, "Win"),
        ];
        for (modifier, name) in names {
            if self.modifiers.contains(modifier) {
                write!(f, "{name}+")?;
            }
        }

        match self.virtual_key {
            SPACE => f.write_str("Space"),
            key if (F1..F1 + LAST_FUNCTION_KEY).contains(&key) => write!(f, "F{}", key - F1 + 1),
            key => {
                write!(f, "{}", u32::try_from(key).ok().and_then(char::from_u32).unwrap_or(char::REPLACEMENT_CHARACTER))
            }
        }
    }
}

fn key_code(name: &str) -> Option<i32> {
    // ToUpperInvariant maps one character to one character.
    let key: String = name.chars().map(upper).collect();
    let mut chars = key.chars();
    if let (Some(c @ ('A'..='Z' | '0'..='9')), None) = (chars.next(), chars.next()) {
        // Letters and digits have their character's code.
        return Some(c as i32);
    }

    if key == "SPACE" {
        return Some(SPACE);
    }

    key.strip_prefix('F')
        .and_then(parse_integer)
        .filter(|number| (1..=LAST_FUNCTION_KEY).contains(number))
        .map(|number| F1 + number - 1)
}

/// A character's simple uppercase mapping, as `char.ToUpperInvariant` gives it.
fn upper(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

/// `int.TryParse` with the invariant culture: ASCII digits with an optional sign, and white space
/// around them.
pub(crate) fn parse_integer(text: &str) -> Option<i32> {
    let text = text.trim_matches(|c: char| matches!(c, '\t'..='\r' | ' '));
    let (negative, digits) = match text.as_bytes().first() {
        Some(b'-') => (true, &text[1..]),
        Some(b'+') => (false, &text[1..]),
        _ => (false, text),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let significant = digits.trim_start_matches('0');
    if significant.len() > 10 {
        return None;
    }
    let magnitude: i64 = if significant.is_empty() { 0 } else { significant.parse().ok()? };
    i32::try_from(if negative { -magnitude } else { magnitude }).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hotkey_is_read_as_typed_and_written_tidily() {
        for (text, modifiers, key, tidy) in [
            ("Win+Shift+N", HotkeyModifiers::WIN | HotkeyModifiers::SHIFT, 0x4E, "Shift+Win+N"),
            (" ctrl + alt + space ", HotkeyModifiers::CTRL | HotkeyModifiers::ALT, 0x20, "Ctrl+Alt+Space"),
            ("Control+7", HotkeyModifiers::CTRL, 0x37, "Ctrl+7"),
            ("windows+f12", HotkeyModifiers::WIN, 0x7B, "Win+F12"),
        ] {
            let hotkey = Hotkey::parse(Some(text)).unwrap();

            assert_eq!(hotkey, Hotkey::new(modifiers, key));
            assert_eq!(hotkey.to_string(), tidy);
        }
    }

    // Nothing, no key, a key Notch does not offer, an unknown modifier, and keys that would be taken away from typing.
    #[test]
    fn hotkey_that_cannot_be_used_is_refused() {
        for text in [
            None,
            Some(""),
            Some("Ctrl+"),
            Some("Ctrl+Alt"),
            Some("Ctrl+Enter"),
            Some("Ctrl+F25"),
            Some("Hyper+N"),
            Some("N"),
            Some("Shift+N"),
        ] {
            assert_eq!(Hotkey::parse(text), None, "{text:?}");
        }
    }

    #[test]
    fn function_keys_are_read_as_int_try_parse_reads_numbers() {
        assert_eq!(Hotkey::parse(Some("Alt+F01")).map(|h| h.to_string()).as_deref(), Some("Alt+F1"));
        assert_eq!(Hotkey::parse(Some("Alt+F+1")), None);
        assert_eq!(Hotkey::parse(Some("Alt+F-1")), None);
        assert_eq!(parse_integer(" +24 "), Some(24));
        assert_eq!(parse_integer("99999999999"), None);
        assert_eq!(parse_integer("٣"), None);
    }
}
