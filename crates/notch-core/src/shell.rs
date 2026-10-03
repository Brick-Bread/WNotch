//! How the island sits on its display, which keys open it, and when a fullscreen app hides it.
//!
//! Ports of `Hotkey.cs`, `IslandPlacement.cs` and `FullscreenRule.cs`. Everything here is plain
//! arithmetic and text; the windows and key registrations that use it live in the app.

use std::fmt;

use serde::Serialize;

use crate::settings::{NotchPosition, NotchStyle};

// Hotkey ---------------------------------------------------------------------------------------

/// The highest function key a hotkey may use.
pub const LAST_FUNCTION_KEY: u8 = 24;

/// The main key of a hotkey.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum HotkeyKey {
    /// `A` to `Z`, stored upper case.
    Letter(char),
    /// `0` to `9`.
    Digit(char),
    /// `F1` to `F24`.
    Function(u8),
    Space,
}

/// A key combination that works from any app, written the way it is typed in settings: `Alt+Shift+N`.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Hotkey {
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
    pub win: bool,
    pub key: HotkeyKey,
}

impl Hotkey {
    /// Reads modifiers and one key joined by `+`: a letter, a digit, F1 to F24 or Space, with at
    /// least one of Ctrl, Alt and Win, since Shift alone would take a key away from typing.
    pub fn parse(text: &str) -> Option<Self> {
        let parts: Vec<&str> = text.split('+').map(str::trim).collect();
        let (key, modifiers) = parts.split_last()?;
        let mut hotkey = Self { ctrl: false, alt: false, shift: false, win: false, key: HotkeyKey::parse(key)? };
        for modifier in modifiers {
            match modifier.to_lowercase().as_str() {
                "ctrl" | "control" => hotkey.ctrl = true,
                "alt" => hotkey.alt = true,
                "shift" => hotkey.shift = true,
                "win" | "windows" => hotkey.win = true,
                _ => return None,
            }
        }
        (hotkey.ctrl || hotkey.alt || hotkey.win).then_some(hotkey)
    }
}

impl HotkeyKey {
    fn parse(name: &str) -> Option<Self> {
        let upper = name.to_uppercase();
        let mut chars = upper.chars();
        match (chars.next(), chars.next()) {
            (Some(c @ 'A'..='Z'), None) => return Some(Self::Letter(c)),
            (Some(c @ '0'..='9'), None) => return Some(Self::Digit(c)),
            _ => {}
        }
        if upper == "SPACE" {
            return Some(Self::Space);
        }
        let number: u8 = upper.strip_prefix('F')?.parse().ok()?;
        (1..=LAST_FUNCTION_KEY).contains(&number).then_some(Self::Function(number))
    }
}

/// The tidy spelling: `Ctrl+Alt+Shift+Win` order, the key last.
impl fmt::Display for Hotkey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (held, name) in [(self.ctrl, "Ctrl"), (self.alt, "Alt"), (self.shift, "Shift"), (self.win, "Win")] {
            if held {
                write!(f, "{name}+")?;
            }
        }
        match self.key {
            HotkeyKey::Letter(c) | HotkeyKey::Digit(c) => write!(f, "{c}"),
            HotkeyKey::Function(n) => write!(f, "F{n}"),
            HotkeyKey::Space => f.write_str("Space"),
        }
    }
}

// Placement ------------------------------------------------------------------------------------

/// Used when the taskbar's height cannot be told, e.g. while it hides itself.
pub const DEFAULT_TASKBAR_HEIGHT: f64 = 48.0;
/// Height of the idle pill, which a floating island centres in the taskbar.
pub const PILL_HEIGHT: f64 = 32.0;
/// How far a floating island keeps from the top edge.
pub const TOP_GAP: f64 = 8.0;
/// How far the island keeps from the left edge when it sits in a corner.
pub const SIDE_INSET: f64 = 12.0;
/// Room around a collapsed or compact island for its glow to reach into.
pub const GLOW_PAD: f64 = 12.0;

const SMALLEST_BOTTOM_GAP: f64 = 4.0;

/// The corner or edge the island hangs from.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Anchor {
    /// Middle of the top edge; opens downwards.
    TopCenter,
    /// Top-left corner; opens downwards. What the taskbar position becomes where there is no taskbar to sit in.
    TopLeft,
    /// Bottom-left corner, over the taskbar; opens upwards.
    BottomLeft,
}

/// A window's place on a display, in logical pixels relative to the display's top-left corner.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct Frame {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

/// Where the island is on its display and how it meets the screen edge, worked out from the
/// user's position and style settings. Sizes are in logical pixels.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct IslandPlacement {
    pub anchor: Anchor,
    pub floating: bool,
    /// Height of the taskbar on the display; only matters at the bottom.
    pub taskbar_height: f64,
}

