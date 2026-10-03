//! What plugins have put on the Plugins tab (cards) and on tabs of their own (pages), with the
//! console lines of each page. Plugins write through their host; the shell reads the snapshots.

use std::collections::HashMap;
use std::sync::{Mutex, MutexGuard};

use serde::Serialize;

use super::blocks::{Card, Page};

/// Console lines kept per page; older ones are dropped.
pub const MAX_CONSOLE_LINES: usize = 500;

/// A card together with the plugin that owns it.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CardEntry {
    pub plugin_id: String,
    #[serde(flatten)]
    pub card: Card,
}

/// Holds the cards of all plugins.
#[derive(Default)]
pub struct CardBoard {
    state: Mutex<CardState>,
}

#[derive(Default)]
struct CardState {
    entries: HashMap<(String, String), (CardEntry, u64)>,
    sequence: u64,
}

impl CardBoard {
    pub fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> MutexGuard<'_, CardState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Adds a card, or replaces the plugin's card with the same id in place, so a card that
    /// refreshes often does not jump around.
    pub fn set(&self, plugin_id: &str, card: Card) {
        let mut state = self.lock();
        let key = (plugin_id.to_owned(), card.id.clone());
        let sequence = match state.entries.get(&key) {
            Some((_, sequence)) => *sequence,
            None => {
                state.sequence += 1;
                state.sequence
            }
        };
        state.entries.insert(key, (CardEntry { plugin_id: plugin_id.to_owned(), card }, sequence));
    }

    /// Removes a card; false when there was no such card.
    pub fn remove(&self, plugin_id: &str, card_id: &str) -> bool {
        self.lock().entries.remove(&(plugin_id.to_owned(), card_id.to_owned())).is_some()
    }

    /// Removes all of a plugin's cards; true when there were any.
    pub fn remove_all(&self, plugin_id: &str) -> bool {
        let mut state = self.lock();
        let before = state.entries.len();
        state.entries.retain(|(owner, _), _| owner != plugin_id);
        state.entries.len() != before
    }

    /// In the order the cards were first added.
    pub fn snapshot(&self) -> Vec<CardEntry> {
        let state = self.lock();
        let mut all: Vec<&(CardEntry, u64)> = state.entries.values().collect();
        all.sort_by_key(|(_, sequence)| *sequence);
        all.into_iter().map(|(entry, _)| entry.clone()).collect()
    }

    /// Whether the plugin has a card with this id that reacts to clicks.
    pub fn is_clickable(&self, plugin_id: &str, card_id: &str) -> bool {
        self.lock()
            .entries
            .get(&(plugin_id.to_owned(), card_id.to_owned()))
            .is_some_and(|(entry, _)| entry.card.clickable)
    }
}

/// A page together with the plugin that owns it.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PageEntry {
    pub plugin_id: String,
    #[serde(flatten)]
    pub page: Page,
}

/// What changed on a [`PageBoard`], so the shell can do the least it has to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PageChange {
    /// A page was added, replaced or removed.
    Pages,
    /// A line was added to a console.
    Console(String),
    /// A console was emptied.
    ConsoleCleared,
    /// The plugin asked for a page to be shown.
    OpenRequested,
}

struct PageSlot {
    entry: PageEntry,
    console: Vec<String>,
    sequence: u64,
}

#[derive(Default)]
struct PageState {
    slots: HashMap<(String, String), PageSlot>,
    sequence: u64,
}

/// Holds the pages of all plugins and their console lines.
#[derive(Default)]
pub struct PageBoard {
    state: Mutex<PageState>,
}

impl PageBoard {
    pub fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> MutexGuard<'_, PageState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Adds a page, or replaces the plugin's page with the same id. The console's lines are
    /// kept when a page is replaced.
    pub fn set(&self, plugin_id: &str, page: Page) {
        let mut state = self.lock();
        let key = (plugin_id.to_owned(), page.id.clone());
        let entry = PageEntry { plugin_id: plugin_id.to_owned(), page };
        if let Some(slot) = state.slots.get_mut(&key) {
            slot.entry = entry;
            return;
        }
        state.sequence += 1;
        let sequence = state.sequence;
        state.slots.insert(key, PageSlot { entry, console: Vec::new(), sequence });
    }

    /// Appends a line to a page's console. `None` when the page does not exist.
    pub fn append(&self, plugin_id: &str, page_id: &str, line: &str) -> Option<PageChange> {
        let mut state = self.lock();
        let slot = state.slots.get_mut(&(plugin_id.to_owned(), page_id.to_owned()))?;
        slot.console.push(line.to_owned());
        if slot.console.len() > MAX_CONSOLE_LINES {
            let excess = slot.console.len() - MAX_CONSOLE_LINES;
            slot.console.drain(..excess);
        }
        Some(PageChange::Console(line.to_owned()))
    }

    /// Empties a page's console. `None` when the page does not exist.
    pub fn clear_console(&self, plugin_id: &str, page_id: &str) -> Option<PageChange> {
        let mut state = self.lock();
        let slot = state.slots.get_mut(&(plugin_id.to_owned(), page_id.to_owned()))?;
        slot.console.clear();
        Some(PageChange::ConsoleCleared)
    }

    /// Whether the plugin has such a page, for requests to show it.
    pub fn exists(&self, plugin_id: &str, page_id: &str) -> bool {
        self.lock().slots.contains_key(&(plugin_id.to_owned(), page_id.to_owned()))
    }

