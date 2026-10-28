//! The widgets on the Home tab, the kinds there are, and how Home lays them out. Ported from
//! `src/Notch.Core/Widgets/HomeLayout.cs`.

use std::collections::HashSet;

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
    /// For a widget that can have nothing to show ([`WidgetType::can_be_empty`]): leave it out of Home until it has.
    pub hide_when_empty: bool,
    /// The widget's own settings, by [`WidgetOption::key`], and anything it keeps, such as a note's text.
    pub options: IndexMap<String, String>,
    /// For a [`kinds::PLUGIN_WIDGET`]: which plugin's type it is, as "plugin-id/type-id".
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

    pub fn create(widget_type: &WidgetType) -> Self {
        Self::new(&widget_type.kind, widget_type.default_width)
    }

    /// An option's value, or what the widget's type gives it when it has not been set.
    pub fn option(&self, key: &str) -> String {
        if let Some(value) = self.options.get(key) {
            return value.clone();
        }

        catalog::find(&self.kind)
            .and_then(|t| t.options.iter().find(|o| o.key == key))
            .map(|o| o.default.clone())
            .unwrap_or_default()
    }

    /// A toggle option is "true" or anything else.
    pub fn flag(&self, key: &str) -> bool {
        self.option(key) == WidgetOption::ON
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WidgetOptionType {
    /// One line of text.
    Text,
    /// Several lines of text, one item per line.
    Lines,
    /// On or off, stored as [`WidgetOption::ON`] or [`WidgetOption::OFF`].
    Toggle,
    /// One of [`WidgetOption::choices`]; `None` choices are filled in by the app (the plugin cards there are).
    Choice,
}

/// Something about a widget the user can set from its gear button.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WidgetOption {
    pub key: String,
    pub label: String,
    pub option_type: WidgetOptionType,
    pub default: String,
    pub choices: Option<Vec<String>>,
    pub hint: Option<String>,
}

impl WidgetOption {
    pub const ON: &str = "true";
    pub const OFF: &str = "false";

    /// An option with an empty default, no choices and no hint, as the C# record's optional parameters give.
    pub fn new(key: &str, label: &str, option_type: WidgetOptionType) -> Self {
        Self {
            key: key.to_owned(),
            label: label.to_owned(),
            option_type,
            default: String::new(),
            choices: None,
            hint: None,
        }
    }

    pub fn with_default(mut self, default: &str) -> Self {
        default.clone_into(&mut self.default);
        self
    }

    pub fn with_choices(mut self, choices: &[&str]) -> Self {
        self.choices = Some(choices.iter().map(|&c| c.to_owned()).collect());
        self
    }

    pub fn with_hint(mut self, hint: &str) -> Self {
        self.hint = Some(hint.to_owned());
        self
    }
}

/// A kind of widget as the "Add widget" picker offers it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WidgetType {
    pub kind: String,
    pub name: String,
    pub glyph: String,
    pub description: String,
    pub default_width: WidgetWidth,
    /// Only one can be on Home, because it shows something there is only one of, such as the timer.
    pub single: bool,
    /// Sometimes has nothing to show, and can then be left out ([`HomeWidget::hide_when_empty`]).
    pub can_be_empty: bool,
    pub options: Vec<WidgetOption>,
}

impl WidgetType {
    /// A new widget of this type has options that must be filled in before it shows anything.
    pub fn needs_setup(&self) -> bool {
        matches!(self.kind.as_str(), kinds::WEATHER | kinds::LAUNCHER | kinds::COMMAND | kinds::FEED | kinds::PLUGIN)
    }
}

/// The widget types there are. `WidgetCatalog.ForPlugin` is not ported here: plugin widget types
/// come from the plugin host.
pub mod catalog {
    use std::sync::LazyLock;

    use super::{WidgetOption, WidgetOptionType, WidgetType, WidgetWidth, kinds};

    pub const TITLE_KEY: &str = "title";

    /// Where a note keeps what was typed into it. Not shown in the note's settings.
    pub const NOTE_TEXT_KEY: &str = "text";

    pub const RUN_IN_TERMINAL: &str = "Terminal tab";
    pub const RUN_HIDDEN: &str = "In the background";

    #[allow(clippy::too_many_arguments)]
    fn entry(
        kind: &str,
        name: &str,
        glyph: &str,
        description: &str,
        default_width: WidgetWidth,
        single: bool,
        can_be_empty: bool,
        options: Vec<WidgetOption>,
    ) -> WidgetType {
        WidgetType {
            kind: kind.to_owned(),
            name: name.to_owned(),
            glyph: glyph.to_owned(),
            description: description.to_owned(),
            default_width,
            single,
            can_be_empty,
            options,
        }
    }