impl IslandPlacement {
    /// `has_taskbar` is whether a panel at the bottom of the display could be found (see
    /// [`taskbar_height`]); without one the taskbar position falls back to the top-left corner.
    pub fn new(position: NotchPosition, style: NotchStyle, taskbar_height: f64, has_taskbar: bool) -> Self {
        let anchor = match position {
            NotchPosition::TopCenter => Anchor::TopCenter,
            NotchPosition::TaskbarLeft if has_taskbar => Anchor::BottomLeft,
            NotchPosition::TaskbarLeft => Anchor::TopLeft,
        };
        Self { anchor, floating: style == NotchStyle::Island, taskbar_height }
    }

    /// The island sits on the bottom edge and opens upwards, rather than hanging from the top.
    pub fn at_bottom(&self) -> bool {
        self.anchor == Anchor::BottomLeft
    }

    /// Space between the screen edge and the island: none for a notch, enough to centre the pill in the taskbar there.
    pub fn edge_gap(&self) -> f64 {
        match (self.floating, self.at_bottom()) {
            (false, _) => 0.0,
            (true, true) => SMALLEST_BOTTOM_GAP.max((self.taskbar_height - PILL_HEIGHT) / 2.0),
            (true, false) => TOP_GAP,
        }
    }

    /// How far the island keeps from the display's left edge: nothing at the top centre.
    pub fn side_inset(&self) -> f64 {
        if self.anchor == Anchor::TopCenter {
            0.0
        } else {
            SIDE_INSET
        }
    }

    /// The window that holds an island of `island` size on a display of `display` size. Only the
    /// idle and compact pills get room for their glow; the open panel has none, as its halo
    /// would swallow clicks meant for the windows next to it.
    pub fn frame(&self, display: (f64, f64), island: (f64, f64), glow: bool) -> Frame {
        let pad = if glow { GLOW_PAD } else { 0.0 };
        let gap = self.edge_gap();
        let (island_width, island_height) = island;
        let height = gap + island_height + pad;
        match self.anchor {
            Anchor::TopCenter => {
                let width = island_width + 2.0 * pad;
                Frame { x: ((display.0 - width) / 2.0).round(), y: 0.0, width, height }
            }
            Anchor::TopLeft => Frame { x: 0.0, y: 0.0, width: SIDE_INSET + island_width + pad, height },
            Anchor::BottomLeft => {
                Frame { x: 0.0, y: display.1 - height, width: SIDE_INSET + island_width + pad, height }
            }
        }
    }
}

/// The height of the taskbar on a display, from the display and its work area (the display minus
/// the taskbar), both in physical pixels, at `scale` physical pixels per logical one. `None`
/// when no panel takes room from the bottom, as with an auto-hiding taskbar.
pub fn taskbar_height(display_bottom: i32, work_area_bottom: i32, scale: f64) -> Option<f64> {
    let taken = f64::from(display_bottom - work_area_bottom) / scale;
    (taken > 0.0).then_some(taken)
}

// Fullscreen ----------------------------------------------------------------------------------

/// An application window on the notch's display, as far as hiding for fullscreen apps is concerned.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct StackedWindow {
    /// It fills the whole display and has no title bar: a game, a fullscreen video, an F11 browser.
    pub covers_display: bool,
    /// It is an always-on-top window, which floats above whatever the user is really looking at.
    pub is_topmost: bool,
}

