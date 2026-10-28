//! When the notch opens and closes: on hover after a short delay, on a click, on the hotkey,
//! and closing when the pointer has really left or the user clicked elsewhere. Ported from the
//! hover, pointer-watch and hotkey handling in `src/Notch.App/Shell/NotchWindow.xaml.cs` and
//! `NotchWindow.Keyboard.cs`.
//!
//! The C# window learns of the pointer from mouse events, a 100 ms watch while open, and a
//! timer for the delays. Here the app polls the pointer and calls [`Opening::poll`]; the delays
//! are deadlines the next poll acts on.

use std::time::{Duration, Instant};

pub const OPEN_DELAY: Duration = Duration::from_millis(120);
pub const CLOSE_DELAY: Duration = Duration::from_millis(350);

/// What the page reports holds the notch open or gives it the keyboard.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Holds {
    /// A file is being dragged out of the notch.
    pub drag_out: bool,
    /// A menu of the notch's own is showing (a shelf tile's, the terminal's folder menu).
    pub menu_open: bool,
    /// A Home widget is being moved.
    pub home_drag: bool,
    /// A Home sheet (add a widget, a widget's settings, a detail view) is open.
    pub sheet_open: bool,
    /// The notch has the keyboard and something in it is being typed into.
    pub keyboard_in_use: bool,
}

/// Where the pointer is, as last polled.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Pointer {
    /// On the island, or in the gap between a floating island and its edge.
    pub over: bool,
    /// The left, right or middle button is down, wherever the pointer is.
    pub button_down: bool,
}

/// What a call changed, for the app to act on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Change {
    /// The notch opened or closed.
    pub expanded_changed: bool,
    /// It closed while it may have had the keyboard: give it back to the window that had it.
    pub hand_back_keyboard: bool,
}

#[derive(Debug, Default)]
pub struct Opening {
    expanded: bool,
    pinned: bool,
    /// A pending open or close, and when it happens.
    pending: Option<(bool, Instant)>,
    was_over: bool,
    mouse_was_down: bool,
    /// A hotkey (or the palette, or a command) opened the notch, and holds it open until the
    /// pointer has been on it.
    hotkey_hold: bool,
}

impl Opening {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn is_expanded(&self) -> bool {
        self.expanded
    }

    /// When the next poll is due to act on a pending open or close.
    pub fn next_deadline(&self) -> Option<Instant> {
        self.pending.map(|(_, at)| at)
    }

    /// Keeps the notch open regardless of the pointer (`--pin-open`, for development).
    pub fn pin_open(&mut self, pointer: Pointer) -> Change {
        self.pinned = true;
        self.set_expanded(true, pointer)
    }

    fn hold_open(&self, holds: Holds) -> bool {
        holds.drag_out || holds.menu_open || self.hotkey_hold || holds.home_drag || holds.sheet_open
    }

    fn schedule(&mut self, expanded: bool, delay: Duration, holds: Holds, now: Instant) {
        if !expanded && self.hold_open(holds) {
            return;
        }
        self.pending = Some((expanded, now + delay));
    }

    fn set_expanded(&mut self, expanded: bool, pointer: Pointer) -> Change {
        let expanded = expanded || self.pinned;
        if self.expanded == expanded {
            return Change::default();
        }

        self.expanded = expanded;
        if expanded {
            self.mouse_was_down = pointer.button_down;
        } else {
            self.hotkey_hold = false;
        }
        Change { expanded_changed: true, hand_back_keyboard: false }
    }