    static ALL: LazyLock<Vec<WidgetType>> = LazyLock::new(|| {
        use WidgetOptionType::{Choice, Lines, Text, Toggle};
        use WidgetWidth::{Full, Half};
        let option = WidgetOption::new;
        vec![
            entry(kinds::MEDIA, "Media", "\u{E8D6}", "What is playing, with its controls", Full, true, true, vec![]),
            entry(
                kinds::AGENTS,
                "Agents",
                "\u{E99A}",
                "Coding agents and what they are doing",
                Full,
                true,
                true,
                vec![],
            ),
            entry(kinds::TIMER, "Timer", "\u{E916}", "Pomodoro and one-click timers", Half, true, false, vec![]),
            entry(
                kinds::CALENDAR,
                "Calendar",
                "\u{E787}",
                "Upcoming events from your feeds",
                Half,
                true,
                false,
                vec![],
            ),
            entry(
                kinds::CLOCK,
                "Clock",
                "\u{E121}",
                "The time here or anywhere else",
                Half,
                false,
                false,
                vec![
                    option(TITLE_KEY, "Label", Text).with_hint("Shown above the time, such as a city"),
                    option("zone", "Time zone", Text)
                        .with_hint("Empty for this computer's, or a city or zone such as Tokyo or UTC"),
                    option("hours24", "24-hour clock", Toggle).with_default(WidgetOption::OFF),
                    option("seconds", "Show seconds", Toggle).with_default(WidgetOption::OFF),
                ],
            ),
            entry(
                kinds::STATS,
                "System stats",
                "\u{E9D2}",
                "CPU, memory and more at a glance",
                Half,
                false,
                false,
                vec![
                    option("cpu", "CPU", Toggle).with_default(WidgetOption::ON),
                    option("memory", "Memory", Toggle).with_default(WidgetOption::ON),
                    option("gpu", "GPU", Toggle).with_default(WidgetOption::OFF),
                    option("network", "Network", Toggle).with_default(WidgetOption::OFF),
                    option("battery", "Battery", Toggle).with_default(WidgetOption::OFF),
                ],
            ),
            entry(kinds::SHELF, "Shelf", "\u{E7B8}", "The latest files on the shelf", Full, true, true, vec![]),
            entry(kinds::CLIPBOARD, "Clipboard", "\u{E77F}", "What you copied last", Half, true, true, vec![]),
            entry(
                kinds::WEATHER,
                "Weather",
                "\u{E706}",
                "Current weather from Open-Meteo",
                Half,
                false,
                false,
                vec![
                    option("city", "City", Text).with_hint("Such as London or San Francisco"),
                    option("units", "Units", Choice).with_default("Celsius").with_choices(&["Celsius", "Fahrenheit"]),
                ],
            ),
            entry(
                kinds::NOTE,
                "Note",
                "\u{E70B}",
                "Text you type, kept for you",
                Half,
                false,
                false,
                vec![option(TITLE_KEY, "Title", Text).with_default("Note")],
            ),
            entry(
                kinds::TODO,
                "To-do list",
                "\u{E73A}",
                "Tasks to tick off, in the order you choose",
                Half,
                false,
                false,
                vec![option(TITLE_KEY, "Title", Text).with_default("To do")],
            ),
            entry(
                kinds::LAUNCHER,
                "Launcher",
                "\u{E8A7}",
                "Buttons that open apps, files, folders or links",
                Half,
                false,
                false,
                vec![
                    option(TITLE_KEY, "Title", Text).with_default("Launcher"),
                    option("items", "Buttons", Lines)
                        .with_hint("One per line: Name = C:\\path\\app.exe, a folder, or https://link"),
                ],
            ),
            entry(
                kinds::COMMAND,
                "Commands",
                "\u{E756}",
                "Buttons that run commands",
                Half,
                false,
                false,
                vec![
                    option(TITLE_KEY, "Title", Text).with_default("Commands"),
                    option("commands", "Buttons", Lines)
                        .with_hint("One per line: Name = command, such as Build = dotnet build"),
                    option("run", "Run in", Choice)
                        .with_default(RUN_IN_TERMINAL)
                        .with_choices(&[RUN_IN_TERMINAL, RUN_HIDDEN]),
                ],
            ),
            entry(
                kinds::FEED,
                "Web feed",
                "\u{E774}",
                "Text fetched from a link, kept up to date",
                Half,
                false,
                false,
                vec![
                    option(TITLE_KEY, "Title", Text).with_default("Feed"),
                    option("url", "Link", Text).with_hint("https://… returning text or JSON"),
                    option("field", "JSON field", Text).with_hint("Optional, such as data.status or items.0.title"),
                    option("minutes", "Refresh every", Choice)
                        .with_default("15 min")
                        .with_choices(&["1 min", "5 min", "15 min", "60 min"]),
                ],
            ),
            entry(
                kinds::PLUGIN,
                "Plugin card",
                "\u{E71D}",
                "A card from a plugin, on Home",
                Half,
                false,
                true,
                vec![option("card", "Card", Choice)],
            ),
            // Stands for every plugin's widget types; the app offers each of those in the picker by its own name.
            entry(
                kinds::PLUGIN_WIDGET,
                "Plugin widget",
                "\u{E71D}",
                "A widget from a plugin",
                Half,
                false,
                false,
                vec![],
            ),
        ]
    });

