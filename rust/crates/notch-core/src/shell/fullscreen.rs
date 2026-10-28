//! Ported from `src/Notch.Core/Shell/FullscreenRule.cs`.

/// An application window on the notch's display, as far as hiding for fullscreen apps is concerned.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StackedWindow {
    /// It fills the whole display and has no title bar: a game, a fullscreen video, an F11 browser.
    pub covers_display: bool,
    /// It is an always-on-top window, which floats above whatever the user is really looking at.
    pub is_topmost: bool,
}

/// Whether a fullscreen app is what shows on the display, given its windows from the top of the
/// stack down. Small always-on-top windows are looked past, so an overlay or a pinned tool above a
/// game does not bring the notch back; an ordinary window on top means the user has put something
/// over the fullscreen app, and the notch may show.
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