    /// Takes in the polled pointer: opens on hover, closes when it has really left, and carries
    /// out a pending open or close whose time has come.
    pub fn poll(&mut self, pointer: Pointer, holds: Holds, expand_on_hover: bool, now: Instant) -> Change {
        // The pointer came onto the island, or left it.
        if pointer.over && !self.was_over {
            // Entering also cancels a pending close, which matters even when hover-to-open is off.
            if expand_on_hover || self.expanded {
                self.schedule(true, OPEN_DELAY, holds, now);
            }
        } else if !pointer.over && self.was_over {
            self.schedule(false, CLOSE_DELAY, holds, now);
        }
        self.was_over = pointer.over;

        if self.expanded {
            let change = self.watch(pointer, holds, now);
            if change.expanded_changed {
                return change;
            }
        }

        match self.pending {
            Some((wants, at)) if now >= at => {
                self.pending = None;
                if !wants && self.expanded && pointer.over {
                    return Change::default();
                }
                // The notch may have the keyboard (terminal, clipboard search, timer box); it goes
                // back to the window that had it, as when the hotkey closes the notch.
                let hand_back = !wants && self.expanded && holds.keyboard_in_use;
                let mut change = self.set_expanded(wants, pointer);
                change.hand_back_keyboard = hand_back && change.expanded_changed;
                change
            }
            _ => Change::default(),
        }
    }

    /// While the notch is open: closes it once the pointer has really left, and calls off a
    /// close when the pointer is back.
    fn watch(&mut self, pointer: Pointer, holds: Holds, now: Instant) -> Change {
        // A press, not a held button, so a button already down when the notch opened does not count.
        let pressed = pointer.button_down && !self.mouse_was_down;
        self.mouse_was_down = pointer.button_down;

        let close_pending = matches!(self.pending, Some((false, _)));
        if !pointer.over && pressed && !self.pinned && (self.hotkey_hold || holds.keyboard_in_use || holds.sheet_open) {
            // The notch never saw this click: it was opened without the keyboard, or the window
            // clicked does not take focus.
            return self.close_for_click_away(pointer, holds);
        }

        if pointer.over {
            // From here on the pointer is in charge again: leaving closes the notch as usual.
            self.hotkey_hold = false;
            if close_pending {
                self.pending = None;
            }
        } else if !close_pending && !self.pinned && !self.hold_open(holds) {
            self.schedule(false, CLOSE_DELAY, holds, now);
        }
        Change::default()
    }

    /// A click on the island opens it, even with hover-to-open off.
    pub fn island_clicked(&mut self, pointer: Pointer) -> Change {
        self.pending = None;
        self.set_expanded(true, pointer)
    }

    /// The open hotkey: opens and holds the notch open, or closes it and gives the keyboard back.
    pub fn hotkey(&mut self, pointer: Pointer, holds: Holds) -> Change {
        self.pending = None;
        if !self.expanded {
            self.hotkey_hold = true;
            return self.set_expanded(true, pointer);
        }
        let mut change = self.set_expanded(false, pointer);
        change.hand_back_keyboard = change.expanded_changed && holds.keyboard_in_use;
        change
    }

    /// Opens the notch and holds it open until the pointer has been on it, as the hotkey does:
    /// for the palette and for commands that open a tab or a page.
    pub fn open_and_hold(&mut self, pointer: Pointer) -> Change {
        self.pending = None;
        self.hotkey_hold = true;
        self.set_expanded(true, pointer)
    }

    /// Closes the notch because the user clicked another window (the notch lost the keyboard),
    /// unless a menu or drag of its own is what they are using.
    pub fn close_for_click_away(&mut self, pointer: Pointer, holds: Holds) -> Change {
        if pointer.over || holds.drag_out || holds.menu_open {
            return Change::default();
        }
        // A pending open from a pass over the island must not bring it back.
        self.pending = None;
        self.hotkey_hold = false;
        self.set_expanded(false, pointer)
    }