/// Whether a fullscreen app is what shows on the display, given its windows from the top of the
/// stack down. Small always-on-top windows are looked past, so an overlay or a pinned tool above
/// a game does not bring the notch back; an ordinary window on top means the user has put
/// something over the fullscreen app, and the notch may show.
pub fn is_covered(top_to_bottom: impl IntoIterator<Item = StackedWindow>) -> bool {
    for window in top_to_bottom {
        if window.covers_display {
            return true;
        }
        if !window.is_topmost {
            return false;
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(text: &str) -> Option<Hotkey> {
        Hotkey::parse(text)
    }

    #[test]
    fn hotkey_is_read_as_typed_and_written_tidily() {
        for (text, tidy) in [
            ("Win+Shift+N", "Shift+Win+N"),
            (" ctrl + alt + space ", "Ctrl+Alt+Space"),
            ("Control+7", "Ctrl+7"),
            ("windows+f12", "Win+F12"),
            ("Alt+Shift+N", "Alt+Shift+N"),
        ] {
            let hotkey = key(text).unwrap_or_else(|| panic!("{text:?} should parse"));
            assert_eq!(hotkey.to_string(), tidy);
            assert_eq!(key(tidy), Some(hotkey));
        }
    }

    #[test]
    fn hotkey_keys_are_decoded() {
        assert_eq!(key("Ctrl+n").map(|h| h.key), Some(HotkeyKey::Letter('N')));
        assert_eq!(key("Ctrl+F24").map(|h| h.key), Some(HotkeyKey::Function(24)));
        assert_eq!(key("Ctrl+SPACE").map(|h| h.key), Some(HotkeyKey::Space));
    }

    #[test]
    fn hotkey_that_cannot_be_used_is_refused() {
        for text in ["", "Ctrl+", "Ctrl+Alt", "Ctrl+Enter", "Ctrl+F25", "Ctrl+F0", "Hyper+N", "N", "Shift+N"] {
            assert_eq!(key(text), None, "{text:?} should be refused");
        }
    }

    fn placement(position: NotchPosition, style: NotchStyle, taskbar: f64) -> IslandPlacement {
        IslandPlacement::new(position, style, taskbar, true)
    }

    #[test]
    fn a_floating_island_keeps_clear_of_the_edge() {
        let island = placement(NotchPosition::TopCenter, NotchStyle::Island, 48.0);
        assert_eq!(island.edge_gap(), TOP_GAP);
        assert_eq!(placement(NotchPosition::TopCenter, NotchStyle::Notch, 48.0).edge_gap(), 0.0);
    }

    #[test]
    fn a_floating_island_is_centred_in_the_taskbar() {
        for (taskbar, gap) in [(48.0, 8.0), (72.0, 20.0), (30.0, 4.0)] {
            assert_eq!(placement(NotchPosition::TaskbarLeft, NotchStyle::Island, taskbar).edge_gap(), gap);
        }
    }

    #[test]
    fn without_a_taskbar_the_corner_is_at_the_top() {
        let corner = IslandPlacement::new(NotchPosition::TaskbarLeft, NotchStyle::Notch, 48.0, false);
        assert_eq!(corner.anchor, Anchor::TopLeft);
        assert!(!corner.at_bottom());
        assert_eq!(corner.side_inset(), SIDE_INSET);
    }

    #[test]
    fn the_top_window_is_centred_with_room_for_the_glow() {
        let frame = placement(NotchPosition::TopCenter, NotchStyle::Island, 48.0).frame((1920.0, 1080.0), (180.0, 32.0), true);
        assert_eq!(frame, Frame { x: 840.0, y: 0.0, width: 204.0, height: 52.0 });
    }

    #[test]
    fn the_open_panel_has_no_glow_room() {
        let frame = placement(NotchPosition::TopCenter, NotchStyle::Notch, 48.0).frame((1920.0, 1080.0), (600.0, 372.0), false);
        assert_eq!(frame, Frame { x: 660.0, y: 0.0, width: 600.0, height: 372.0 });
    }

    #[test]
    fn the_taskbar_window_sits_on_the_bottom_left_and_grows_upwards() {
        let island = placement(NotchPosition::TaskbarLeft, NotchStyle::Island, 48.0);
        let idle = island.frame((1920.0, 1080.0), (180.0, 32.0), true);
        assert_eq!(idle, Frame { x: 0.0, y: 1080.0 - 52.0, width: 204.0, height: 52.0 });
        let open = island.frame((1920.0, 1080.0), (600.0, 372.0), false);
        assert_eq!(open.y + open.height, 1080.0);
    }

    #[test]
    fn taskbar_height_comes_from_the_work_area() {
        assert_eq!(taskbar_height(1080, 1032, 1.0), Some(48.0));
        assert_eq!(taskbar_height(1440, 1368, 1.5), Some(48.0));
        assert_eq!(taskbar_height(1080, 1080, 1.0), None);
    }

    const FULLSCREEN: StackedWindow = StackedWindow { covers_display: true, is_topmost: false };
    const ORDINARY: StackedWindow = StackedWindow { covers_display: false, is_topmost: false };
    const PINNED: StackedWindow = StackedWindow { covers_display: false, is_topmost: true };

    #[test]
    fn a_fullscreen_app_on_top_covers_the_display() {
        assert!(is_covered([FULLSCREEN, ORDINARY]));
        assert!(is_covered([StackedWindow { covers_display: true, is_topmost: true }]));
    }

    #[test]
    fn always_on_top_windows_above_a_game_are_looked_past() {
        assert!(is_covered([PINNED, PINNED, FULLSCREEN]));
    }

    #[test]
    fn an_ordinary_window_over_a_fullscreen_app_uncovers_the_display() {
        assert!(!is_covered([ORDINARY, FULLSCREEN]));
        assert!(!is_covered([PINNED, ORDINARY, FULLSCREEN]));
    }

    #[test]
    fn a_display_without_a_fullscreen_app_is_not_covered() {
        assert!(!is_covered([]));
        assert!(!is_covered([PINNED]));
        assert!(!is_covered([ORDINARY, ORDINARY]));
    }
}
