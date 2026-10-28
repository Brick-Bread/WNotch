//! The items of a to-do widget. Ported from `src/Notch.Core/Widgets/TodoList.cs`.

use serde_json::{Map, Value};

use crate::json::{self, Read};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TodoItem {
    pub text: String,
    pub done: bool,
}

impl TodoItem {
    pub fn new(text: impl Into<String>, done: bool) -> Self {
        Self { text: text.into(), done }
    }
}

/// The items of a to-do widget: open ones first, in the user's order, then the ticked ones. Kept in the
/// widget's options as JSON ([`ITEMS_KEY`]).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TodoList {
    items: Vec<TodoItem>,
}

pub const ITEMS_KEY: &str = "items";
pub const MAX_ITEMS: usize = 100;
pub const MAX_LENGTH: usize = 300;

impl TodoList {
    fn of(items: impl IntoIterator<Item = TodoItem>) -> Self {
        let all: Vec<TodoItem> = items.into_iter().collect();
        let (open, done): (Vec<_>, Vec<_>) = all.into_iter().partition(|i| !i.done);
        Self { items: open.into_iter().chain(done).collect() }
    }

    pub fn items(&self) -> &[TodoItem] {
        &self.items
    }

    pub fn any_done(&self) -> bool {
        self.items.iter().any(|i| i.done)
    }

    pub fn open_count(&self) -> usize {
        self.items.iter().filter(|i| !i.done).count()
    }

    /// Reads what [`Self::to_json`] wrote, `[{"Text":…,"Done":…}]`, by [`crate::json`]'s rules.
    /// Anything unreadable is an empty list rather than an error.
    pub fn from_json(json: Option<&str>) -> Self {
        let Some(json) = json.filter(|j| !j.trim().is_empty()) else { return Self::default() };
        let Ok(value) = serde_json::from_str::<Value>(json) else { return Self::default() };
        // A JSON null deserializes to a null array in C#, which `?? []` makes empty.
        let Ok(items) = json::list(&value, read_item) else { return Self::default() };
        Self::of(items.into_iter().filter_map(|i| i.filter(|i| !i.text.is_empty())).take(MAX_ITEMS))
    }

    /// The same shape System.Text.Json writes, `[{"Text":"milk","Done":false}]`. C# also escapes
    /// non-ASCII and HTML characters as `\uXXXX`; this writes them as they are, which reads the same.
    pub fn to_json(&self) -> String {
        let items = self
            .items
            .iter()
            .map(|i| {
                let mut map = Map::new();
                map.insert("Text".into(), Value::String(i.text.clone()));
                map.insert("Done".into(), Value::Bool(i.done));
                Value::Object(map)
            })
            .collect();
        Value::Array(items).to_string()
    }

    /// Adds an open item after the other open ones. False for empty text or a full list.
    pub fn add(&mut self, text: &str) -> bool {
        let text = tidy(text);
        if text.is_empty() || self.items.len() >= MAX_ITEMS {
            return false;
        }

        let open = self.open_count();
        self.items.insert(open, TodoItem::new(text, false));
        true
    }

    /// Ticks an open item, which goes to the top of the ticked ones, or unticks one, which goes to the end of the open ones.
    pub fn toggle(&mut self, index: usize) {
        if !self.valid(index) {
            return;
        }

        let mut item = self.items.remove(index);
        item.done = !item.done;
        let open = self.open_count();
        self.items.insert(open, item);
    }

    pub fn remove(&mut self, index: usize) {
        if self.valid(index) {
            self.items.remove(index);
        }
    }

    /// Changes an item's text; empty text removes it.
    pub fn edit(&mut self, index: usize, text: &str) {
        if !self.valid(index) {
            return;
        }

        let text = tidy(text);
        if text.is_empty() {
            self.items.remove(index);
        } else {
            self.items[index].text = text;
        }
    }

    pub fn clear_done(&mut self) {
        self.items.retain(|i| !i.done);
    }

    /// Moves an item to another place, counted as if it had already been taken out. It stays among its own
    /// kind: an open item never lands below a ticked one, nor a ticked one above an open one.
    pub fn move_item(&mut self, from: usize, to: usize) -> bool {
        if !self.valid(from) {
            return false;
        }

        let item = self.items.remove(from);
        let open = self.open_count();
        let to = if item.done { to.clamp(open, self.items.len()) } else { to.min(open) };
        self.items.insert(to, item);
        to != from
    }

    fn valid(&self, index: usize) -> bool {
        index < self.items.len()
    }
}

/// An item as C# reads its record: a missing or null `Text` is null, and is then left out.
fn read_item(value: &Value) -> Read<Option<TodoItem>> {
    let Value::Object(map) = value else { return Err(json::Mismatch) };
    let (mut text, mut done) = (None, false);
    for (key, value) in map {
        match key.as_str() {
            "Text" => text = json::optional_string(value)?,
            "Done" => done = json::bool(value)?,
            _ => {}
        }
    }
    Ok(text.map(|text| TodoItem { text, done }))
}

