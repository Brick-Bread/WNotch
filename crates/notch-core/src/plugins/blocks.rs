//! What a plugin shows besides activities: cards on the Plugins tab and pages with figures,
//! buttons, a console and free-form blocks. All of it is plain data; the UI draws it in Notch's
//! own style. These are the shapes plugins send (see `docs/plugins.md`) and the UI receives.

use base64::engine::general_purpose::STANDARD;
use base64::Engine;
use serde::de::{self, Deserializer};
use serde::{Deserialize, Serialize, Serializer};
use serde_json::Value;

use crate::glow::GlowColor;

/// Figures drawn in a row on top of a page.
pub const MAX_STATS: usize = 8;
/// Buttons in the top right of a page.
pub const MAX_ACTIONS: usize = 6;
/// Blocks drawn per page; the rest are ignored.
pub const MAX_BLOCKS: usize = 100;
/// Columns of a table block that are drawn.
pub const MAX_TABLE_COLUMNS: usize = 8;
/// Rows of a table block that are drawn.
pub const MAX_TABLE_ROWS: usize = 200;
/// Values of a chart block that are drawn.
pub const MAX_CHART_VALUES: usize = 240;
/// The largest picture a block may carry, as base64 text.
pub const MAX_IMAGE_BASE64: usize = 4 * 1024 * 1024;

/// A colour as a plugin writes it: a name from Notch's palette (`"Violet"`), `"#rrggbb"`,
/// `"#rgb"`, `{ "r": 1, "g": 2, "b": 3 }` or `[r, g, b]`. Sent to the UI as `{ r, g, b }`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Color(pub GlowColor);

impl Color {
    /// Reads a colour from its JSON form.
    pub fn from_json(value: &Value) -> Option<Color> {
        match value {
            Value::String(text) => parse_color_text(text.trim()).map(Color),
            Value::Object(map) => {
                let channel = |name: &str| map.get(name).and_then(Value::as_u64).and_then(|n| u8::try_from(n).ok());
                Some(Color(GlowColor::new(channel("r")?, channel("g")?, channel("b")?)))
            }
            Value::Array(items) if items.len() == 3 => {
                let channel = |i: usize| items[i].as_u64().and_then(|n| u8::try_from(n).ok());
                Some(Color(GlowColor::new(channel(0)?, channel(1)?, channel(2)?)))
            }
            _ => None,
        }
    }
}

fn parse_color_text(text: &str) -> Option<GlowColor> {
    let Some(hex) = text.strip_prefix('#') else {
        return GlowColor::from_name(text);
    };
    let digits: Option<Vec<u8>> = hex.chars().map(|c| c.to_digit(16).and_then(|d| u8::try_from(d).ok())).collect();
    let digits = digits?;
    match digits.len() {
        3 => Some(GlowColor::new(digits[0] * 17, digits[1] * 17, digits[2] * 17)),
        6 => Some(GlowColor::new(digits[0] * 16 + digits[1], digits[2] * 16 + digits[3], digits[4] * 16 + digits[5])),
        _ => None,
    }
}

impl Serialize for Color {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        self.0.serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for Color {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let value = Value::deserialize(deserializer)?;
        Color::from_json(&value).ok_or_else(|| {
            de::Error::custom("a colour is a name like \"Violet\", \"#rrggbb\", { r, g, b } or [r, g, b]")
        })
    }
}

/// How a text block is set.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TextStyle {
    #[default]
    Body,
    Heading,
    /// Smaller and dimmer.
    Muted,
    /// Monospace, e.g. for log output or a path.
    Code,
}

/// A button on a page or inside a buttons block.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Action {
    pub label: String,
    /// Colour of the label; none for the notch's own colours.
    #[serde(default)]
    pub color: Option<Color>,
    /// False greys the button out and ignores clicks.
    #[serde(default = "yes")]
    pub enabled: bool,
    /// For actions that cannot be undone: the first click changes the label to "Click again"
    /// for a few seconds, and only a second click inside that time reaches the plugin.
    #[serde(default)]
    pub confirm: bool,
    /// Shown as a tooltip.
    #[serde(default)]
    pub hint: Option<String>,
}

fn yes() -> bool {
    true
}

fn default_chart_height() -> f64 {
    72.0
}

fn default_image_height() -> f64 {
    120.0
}

fn default_max() -> f64 {
    100.0
}