    /// Removes a page; false when there was no such page.
    pub fn remove(&self, plugin_id: &str, page_id: &str) -> bool {
        self.lock().slots.remove(&(plugin_id.to_owned(), page_id.to_owned())).is_some()
    }

    /// Removes all of a plugin's pages; true when there were any.
    pub fn remove_all(&self, plugin_id: &str) -> bool {
        let mut state = self.lock();
        let before = state.slots.len();
        state.slots.retain(|(owner, _), _| owner != plugin_id);
        state.slots.len() != before
    }

    /// In the order the pages were first added.
    pub fn snapshot(&self) -> Vec<PageEntry> {
        let state = self.lock();
        let mut all: Vec<&PageSlot> = state.slots.values().collect();
        all.sort_by_key(|slot| slot.sequence);
        all.into_iter().map(|slot| slot.entry.clone()).collect()
    }

    /// One page, as it was last sent.
    pub fn page(&self, plugin_id: &str, page_id: &str) -> Option<Page> {
        self.lock().slots.get(&(plugin_id.to_owned(), page_id.to_owned())).map(|slot| slot.entry.page.clone())
    }

    /// The page's console lines, oldest first; empty when there is no such page.
    pub fn console_of(&self, plugin_id: &str, page_id: &str) -> Vec<String> {
        self.lock()
            .slots
            .get(&(plugin_id.to_owned(), page_id.to_owned()))
            .map(|slot| slot.console.clone())
            .unwrap_or_default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn card(id: &str, label: &str) -> Card {
        serde_json::from_value(json!({ "id": id, "label": label })).unwrap()
    }

    fn page(id: &str, title: &str) -> Page {
        serde_json::from_value(json!({ "id": id, "title": title })).unwrap()
    }

    #[test]
    fn updating_a_card_keeps_its_place() {
        let board = CardBoard::new();
        board.set("a", card("one", "One"));
        board.set("b", card("one", "Other plugin, same id"));
        board.set("a", card("one", "Updated"));

        let labels: Vec<String> = board.snapshot().into_iter().map(|e| e.card.label).collect();
        assert_eq!(labels, ["Updated", "Other plugin, same id"]);
    }

    #[test]
    fn remove_all_only_touches_one_plugin() {
        let board = CardBoard::new();
        board.set("a", card("one", "A1"));
        board.set("a", card("two", "A2"));
        board.set("b", card("one", "B1"));

        assert!(board.remove_all("a"));
        assert!(!board.remove_all("a"));

        let labels: Vec<String> = board.snapshot().into_iter().map(|e| e.card.label).collect();
        assert_eq!(labels, ["B1"]);
        assert!(!board.remove("a", "one"));
        assert!(board.remove("b", "one"));
    }

    #[test]
    fn a_card_is_clickable_when_it_says_so() {
        let board = CardBoard::new();
        let mut clickable = card("c", "C");
        clickable.clickable = true;
        board.set("a", clickable);
        board.set("a", card("d", "D"));
        assert!(board.is_clickable("a", "c"));
        assert!(!board.is_clickable("a", "d"));
        assert!(!board.is_clickable("a", "missing"));
    }

    #[test]
    fn console_lines_are_kept_in_order_and_survive_replacing_the_page() {
        let board = PageBoard::new();
        board.set("p", page("main", "Main"));
        board.append("p", "main", "first");
        board.append("p", "main", "second");

        board.set("p", page("main", "Renamed"));

        assert_eq!(board.console_of("p", "main"), ["first", "second"]);
        assert_eq!(board.snapshot()[0].page.title, "Renamed");

        assert_eq!(board.clear_console("p", "main"), Some(PageChange::ConsoleCleared));
        assert!(board.console_of("p", "main").is_empty());
    }

    #[test]
    fn only_the_newest_lines_are_kept() {
        let board = PageBoard::new();
        board.set("p", page("main", "Main"));
        for i in 0..MAX_CONSOLE_LINES + 25 {
            board.append("p", "main", &format!("line {i}"));
        }

        let lines = board.console_of("p", "main");
        assert_eq!(lines.len(), MAX_CONSOLE_LINES);
        assert_eq!(lines[0], "line 25");
        assert_eq!(lines.last().map(String::as_str), Some(format!("line {}", MAX_CONSOLE_LINES + 24).as_str()));
    }

    #[test]
    fn appending_to_a_missing_page_is_ignored() {
        let board = PageBoard::new();
        assert_eq!(board.append("p", "nope", "x"), None);
        assert_eq!(board.clear_console("p", "nope"), None);
        assert!(board.console_of("p", "nope").is_empty());
        assert!(!board.exists("p", "nope"));
        assert!(!board.remove("p", "nope"));
    }

    #[test]
    fn pages_stay_in_the_order_they_were_added_and_belong_to_their_plugin() {
        let board = PageBoard::new();
        board.set("a", page("one", "A one"));
        board.set("b", page("one", "B one"));
        board.set("a", page("two", "A two"));
        board.set("a", page("one", "A one again"));

        let titles: Vec<(String, String)> = board.snapshot().into_iter().map(|e| (e.plugin_id, e.page.title)).collect();
        assert_eq!(
            titles,
            [("a".into(), "A one again".into()), ("b".into(), "B one".into()), ("a".into(), "A two".into())]
        );

        assert!(board.remove_all("a"));
        assert_eq!(board.snapshot().len(), 1);
    }

    #[test]
    fn a_console_change_carries_the_line() {
        let board = PageBoard::new();
        board.set("p", page("main", "Main"));
        assert_eq!(board.append("p", "main", "hi"), Some(PageChange::Console("hi".into())));
    }
}