    /// Closes at once, as when a fullscreen app hides the notch.
    pub fn close(&mut self, pointer: Pointer) -> Change {
        self.pending = None;
        self.set_expanded(false, pointer)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ON: Pointer = Pointer { over: true, button_down: false };
    const OFF: Pointer = Pointer { over: false, button_down: false };
    const NONE: Holds =
        Holds { drag_out: false, menu_open: false, home_drag: false, sheet_open: false, keyboard_in_use: false };

    fn ms(start: Instant, millis: u64) -> Instant {
        start + Duration::from_millis(millis)
    }

    #[test]
    fn hovering_opens_after_the_delay_and_leaving_closes_after_its_own() {
        let mut opening = Opening::new();
        let t = Instant::now();
        assert_eq!(opening.poll(ON, NONE, true, t), Change::default());
        assert!(!opening.poll(ON, NONE, true, ms(t, 100)).expanded_changed);
        assert!(opening.poll(ON, NONE, true, ms(t, 120)).expanded_changed);
        assert!(opening.is_expanded());

        opening.poll(OFF, NONE, true, ms(t, 500));
        assert!(!opening.poll(OFF, NONE, true, ms(t, 800)).expanded_changed);
        assert!(opening.poll(OFF, NONE, true, ms(t, 850)).expanded_changed);
        assert!(!opening.is_expanded());
    }

    #[test]
    fn hover_does_not_open_when_switched_off_but_a_click_does() {
        let mut opening = Opening::new();
        let t = Instant::now();
        opening.poll(ON, NONE, false, t);
        assert!(!opening.poll(ON, NONE, false, ms(t, 500)).expanded_changed);
        assert!(opening.island_clicked(ON).expanded_changed);
    }

    #[test]
    fn coming_back_calls_off_a_close() {
        let mut opening = Opening::new();
        let t = Instant::now();
        opening.island_clicked(ON);
        opening.poll(ON, NONE, true, t);
        opening.poll(OFF, NONE, true, ms(t, 100));
        opening.poll(ON, NONE, true, ms(t, 200));
        assert!(!opening.poll(ON, NONE, true, ms(t, 1000)).expanded_changed);
        assert!(opening.is_expanded());
    }

    #[test]
    fn a_hotkey_holds_it_open_until_the_pointer_has_been_on_it() {
        let mut opening = Opening::new();
        let t = Instant::now();
        assert!(opening.hotkey(OFF, NONE).expanded_changed);
        assert!(!opening.poll(OFF, NONE, true, ms(t, 2000)).expanded_changed);

        opening.poll(ON, NONE, true, ms(t, 2100));
        opening.poll(OFF, NONE, true, ms(t, 2200));
        assert!(opening.poll(OFF, NONE, true, ms(t, 2600)).expanded_changed);
    }

    #[test]
    fn a_click_elsewhere_closes_it_when_the_hotkey_opened_it() {
        let mut opening = Opening::new();
        let t = Instant::now();
        opening.hotkey(OFF, NONE);
        let pressed = Pointer { over: false, button_down: true };
        assert!(opening.poll(pressed, NONE, true, ms(t, 100)).expanded_changed);
        assert!(!opening.is_expanded());
    }

    #[test]
    fn a_button_held_when_it_opened_is_not_a_click() {
        let mut opening = Opening::new();
        let t = Instant::now();
        let held = Pointer { over: false, button_down: true };
        opening.hotkey(held, NONE);
        assert!(!opening.poll(held, NONE, true, ms(t, 100)).expanded_changed);
    }

    #[test]
    fn a_menu_or_drag_holds_it_open() {
        let mut opening = Opening::new();
        let t = Instant::now();
        opening.island_clicked(ON);
        opening.poll(ON, NONE, true, t);
        let menu = Holds { menu_open: true, ..NONE };
        opening.poll(OFF, menu, true, ms(t, 100));
        assert!(!opening.poll(OFF, menu, true, ms(t, 1000)).expanded_changed);
        assert!(!opening.close_for_click_away(OFF, menu).expanded_changed);
    }

    #[test]
    fn closing_with_the_keyboard_in_use_hands_it_back() {
        let mut opening = Opening::new();
        let typing = Holds { keyboard_in_use: true, ..NONE };
        opening.island_clicked(ON);
        let change = opening.hotkey(ON, typing);
        assert!(change.expanded_changed && change.hand_back_keyboard);
    }

    #[test]
    fn pinned_open_never_closes() {
        let mut opening = Opening::new();
        let t = Instant::now();
        opening.pin_open(OFF);
        opening.poll(OFF, NONE, true, t);
        assert!(!opening.poll(OFF, NONE, true, ms(t, 5000)).expanded_changed);
        assert!(!opening.close(OFF).expanded_changed);
        assert!(opening.is_expanded());
    }
}