fn default_step() -> f64 {
    1.0
}

/// The building blocks of a page body. Blocks are drawn in a scrolling column in place of the
/// console, and keep what the user is typing when the plugin sends an updated page.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum BlockKind {
    /// A paragraph. Wraps; line breaks are kept.
    Text {
        text: String,
        #[serde(default)]
        style: TextStyle,
        #[serde(default)]
        color: Option<Color>,
    },
    /// A caption on the left and a value on the right.
    Value {
        label: String,
        #[serde(default)]
        value: Option<String>,
        #[serde(default)]
        color: Option<Color>,
    },
    /// A caption, a value and a bar.
    Progress {
        label: String,
        #[serde(default)]
        value: Option<String>,
        /// 0..1.
        #[serde(default)]
        progress: f64,
        #[serde(default)]
        color: Option<Color>,
    },
    /// A grid of text with a header row. Up to eight columns and 200 rows are drawn.
    Table {
        columns: Vec<String>,
        #[serde(default)]
        rows: Vec<Vec<String>>,
    },
    /// A line chart, oldest value on the left. Up to 240 values are drawn.
    Chart {
        #[serde(default)]
        label: Option<String>,
        values: Vec<f64>,
        /// The value that reaches the top. Defaults to the largest value shown, or 1 when that is smaller.
        #[serde(default)]
        max: Option<f64>,
        #[serde(default)]
        color: Option<Color>,
        /// In pixels, 40 to 240.
        #[serde(default = "default_chart_height")]
        height: f64,
    },
    /// A row of buttons inside the body.
    Buttons { actions: Vec<Action> },
    /// A labelled switch.
    Toggle {
        label: String,
        #[serde(default)]
        detail: Option<String>,
        #[serde(default)]
        value: bool,
    },
    /// A labelled slider with its current value shown beside it. The plugin hears about the
    /// new value once the user lets go of the handle.
    Slider {
        label: String,
        #[serde(default)]
        min: f64,
        #[serde(default = "default_max")]
        max: f64,
        #[serde(default)]
        value: f64,
        /// How far one step moves the value; 0 for a continuous slider.
        #[serde(default = "default_step")]
        step: f64,
        /// Text after the number, e.g. `%` or ` s`.
        #[serde(default)]
        unit: Option<String>,
    },
    /// A labelled list of options, one of which is selected.
    Select {
        label: String,
        options: Vec<String>,
        #[serde(default)]
        selected: Option<String>,
    },
    /// A labelled text box with a button that submits it.
    #[serde(rename = "textfield")]
    TextField {
        label: String,
        /// The text to show. While the user is typing in the box, Notch leaves their text alone
        /// when an updated page arrives.
        #[serde(default)]
        value: Option<String>,
        /// Greyed text in the empty box.
        #[serde(default)]
        hint: Option<String>,
        /// Shows dots instead of the text.
        #[serde(default)]
        secret: bool,
        /// The button's text. Defaults to "Save".
        #[serde(default, rename = "submitLabel")]
        submit_label: Option<String>,
    },
    /// A picture: PNG, JPEG, GIF or WebP, base64 encoded.
    Image {
        data: String,
        /// In pixels, 20 to 400. The width follows the picture.
        #[serde(default = "default_image_height")]
        height: f64,
    },
    /// A thin line between groups of blocks.
    Separator {},
}

/// A block and the id the plugin is told about when the user operates it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Block {
    /// Names the block in events (`block.toggle` and the like). Defaults to its position in the
    /// page's `blocks`, counted from 0, as text.
    pub id: Option<String>,
    #[serde(flatten)]
    pub kind: BlockKind,
}

/// One clickable row of a page's list: a name, a headline value and a line of detail.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Choice {
    pub label: String,
    #[serde(default)]
    pub value: Option<String>,
    #[serde(default)]
    pub detail: Option<String>,
    /// Colour of the value; none for the notch's own colours.
    #[serde(default)]
    pub color: Option<Color>,
}

/// One figure on a page: a caption, a headline value and a line of detail.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Stat {
    pub label: String,
    #[serde(default)]
    pub value: Option<String>,
    #[serde(default)]
    pub detail: Option<String>,
    /// 0..1 draws a bar along the bottom; none for no bar.
    #[serde(default)]
    pub progress: Option<f64>,
    #[serde(default)]
    pub color: Option<Color>,
}

