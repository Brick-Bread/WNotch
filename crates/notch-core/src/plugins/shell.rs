//! The notch's state as plugins may read it: expanded or not, which page is showing, the theme.

use std::sync::{Mutex, MutexGuard};

use serde::Serialize;

use crate::glow::GlowColor;

/// What one plugin is told about the notch. Sent in `hello` and again as `shell` whenever it changes.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ShellView {
    /// True while the notch is expanded.
    pub expanded: bool,
    /// The id of this plugin's page the notch is showing, if it is expanded on one.
    pub page: Option<String>,
    /// True while the notch is expanded on the Plugins tab, where the plugin's cards are.
    pub cards_visible: bool,
    /// True when the notch is drawn dark.
    pub dark: bool,
    /// The accent colour the user chose, or none when they switched it off.
    pub accent: Option<GlowColor>,
}

#[derive(Debug, Clone, PartialEq)]
struct State {
    expanded: bool,
    page: Option<(String, String)>,
    cards: bool,
    dark: bool,
    accent: Option<GlowColor>,
}

/// The notch's state as the shell reports it. The shell writes; plugins read their [`ShellView`].
pub struct ShellState {
    state: Mutex<State>,
}

impl Default for ShellState {
    fn default() -> Self {
        Self { state: Mutex::new(State { expanded: false, page: None, cards: false, dark: true, accent: None }) }
    }
}

impl ShellState {
    pub fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Called by the shell. `page` is the plugin page the tab shows, if any. True when something changed.
    pub fn update(&self, expanded: bool, page: Option<(String, String)>, cards_tab: bool, dark: bool, accent: Option<GlowColor>) -> bool {
        let next = State { expanded, page, cards: cards_tab, dark, accent };
        let mut state = self.lock();
        if *state == next {
            return false;
        }
        *state = next;
        true
    }

    /// The state as one plugin sees it.
    pub fn view_for(&self, plugin_id: &str) -> ShellView {
        let state = self.lock();
        ShellView {
            expanded: state.expanded,
            page: state
                .page
                .as_ref()
                .filter(|(owner, _)| state.expanded && owner == plugin_id)
                .map(|(_, page)| page.clone()),
            cards_visible: state.expanded && state.cards,
            dark: state.dark,
            accent: state.accent,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_shell_reports_what_the_notch_is_showing() {
        let shell = ShellState::new();
        assert!(!shell.view_for("a").expanded);

        assert!(shell.update(true, Some(("a".into(), "main".into())), false, false, Some(GlowColor::BLUE)));
        assert!(!shell.update(true, Some(("a".into(), "main".into())), false, false, Some(GlowColor::BLUE)));

        let a = shell.view_for("a");
        assert!(a.expanded && !a.dark && !a.cards_visible);
        assert_eq!(a.page.as_deref(), Some("main"));
        assert_eq!(a.accent, Some(GlowColor::BLUE));
        // Another plugin's page is showing, not mine.
        assert_eq!(shell.view_for("b").page, None);

        assert!(shell.update(true, None, true, true, None));
        assert!(shell.view_for("b").cards_visible);

        // Collapsed: nothing is looking, whatever tab was last open.
        assert!(shell.update(false, Some(("a".into(), "main".into())), true, true, None));
        let collapsed = shell.view_for("a");
        assert!(!collapsed.expanded && !collapsed.cards_visible);
        assert_eq!(collapsed.page, None);
    }
}
