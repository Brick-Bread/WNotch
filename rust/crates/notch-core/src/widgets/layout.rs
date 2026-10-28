//! The widgets on the Home tab. Ported from `src/Notch.Core/Widgets/HomeLayout.cs`.

use indexmap::IndexMap;
use serde_json::{Map, Value};

use crate::json::{self, Read, json_enum};

json_enum! {
    /// How much of a row on the Home tab a widget takes. Two half-width widgets next to each other share a row.
    pub enum WidgetWidth {
        Full = 0,
        Half = 1,
    }
    default Full;
}

/// One widget on the Home tab, as the settings keep it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HomeWidget {
    /// Tells two widgets of the same kind apart, such as two notes.
    pub id: String,
    /// One of [`kinds`].
    pub kind: String,
    pub width: WidgetWidth,
    /// For a widget that can have nothing to show: leave it out of Home until it has.
    pub hide_when_empty: bool,
    /// The widget's own settings, by option key, and anything it keeps, such as a note's text.
    pub options: IndexMap<String, String>,
    /// For a plugin widget: which plugin's type it is, as "plugin-id/type-id".
    pub source: Option<String>,
}

impl Default for HomeWidget {
    fn default() -> Self {
        Self {
            id: new_id(),
            kind: String::new(),
            width: WidgetWidth::Full,
            hide_when_empty: true,
            options: IndexMap::new(),
            source: None,
        }
    }
}

impl HomeWidget {
    pub fn new(kind: &str, width: WidgetWidth) -> Self {
        Self { kind: kind.to_owned(), width, ..Self::default() }
    }

    pub(crate) fn read(value: &Value) -> Read<Self> {
        let Value::Object(map) = value else { return Err(json::Mismatch) };
        let mut widget = Self::default();
        for (key, value) in map {
            match key.as_str() {
                "Id" => widget.id = json::string(value)?,
                "Kind" => widget.kind = json::string(value)?,
                "Width" => widget.width = WidgetWidth::read(value)?,
                "HideWhenEmpty" => widget.hide_when_empty = json::bool(value)?,
                "Options" => widget.options = json::string_map(value)?,
                "Source" => widget.source = json::optional_string(value)?,
                _ => {}
            }
        }
        Ok(widget)
    }

    pub(crate) fn to_json(&self) -> Value {
        let mut map = Map::new();
        map.insert("Id".into(), Value::String(self.id.clone()));
        map.insert("Kind".into(), Value::String(self.kind.clone()));
        map.insert("Width".into(), self.width.to_json());
        map.insert("HideWhenEmpty".into(), Value::Bool(self.hide_when_empty));
        map.insert(
            "Options".into(),
            Value::Object(self.options.iter().map(|(k, v)| (k.clone(), Value::String(v.clone()))).collect()),
        );
        map.insert("Source".into(), json::to_optional_string(self.source.as_deref()));
        Value::Object(map)
    }
}

/// A new widget id: 8 random lowercase hex digits.
pub fn new_id() -> String {
    let mut bytes = [0u8; 4];
    getrandom::fill(&mut bytes).expect("the system has no source of randomness");
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// The kinds of widget there are. Stored in the settings, so never renamed.
pub mod kinds {
    pub const MEDIA: &str = "media";
    pub const AGENTS: &str = "agents";
    pub const TIMER: &str = "timer";
    pub const CALENDAR: &str = "calendar";
    pub const CLOCK: &str = "clock";
    pub const STATS: &str = "stats";
    pub const SHELF: &str = "shelf";
    pub const CLIPBOARD: &str = "clipboard";
    pub const WEATHER: &str = "weather";
    pub const NOTE: &str = "note";
    pub const TODO: &str = "todo";
    pub const LAUNCHER: &str = "launcher";
    pub const COMMAND: &str = "command";
    pub const FEED: &str = "feed";
    pub const PLUGIN: &str = "plugin";
    /// A widget a plugin draws itself; [`super::HomeWidget::source`] says whose.
    pub const PLUGIN_WIDGET: &str = "plugin-widget";
}

/// The layout a new install starts with: media and agents across, then the timer and the calendar side by side.
pub fn default_layout() -> Vec<HomeWidget> {
    vec![
        HomeWidget::new(kinds::MEDIA, WidgetWidth::Full),
        HomeWidget::new(kinds::AGENTS, WidgetWidth::Full),
        HomeWidget::new(kinds::TIMER, WidgetWidth::Half),
        HomeWidget::new(kinds::CALENDAR, WidgetWidth::Half),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_are_eight_hex_digits_and_differ() {
        let (a, b) = (new_id(), new_id());
        assert_eq!(a.len(), 8);
        assert!(a.bytes().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
        assert_ne!(a, b);
    }
}