    pub fn all() -> &'static [WidgetType] {
        &ALL
    }

    pub fn find(kind: &str) -> Option<&'static WidgetType> {
        all().iter().find(|t| t.kind == kind)
    }
}

/// More than this is more than the notch can show, even scrolling.
pub const MAX_WIDGETS: usize = 24;

/// What Home showed before it could be changed: media, agents, then the timer and calendar side by side.
pub fn default_layout() -> Vec<HomeWidget> {
    vec![
        HomeWidget::new(kinds::MEDIA, WidgetWidth::Full),
        HomeWidget::new(kinds::AGENTS, WidgetWidth::Full),
        HomeWidget::new(kinds::TIMER, WidgetWidth::Half),
        HomeWidget::new(kinds::CALENDAR, WidgetWidth::Half),
    ]
}

/// The rows Home shows, top to bottom: a full-width widget alone, two half-width widgets that
/// follow each other together, and a half-width widget with no partner alone on the left.
/// Widgets that are not `shown` take no room, so their neighbours pair up as if they were not there.
pub fn rows<'a>(
    widgets: impl IntoIterator<Item = &'a HomeWidget>,
    shown: impl Fn(&HomeWidget) -> bool,
) -> Vec<Vec<&'a HomeWidget>> {
    let mut rows = Vec::new();
    let mut waiting: Option<&HomeWidget> = None;
    for widget in widgets.into_iter().filter(|w| shown(w)) {
        if widget.width == WidgetWidth::Full {
            if let Some(alone) = waiting.take() {
                rows.push(vec![alone]);
            }

            rows.push(vec![widget]);
        } else if let Some(partner) = waiting.take() {
            rows.push(vec![partner, widget]);
        } else {
            waiting = Some(widget);
        }
    }

    if let Some(alone) = waiting {
        rows.push(vec![alone]);
    }

    rows
}

/// Repairs a list read from the settings: kinds this version does not know are dropped, as are
/// second copies of a single widget, missing or repeated ids are replaced, and there are no more than fit.
pub fn normalize(widgets: impl IntoIterator<Item = HomeWidget>) -> Vec<HomeWidget> {
    let mut result: Vec<HomeWidget> = Vec::new();
    let mut ids = HashSet::new();
    for mut widget in widgets {
        let Some(widget_type) = catalog::find(&widget.kind) else { continue };

        // A plugin widget is kept while its plugin is off, so it is back in its place when the plugin is.
        if widget.kind == kinds::PLUGIN_WIDGET && widget.source.as_deref().is_none_or(|s| s.trim().is_empty()) {
            continue;
        }

        if widget_type.single && result.iter().any(|w| w.kind == widget.kind) {
            continue;
        }

        if widget.id.trim().is_empty() || !ids.insert(widget.id.clone()) {
            loop {
                widget.id = new_id();
                if ids.insert(widget.id.clone()) {
                    break;
                }
            }
        }

        if let WidgetWidth::Unknown(_) = widget.width {
            widget.width = widget_type.default_width;
        }

        result.push(widget);
        if result.len() == MAX_WIDGETS {
            break;
        }
    }

    result
}

/// The built-in types the picker can still add: every type, less the single ones already on Home.
///
/// `HomeLayout.AddableFromPlugins` is not ported here; it belongs with the plugin widget types.
pub fn addable(widgets: &[HomeWidget]) -> Vec<&'static WidgetType> {
    if widgets.len() >= MAX_WIDGETS {
        return Vec::new();
    }

    catalog::all()
        .iter()
        .filter(|t| t.kind != kinds::PLUGIN_WIDGET && (!t.single || widgets.iter().all(|w| w.kind != t.kind)))
        .collect()
}