fn tidy(text: &str) -> String {
    // One line: a pasted paragraph becomes one item.
    let text = text.split_whitespace().collect::<Vec<_>>().join(" ");
    let units: Vec<u16> = text.encode_utf16().collect();
    if units.len() <= MAX_LENGTH {
        return text;
    }
    String::from_utf16_lossy(&units[..MAX_LENGTH]).trim_end().to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::widgets::layout::{self, HomeWidget, WidgetWidth, catalog, kinds};

    fn texts(list: &TodoList) -> Vec<String> {
        list.items().iter().map(|i| if i.done { format!("x {}", i.text) } else { i.text.clone() }).collect()
    }

    fn of(items: &[&str]) -> TodoList {
        let mut list = TodoList::from_json(None);
        for text in items {
            list.add(text);
        }
        list
    }

    #[test]
    fn adding_puts_new_items_after_the_open_ones_and_skips_empty_text() {
        let mut list = of(&["milk", "eggs"]);
        list.toggle(0);
        assert!(list.add("  bread \r\n rolls "));
        assert!(!list.add("   "));

        assert_eq!(texts(&list), ["eggs", "bread rolls", "x milk"]);
        assert_eq!(list.open_count(), 2);
    }

    #[test]
    fn ticking_moves_down_and_unticking_moves_back_up() {
        let mut list = of(&["a", "b", "c"]);
        list.toggle(0);
        list.toggle(0);
        assert_eq!(texts(&list), ["c", "x b", "x a"]);

        list.toggle(2);
        assert_eq!(texts(&list), ["c", "a", "x b"]);
    }

    #[test]
    fn moves_stay_within_open_or_ticked_items() {
        let mut list = of(&["a", "b", "c", "d"]);
        list.toggle(3);

        assert!(list.move_item(2, 0));
        assert_eq!(texts(&list), ["c", "a", "b", "x d"]);

        // An open item dragged below the ticked ones stops at the end of the open ones.
        list.move_item(0, 99);
        assert_eq!(texts(&list), ["a", "b", "c", "x d"]);

        // A ticked one dragged to the top stops at the first ticked place.
        assert!(!list.move_item(3, 0));
        assert_eq!(texts(&list), ["a", "b", "c", "x d"]);
    }

    #[test]
    fn edit_remove_and_clear_done() {
        let mut list = of(&["a", "b", "c"]);
        list.edit(0, "apples");
        list.edit(1, "  ");
        list.toggle(1);
        assert_eq!(texts(&list), ["apples", "x c"]);

        assert!(list.any_done());
        list.clear_done();
        assert_eq!(texts(&list), ["apples"]);

        list.remove(0);
        list.remove(5);
        assert!(list.items().is_empty());
    }

    #[test]
    fn round_trips_through_json_and_survives_bad_data() {
        let mut list = of(&["a", "b"]);
        list.toggle(0);
        let back = TodoList::from_json(Some(&list.to_json()));
        assert_eq!(texts(&list), texts(&back));
        assert_eq!(list.to_json(), r#"[{"Text":"b","Done":false},{"Text":"a","Done":true}]"#);

        assert!(TodoList::from_json(Some("not json")).items().is_empty());
        assert_eq!(
            texts(&TodoList::from_json(Some(
                r#"[{"Text":"done","Done":true},null,{"Text":"","Done":false},{"Text":"open","Done":false}]"#
            ))),
            ["open", "x done"]
        );
    }

    #[test]
    fn json_of_the_wrong_shape_is_an_empty_list() {
        for json in ["null", "{}", r#"[{"Text":1}]"#, r#"[{"Text":"a","Done":null}]"#, "[1]"] {
            assert!(TodoList::from_json(Some(json)).items().is_empty(), "{json}");
        }
        assert_eq!(texts(&TodoList::from_json(Some(r#"[{"Done":true},{"Text":"a"}]"#))), ["a"]);
    }

    #[test]
    fn long_text_is_cut() {
        let mut list = of(&[]);
        list.add(&"x".repeat(400));
        assert_eq!(list.items()[0].text.len(), MAX_LENGTH);
    }

    #[test]
    fn todo_is_in_the_catalog_and_can_be_added_more_than_once() {
        let todos: Vec<_> = catalog::all().iter().filter(|t| t.kind == kinds::TODO).collect();
        assert_eq!(todos.len(), 1);
        assert!(!todos[0].single);
        assert!(
            layout::addable(&[HomeWidget::new(kinds::TODO, WidgetWidth::Full)]).iter().any(|t| t.kind == kinds::TODO)
        );
    }
}