/// A tab: figures on top, then a console, a list of choices or free-form blocks.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Page {
    /// Stable per page and unique within the plugin.
    pub id: String,
    /// The tab's name. Keep it short.
    pub title: String,
    /// One line under the tab names, e.g. a connection state.
    #[serde(default)]
    pub status: Option<String>,
    /// The figures in a row on top, up to eight.
    #[serde(default)]
    pub stats: Vec<Stat>,
    /// Shows an input line under the console; each submitted line reaches the plugin as
    /// `page.input`. False makes the console read-only.
    #[serde(default)]
    pub input: bool,
    /// Greyed text in the empty input line, e.g. "Send a command".
    #[serde(default)]
    pub input_hint: Option<String>,
    /// Shows a back button in the top left; a click reaches the plugin as `page.back`.
    #[serde(default)]
    pub back: bool,
    /// The back button's text, e.g. "Servers". Defaults to "Back".
    #[serde(default)]
    pub back_label: Option<String>,
    /// A list of rows the user can click, shown in place of the console while it is not empty.
    #[serde(default)]
    pub choices: Vec<Choice>,
    /// A row of buttons in the top right of the page, e.g. Start, Stop, Restart. Up to six.
    #[serde(default)]
    pub actions: Vec<Action>,
    /// Free-form content shown in place of the console while it is not empty (and while there
    /// are no choices, which win). Up to 100 blocks. Blocks of an unknown type are skipped.
    #[serde(default, deserialize_with = "lenient_blocks")]
    pub blocks: Vec<Block>,
}

fn lenient_blocks<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<Block>, D::Error> {
    let values = Vec::<Value>::deserialize(deserializer)?;
    Ok(values
        .into_iter()
        .enumerate()
        .filter_map(|(index, mut value)| {
            // The default id is the position the plugin gave the block, whatever is skipped.
            if let Value::Object(map) = &mut value {
                map.entry("id").or_insert_with(|| Value::String(index.to_string()));
            }
            serde_json::from_value(value).ok()
        })
        .collect())
}

impl Page {
    /// Checks the page and trims it to the limits. A page needs an id and a title.
    pub fn sanitized(mut self) -> Result<Page, String> {
        if self.id.trim().is_empty() {
            return Err("a page needs an id".into());
        }
        if self.title.trim().is_empty() {
            return Err("a page needs a title".into());
        }
        self.stats.truncate(MAX_STATS);
        self.actions.truncate(MAX_ACTIONS);
        self.blocks.truncate(MAX_BLOCKS);
        self.blocks = self.blocks.into_iter().filter_map(Block::sanitized).collect();
        for stat in &mut self.stats {
            stat.progress = stat.progress.map(unit_range);
        }
        Ok(self)
    }

    /// True when the page has a way for the user to type something that Notch must not steal
    /// the keyboard from: an input line, or a text box among the blocks that shows.
    pub fn takes_input(&self) -> bool {
        self.input || (self.choices.is_empty() && self.blocks.iter().any(|b| matches!(b.kind, BlockKind::TextField { .. })))
    }
}

fn unit_range(value: f64) -> f64 {
    if value.is_nan() {
        0.0
    } else {
        value.clamp(0.0, 1.0)
    }
}

impl Block {
    /// Trims one block to its limits; none when it cannot be drawn (a picture that is not base64).
    fn sanitized(mut self) -> Option<Block> {
        match &mut self.kind {
            BlockKind::Progress { progress, .. } => *progress = unit_range(*progress),
            BlockKind::Table { columns, rows } => {
                columns.truncate(MAX_TABLE_COLUMNS);
                rows.truncate(MAX_TABLE_ROWS);
                for row in rows.iter_mut() {
                    row.truncate(columns.len().max(1));
                }
                if columns.is_empty() {
                    return None;
                }
            }
            BlockKind::Chart { values, height, max, .. } => {
                values.truncate(MAX_CHART_VALUES);
                values.retain(|v| v.is_finite());
                *height = height.clamp(40.0, 240.0);
                *max = max.filter(|m| m.is_finite());
            }
            BlockKind::Slider { min, max, value, step, .. } => {
                if !(min.is_finite() && max.is_finite() && value.is_finite() && step.is_finite()) || *max <= *min {
                    return None;
                }
                *value = value.clamp(*min, *max);
                *step = step.max(0.0);
            }
            BlockKind::Image { data, height } => {
                if data.len() > MAX_IMAGE_BASE64 || STANDARD.decode(data.as_bytes()).is_err() {
                    return None;
                }
                *height = height.clamp(20.0, 400.0);
            }
            _ => {}
        }
        Some(self)
    }
}