/// Moves a widget to a new place in the list, counted as if it had already been taken out. False when nothing moved.
pub fn move_to(widgets: &mut Vec<HomeWidget>, id: &str, index: usize) -> bool {
    let Some(from) = widgets.iter().position(|w| w.id == id) else { return false };

    let index = index.min(widgets.len() - 1);
    if index == from {
        return false;
    }

    let widget = widgets.remove(from);
    widgets.insert(index, widget);
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::settings::AppSettings;

    fn widget(kind: &str, width: WidgetWidth, id: Option<&str>) -> HomeWidget {
        HomeWidget { id: id.map_or_else(new_id, str::to_owned), ..HomeWidget::new(kind, width) }
    }

    fn row_kinds(rows: &[Vec<&HomeWidget>]) -> Vec<Vec<String>> {
        rows.iter().map(|r| r.iter().map(|w| w.kind.clone()).collect()).collect()
    }

    fn texts(items: &[&[&str]]) -> Vec<Vec<String>> {
        items.iter().map(|r| r.iter().map(|&k| k.to_owned()).collect()).collect()
    }

    #[test]
    fn default_layout_is_what_home_showed_before() {
        let widgets = default_layout();
        assert_eq!(
            row_kinds(&rows(&widgets, |_| true)),
            texts(&[&[kinds::MEDIA], &[kinds::AGENTS], &[kinds::TIMER, kinds::CALENDAR]])
        );
    }

    #[test]
    fn half_widgets_pair_up_and_a_lone_one_keeps_its_own_row() {
        let widgets = [
            widget(kinds::CLOCK, WidgetWidth::Half, None),
            widget(kinds::MEDIA, WidgetWidth::Full, None),
            widget(kinds::NOTE, WidgetWidth::Half, None),
            widget(kinds::NOTE, WidgetWidth::Half, None),
            widget(kinds::WEATHER, WidgetWidth::Half, None),
        ];

        assert_eq!(
            row_kinds(&rows(&widgets, |_| true)),
            texts(&[&[kinds::CLOCK], &[kinds::MEDIA], &[kinds::NOTE, kinds::NOTE], &[kinds::WEATHER]])
        );
    }

    #[test]
    fn hidden_widgets_take_no_room_so_their_neighbours_pair() {
        let widgets = [
            widget(kinds::TIMER, WidgetWidth::Half, None),
            widget(kinds::CLIPBOARD, WidgetWidth::Half, None),
            widget(kinds::CALENDAR, WidgetWidth::Half, None),
        ];

        assert_eq!(
            row_kinds(&rows(&widgets, |w| w.kind != kinds::CLIPBOARD)),
            texts(&[&[kinds::TIMER, kinds::CALENDAR]])
        );
    }

    #[test]
    fn normalize_drops_unknown_kinds_and_second_singles_and_fixes_ids() {
        // The C# list also holds a null, which the Rust list cannot.
        let widgets = normalize([
            widget(kinds::TIMER, WidgetWidth::Half, Some("a")),
            widget("hologram", WidgetWidth::Full, Some("b")),
            widget(kinds::TIMER, WidgetWidth::Half, Some("c")),
            widget(kinds::NOTE, WidgetWidth::Half, Some("a")),
            widget(kinds::NOTE, WidgetWidth::Half, Some("")),
        ]);

        assert_eq!(
            widgets.iter().map(|w| w.kind.as_str()).collect::<Vec<_>>(),
            [kinds::TIMER, kinds::NOTE, kinds::NOTE]
        );
        assert_eq!(widgets.iter().map(|w| &w.id).collect::<HashSet<_>>().len(), 3);
        assert_eq!(widgets[0].id, "a");
        assert!(widgets.iter().all(|w| !w.id.trim().is_empty()));
    }

    #[test]
    fn normalize_gives_unknown_widths_the_default_and_stops_at_the_limit() {
        let widgets = normalize([widget(kinds::CLOCK, WidgetWidth::Unknown(7), None)]);
        assert_eq!(widgets[0].width, WidgetWidth::Half);

        assert_eq!(normalize((0..30).map(|_| widget(kinds::NOTE, WidgetWidth::Half, None))).len(), MAX_WIDGETS);
    }

    #[test]
    fn plugin_widgets_survive_normalizing_only_with_a_source() {
        let mut placed = widget(kinds::PLUGIN_WIDGET, WidgetWidth::Half, Some("x"));
        placed.source = Some("a/counter".into());
        let widgets = normalize([placed, widget(kinds::PLUGIN_WIDGET, WidgetWidth::Half, Some("y"))]);

        assert_eq!(widgets.iter().map(|w| w.id.as_str()).collect::<Vec<_>>(), ["x"]);
        assert!(!addable(&widgets).iter().any(|t| t.kind == kinds::PLUGIN_WIDGET));
    }

    #[test]
    fn addable_leaves_out_singles_already_on_home() {
        let addable = addable(&default_layout());

        assert!(!addable.iter().any(|t| t.kind == kinds::TIMER));
        assert!(!addable.iter().any(|t| t.kind == kinds::MEDIA));
        assert!(addable.iter().any(|t| t.kind == kinds::NOTE));
        assert!(addable.iter().any(|t| t.kind == kinds::SHELF));
    }

    #[test]
    fn move_puts_a_widget_in_its_new_place() {
        let mut widgets = default_layout();
        let timer = widgets[2].id.clone();

        assert!(move_to(&mut widgets, &timer, 0));
        assert_eq!(
            widgets.iter().map(|w| w.kind.as_str()).collect::<Vec<_>>(),
            [kinds::TIMER, kinds::MEDIA, kinds::AGENTS, kinds::CALENDAR]
        );
        assert!(!move_to(&mut widgets, &timer, 0));
        assert!(move_to(&mut widgets, &timer, 99));
        assert_eq!(widgets.last().map(|w| w.kind.as_str()), Some(kinds::TIMER));
        assert!(!move_to(&mut widgets, "missing", 0));
    }

    #[test]
    fn options_fall_back_to_their_defaults() {
        let mut clock = HomeWidget::new(kinds::CLOCK, WidgetWidth::Full);
        assert!(!clock.flag("seconds"));
        clock.options.insert("seconds".into(), WidgetOption::ON.into());
        assert!(clock.flag("seconds"));

        let stats = HomeWidget::new(kinds::STATS, WidgetWidth::Full);
        assert!(stats.flag("cpu"));
        assert_eq!(stats.option("nonsense"), "");
    }

    #[test]
    fn settings_without_a_layout_get_the_default_and_one_round_trips() {
        let mut fresh = AppSettings::from_json("{}");
        let default_kinds: Vec<String> = default_layout().into_iter().map(|w| w.kind).collect();
        assert_eq!(fresh.home_widgets.iter().map(|w| w.kind.clone()).collect::<Vec<_>>(), default_kinds);

        fresh.home_widgets = vec![widget(kinds::NOTE, WidgetWidth::Full, Some("n1"))];
        fresh.home_widgets[0].options.insert(catalog::NOTE_TEXT_KEY.into(), "milk".into());
        let back = AppSettings::from_json(&fresh.to_json());

        assert_eq!(back.home_widgets.len(), 1);
        let note = &back.home_widgets[0];
        assert_eq!(
            (note.id.as_str(), note.width, note.option(catalog::NOTE_TEXT_KEY).as_str()),
            ("n1", WidgetWidth::Full, "milk")
        );
        assert!(fresh.to_json().contains("\"Full\""));
    }

    #[test]
    fn every_catalog_kind_is_distinct_and_choices_have_valid_defaults() {
        let all = catalog::all();
        assert_eq!(all.iter().map(|t| &t.kind).collect::<HashSet<_>>().len(), all.len());
        for option in all.iter().flat_map(|t| &t.options).filter(|o| o.option_type == WidgetOptionType::Choice) {
            if let Some(choices) = &option.choices {
                assert!(choices.contains(&option.default), "{}", option.key);
            }
        }
    }

    #[test]
    fn types_that_need_setup_and_new_widgets_of_a_type() {
        let weather = catalog::find(kinds::WEATHER).unwrap();
        assert!(weather.needs_setup());
        assert!(!catalog::find(kinds::NOTE).unwrap().needs_setup());

        let created = HomeWidget::create(catalog::find(kinds::MEDIA).unwrap());
        assert_eq!((created.kind.as_str(), created.width), (kinds::MEDIA, WidgetWidth::Full));
    }

    #[test]
    fn ids_are_eight_hex_digits_and_differ() {
        let (a, b) = (new_id(), new_id());
        assert_eq!(a.len(), 8);
        assert!(a.bytes().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
        assert_ne!(a, b);
    }
}