/// A small panel on the Plugins tab: a caption, a headline value and a line of detail.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Card {
    /// Stable per card and unique within the plugin, e.g. `status`.
    pub id: String,
    /// Small caption at the top, e.g. "Build".
    pub label: String,
    /// The headline, shown large on one line. Keep it to a few characters.
    #[serde(default)]
    pub value: Option<String>,
    /// Secondary text under the value; wraps to two lines.
    #[serde(default)]
    pub detail: Option<String>,
    /// 0..1 draws a bar along the bottom; none for no bar.
    #[serde(default)]
    pub progress: Option<f64>,
    /// Colour of the value and the bar; none for the notch's own colours.
    #[serde(default)]
    pub color: Option<Color>,
    /// True when a click on the card reaches the plugin as `card.click`.
    #[serde(default)]
    pub clickable: bool,
}

impl Card {
    /// Checks the card. A card needs an id.
    pub fn sanitized(mut self) -> Result<Card, String> {
        if self.id.trim().is_empty() {
            return Err("a card needs an id".into());
        }
        self.progress = self.progress.map(unit_range);
        Ok(self)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn page(value: Value) -> Page {
        serde_json::from_value::<Page>(value).unwrap().sanitized().unwrap()
    }

    #[test]
    fn colours_come_in_four_forms() {
        let violet = GlowColor::VIOLET;
        for text in [json!("Violet"), json!("violet"), json!("#a86bff"), json!({ "r": 168, "g": 107, "b": 255 }), json!([168, 107, 255])] {
            assert_eq!(Color::from_json(&text), Some(Color(violet)), "{text}");
        }
        assert_eq!(Color::from_json(&json!("#fff")), Some(Color(GlowColor::new(255, 255, 255))));
        for bad in [json!("nope"), json!("#12"), json!("#gggggg"), json!({ "r": 300, "g": 0, "b": 0 }), json!(5)] {
            assert_eq!(Color::from_json(&bad), None, "{bad}");
        }
    }

    #[test]
    fn colours_are_sent_as_channels() {
        let text = serde_json::to_value(Color(GlowColor::RED)).unwrap();
        assert_eq!(text, json!({ "r": 255, "g": 59, "b": 59 }));
    }

    #[test]
    fn a_page_is_read_with_defaults() {
        let page = page(json!({ "id": "main", "title": "Main" }));
        assert!(!page.input && !page.back);
        assert!(page.stats.is_empty() && page.blocks.is_empty());
    }

    #[test]
    fn a_page_needs_an_id_and_a_title() {
        for value in [json!({ "id": " ", "title": "X" }), json!({ "id": "x", "title": "" })] {
            assert!(serde_json::from_value::<Page>(value).unwrap().sanitized().is_err());
        }
    }

    #[test]
    fn at_most_eight_stats_and_six_actions_are_kept() {
        let stats: Vec<Value> = (0..12).map(|i| json!({ "label": format!("S{i}") })).collect();
        let actions: Vec<Value> = (0..9).map(|i| json!({ "label": format!("A{i}") })).collect();
        let page = page(json!({ "id": "x", "title": "X", "stats": stats, "actions": actions }));
        assert_eq!(page.stats.len(), MAX_STATS);
        assert_eq!(page.actions.len(), MAX_ACTIONS);
        assert!(page.actions[0].enabled && !page.actions[0].confirm);
    }

    #[test]
    fn every_kind_of_block_is_read() {
        let page = page(json!({ "id": "x", "title": "X", "blocks": [
            { "type": "text", "text": "Hello", "style": "heading", "color": "Green" },
            { "type": "value", "label": "Version", "value": "1.2.0" },
            { "type": "progress", "label": "Disk", "progress": 4 },
            { "type": "table", "columns": ["a", "b"], "rows": [["1", "2", "3"], ["4"]] },
            { "type": "chart", "values": [1, 2, 3], "height": 900 },
            { "type": "buttons", "actions": [{ "label": "Go", "confirm": true }] },
            { "type": "toggle", "label": "On", "value": true, "id": "power" },
            { "type": "slider", "label": "Level", "value": 250 },
            { "type": "select", "label": "Mode", "options": ["a", "b"], "selected": "b" },
            { "type": "textfield", "label": "Name", "secret": true, "submitLabel": "Send" },
            { "type": "image", "data": "iVBORw0KGgo=", "height": 1 },
            { "type": "separator" },
            { "type": "hologram" }
        ]}));
        // The unknown type is skipped; the rest keep their order.
        assert_eq!(page.blocks.len(), 12);
        assert!(matches!(&page.blocks[0].kind, BlockKind::Text { style: TextStyle::Heading, color: Some(_), .. }));
        assert!(matches!(&page.blocks[2].kind, BlockKind::Progress { progress, .. } if *progress == 1.0));
        match &page.blocks[3].kind {
            BlockKind::Table { rows, .. } => assert_eq!(rows[0], ["1", "2"]),
            other => panic!("{other:?}"),
        }
        assert!(matches!(&page.blocks[4].kind, BlockKind::Chart { height, .. } if *height == 240.0));
        assert_eq!(page.blocks[6].id.as_deref(), Some("power"));
        assert!(matches!(&page.blocks[7].kind, BlockKind::Slider { value, max, .. } if *value == 100.0 && *max == 100.0));
        assert!(matches!(&page.blocks[9].kind, BlockKind::TextField { secret: true, .. }));
        assert!(matches!(&page.blocks[10].kind, BlockKind::Image { height, .. } if *height == 20.0));
        assert!(matches!(page.blocks[11].kind, BlockKind::Separator {}));
    }

    #[test]
    fn blocks_that_cannot_be_drawn_are_dropped() {
        let page = page(json!({ "id": "x", "title": "X", "blocks": [
            { "type": "image", "data": "not base64!" },
            { "type": "table", "columns": [] },
            { "type": "slider", "label": "Bad", "min": 5, "max": 5 },
            { "type": "text", "text": "kept" }
        ]}));
        assert_eq!(page.blocks.len(), 1);
    }

    #[test]
    fn tables_and_charts_are_limited() {
        let columns: Vec<String> = (0..12).map(|i| i.to_string()).collect();
        let rows: Vec<Vec<String>> = (0..300).map(|_| vec!["x".to_owned(); 12]).collect();
        let values: Vec<f64> = (0..500).map(f64::from).collect();
        let page = page(json!({ "id": "x", "title": "X", "blocks": [
            { "type": "table", "columns": columns, "rows": rows },
            { "type": "chart", "values": values }
        ]}));
        match &page.blocks[0].kind {
            BlockKind::Table { columns, rows } => {
                assert_eq!(columns.len(), MAX_TABLE_COLUMNS);
                assert_eq!(rows.len(), MAX_TABLE_ROWS);
                assert_eq!(rows[0].len(), MAX_TABLE_COLUMNS);
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(&page.blocks[1].kind, BlockKind::Chart { values, height, .. } if values.len() == MAX_CHART_VALUES && *height == 72.0));
    }

    #[test]
    fn at_most_a_hundred_blocks_are_kept() {
        let blocks: Vec<Value> = (0..150).map(|_| json!({ "type": "separator" })).collect();
        assert_eq!(page(json!({ "id": "x", "title": "X", "blocks": blocks })).blocks.len(), MAX_BLOCKS);
    }

    #[test]
    fn a_text_box_among_the_blocks_takes_input_unless_choices_win() {
        let with_box = json!({ "id": "x", "title": "X", "blocks": [{ "type": "textfield", "label": "L" }] });
        assert!(page(with_box.clone()).takes_input());
        let mut with_choices = with_box;
        with_choices["choices"] = json!([{ "label": "One" }]);
        assert!(!page(with_choices).takes_input());
        assert!(page(json!({ "id": "x", "title": "X", "input": true })).takes_input());
    }

    #[test]
    fn a_card_needs_an_id_and_its_progress_is_clamped() {
        let card: Card = serde_json::from_value(json!({ "id": "c", "label": "L", "progress": 3 })).unwrap();
        assert_eq!(card.sanitized().unwrap().progress, Some(1.0));
        let blank: Card = serde_json::from_value(json!({ "id": " ", "label": "L" })).unwrap();
        assert!(blank.sanitized().is_err());
    }
}
