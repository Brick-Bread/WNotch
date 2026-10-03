//! The messages between Notch and a plugin process: one JSON object per line on the plugin's
//! standard input (host to plugin) and standard output (plugin to host). Every message has a
//! `type`. See `docs/plugins.md` for the full description.

use std::time::Duration;

use base64::engine::general_purpose::STANDARD;
use base64::Engine;
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

use super::blocks::{Card, Color, Page};
use super::shell::ShellView;
use super::{activity_id_for, PROTOCOL_VERSION};
use crate::activity::{Activity, ActivityTier};
use crate::glow::{Glow, GlowColor, GlowPattern};

/// The longest line the host reads from a plugin. A longer one is dropped, not buffered.
pub const MAX_LINE_BYTES: usize = 8 * 1024 * 1024;

/// How long a notice from [`FromPlugin::Notify`] stays when the plugin does not say.
pub const DEFAULT_NOTICE_LIFETIME: Duration = Duration::from_secs(4);

/// The id of the activity a plugin's notices use (replacing the previous notice).
pub const NOTICE_ID: &str = "notice";

/// How a plugin writes an activity. Shown in the pill like Notch's own.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActivitySpec {
    /// Private to the plugin; Notch prefixes it. Publishing the same id again updates it.
    pub id: String,
    /// Which activity the pill displays is decided by tier, then by which was published most
    /// recently. Transient ones disappear after their lifetime; the other tiers stay until removed.
    #[serde(default)]
    pub tier: ActivityTier,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub detail: Option<String>,
    /// An emoji or short symbol shown before the title.
    #[serde(default)]
    pub glyph: Option<String>,
    /// A picture (PNG or JPEG, base64) shown in place of the glyph.
    #[serde(default)]
    pub image: Option<String>,
    /// 0..1 draws a bar; none for no bar.
    #[serde(default)]
    pub progress: Option<f64>,
    /// The light around the notch while this is on top.
    #[serde(default)]
    pub glow: Option<GlowSpec>,
    /// For transient activities: how long they stay, in milliseconds (default 2000).
    #[serde(default)]
    pub lifetime_ms: Option<u64>,
}

/// A glow as a plugin writes it: just a colour (`"Violet"`, a steady glow), or
/// `{ "color": ..., "pattern": "pulse", "strength": 0.8 }` with the pattern one of
/// `steady`, `breathe`, `pulse`, `flash` and `audio`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GlowSpec(pub Glow);

impl<'de> Deserialize<'de> for GlowSpec {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        use serde::de::Error;
        let value = Value::deserialize(deserializer)?;
        let bad_colour = || D::Error::custom("a glow needs a colour: a name like \"Violet\", \"#rrggbb\", { r, g, b } or [r, g, b]");
        if let Value::Object(map) = &value {
            if let Some(color) = map.get("color") {
                let color = Color::from_json(color).ok_or_else(bad_colour)?;
                let pattern = match map.get("pattern") {
                    None | Some(Value::Null) => GlowPattern::Steady,
                    Some(pattern) => serde_json::from_value(pattern.clone())
                        .map_err(|_| D::Error::custom("a glow's pattern is steady, breathe, pulse, flash or audio"))?,
                };
                let strength = map.get("strength").and_then(Value::as_f64).unwrap_or(1.0).clamp(0.0, 1.0);
                return Ok(GlowSpec(Glow { color: color.0, pattern, strength }));
            }
        }
        let color = Color::from_json(&value).ok_or_else(bad_colour)?;
        Ok(GlowSpec(Glow { color: color.0, pattern: GlowPattern::Steady, strength: 1.0 }))
    }
}

impl ActivitySpec {
    /// The activity as the pill shows it, under the plugin's private id space.
    pub fn into_activity(self, plugin_id: &str) -> Result<Activity, String> {
        if self.id.trim().is_empty() {
            return Err("an activity needs an id".into());
        }
        let image = match self.image {
            Some(data) => Some(STANDARD.decode(data.as_bytes()).map_err(|_| "an activity's image is not valid base64")?),
            None => None,
        };
        Ok(Activity {
            id: activity_id_for(plugin_id, &self.id),
            tier: self.tier,
            title: self.title,
            detail: self.detail,
            glyph: self.glyph,
            image,
            progress: self.progress.map(|p| if p.is_nan() { 0.0 } else { p.clamp(0.0, 1.0) }),
            glow: self.glow.map(|g| g.0),
            lifetime: self.lifetime_ms.map(Duration::from_millis),
        })
    }
}

/// A short notice in the pill: a transient activity without the bookkeeping. It replaces the
/// previous notice from the same plugin.
pub fn notice_activity(
    plugin_id: &str,
    title: &str,
    detail: Option<String>,
    glyph: Option<String>,
    color: Option<GlowColor>,
    lifetime: Option<Duration>,
) -> Activity {
    Activity {
        id: activity_id_for(plugin_id, NOTICE_ID),
        tier: ActivityTier::Transient,
        title: title.to_owned(),
        detail,
        glyph: Some(glyph.unwrap_or_default()),
        glow: color.map(|color| Glow { color, pattern: GlowPattern::Flash, strength: 1.0 }),
        lifetime: Some(lifetime.unwrap_or(DEFAULT_NOTICE_LIFETIME)),
        ..Activity::default()
    }
}

/// Everything a plugin may send to Notch.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(tag = "type")]
pub enum FromPlugin {
    /// The plugin has started and is ready for events. Expected within a few seconds of `hello`.
    #[serde(rename = "ready")]
    Ready {},
    /// A line for the shared plugin log. `level` is `info` (default), `warn` or `error`.
    #[serde(rename = "log")]
    Log {
        #[serde(default)]
        level: Option<String>,
        message: String,
    },
    #[serde(rename = "activity.publish")]
    ActivityPublish { activity: ActivitySpec },
    #[serde(rename = "activity.remove")]
    ActivityRemove { id: String },
    #[serde(rename = "activity.clear")]
    ActivityClear {},
    #[serde(rename = "card.set")]
    CardSet { card: Card },
    #[serde(rename = "card.remove")]
    CardRemove { id: String },
    #[serde(rename = "card.clear")]
    CardClear {},
    /// Adds a page, or replaces the plugin's page with the same id (keeping its console).
    #[serde(rename = "page.set")]
    PageSet { page: Page },
    #[serde(rename = "page.append")]
    PageAppend { page: String, line: String },
    #[serde(rename = "page.clearConsole")]
    PageClearConsole { page: String },
    /// Expands the notch on this page.
    #[serde(rename = "page.open")]
    PageOpen { page: String },
    #[serde(rename = "page.remove")]
    PageRemove { page: String },
    #[serde(rename = "page.clear")]
    PageClear {},
    /// A short notice in the pill.
    #[serde(rename = "notify")]
    Notify {
        title: String,
        #[serde(default)]
        detail: Option<String>,
        #[serde(default)]
        glyph: Option<String>,
        #[serde(default)]
        color: Option<Color>,
        #[serde(default, rename = "lifetimeMs")]
        lifetime_ms: Option<u64>,
    },
    /// Asks for a stored value; answered with a `reply`. Without `key` the answer is every stored value.
    #[serde(rename = "settings.get")]
    SettingsGet {
        id: Value,
        #[serde(default)]
        key: Option<String>,
    },
    #[serde(rename = "settings.set")]
    SettingsSet { key: String, value: Value },
    #[serde(rename = "settings.remove")]
    SettingsRemove { key: String },
    #[serde(rename = "bus.subscribe")]
    BusSubscribe { topic: String },
    #[serde(rename = "bus.unsubscribe")]
    BusUnsubscribe { topic: String },
    #[serde(rename = "bus.publish")]
    BusPublish {
        topic: String,
        #[serde(default)]
        payload: Option<String>,
    },
    /// Asks for the notch's state; answered with a `reply`.
    #[serde(rename = "shell.get")]
    ShellGet { id: Value },
    /// Opens Notch's settings, where the plugin's options are.
    #[serde(rename = "shell.openSettings")]
    ShellOpenSettings {},
}

/// Why a line from a plugin was not turned into a message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParseError {
    /// Not JSON, not an object, or a known message with something wrong in it.
    Invalid(String),
    /// A message type this version of Notch does not know. Ignored, so newer plugins keep working.
    Unknown(String),
}

/// Reads one line from a plugin.
pub fn parse_line(line: &str) -> Result<FromPlugin, ParseError> {
    let value: Value = serde_json::from_str(line).map_err(|e| ParseError::Invalid(format!("not JSON: {e}")))?;
    let kind = match value.get("type").and_then(Value::as_str) {
        Some(kind) => kind.to_owned(),
        None => return Err(ParseError::Invalid("a message needs a \"type\"".into())),
    };
    serde_json::from_value::<FromPlugin>(value).map_err(|e| {
        // serde reports an unknown tag with this wording; every other failure is a bad field.
        if e.to_string().contains("unknown variant") {
            ParseError::Unknown(kind.clone())
        } else {
            ParseError::Invalid(format!("{kind}: {e}"))
        }
    })
}

/// What the user did in the notch, for the plugin that owns the thing they operated.
/// `block` is the block's id, `index` the position of a button, `page` a page id.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum UiEvent {
    /// The user clicked a card that said `clickable`.
    #[serde(rename = "card.click")]
    CardClick { card: String },
    /// The user submitted a line in the page's input.
    #[serde(rename = "page.input")]
    PageInput { page: String, text: String },
    #[serde(rename = "page.back")]
    PageBack { page: String },
    /// The user clicked a row of the page's `choices` (position counted from 0).
    #[serde(rename = "page.choice")]
    PageChoice { page: String, index: usize },
    /// The user clicked a button of the page's `actions` (position counted from 0).
    #[serde(rename = "page.action")]
    PageAction { page: String, index: usize },
    /// The user clicked a button inside a `buttons` block.
    #[serde(rename = "block.button")]
    BlockButton { page: String, block: String, index: usize },
    #[serde(rename = "block.toggle")]
    BlockToggle { page: String, block: String, value: bool },
    #[serde(rename = "block.slider")]
    BlockSlider { page: String, block: String, value: f64 },
    #[serde(rename = "block.select")]
    BlockSelect { page: String, block: String, value: String },
    #[serde(rename = "block.text")]
    BlockText { page: String, block: String, value: String },
}

/// What Notch sends a plugin.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "type")]
pub enum ToPlugin {
    /// The first message, sent as soon as the process starts.
    #[serde(rename = "hello")]
    Hello {
        protocol: u32,
        host: HostInfo,
        plugin: PluginInfoMessage,
        /// Every stored option and setting (`settings.json`), by key.
        options: Map<String, Value>,
        shell: ShellView,
    },
    /// Options the user changed in Notch's settings; only sent to plugins that declare `liveOptions`.
    #[serde(rename = "options")]
    Options { changed: Vec<String>, options: Map<String, Value> },
    /// The notch's state changed.
    #[serde(rename = "shell")]
    Shell { shell: ShellView },
    /// A message another plugin (or this one) published to a topic this plugin subscribed to.
    #[serde(rename = "message")]
    Message { sender: String, topic: String, payload: Option<String> },
    /// The answer to a request carrying `id`.
    #[serde(rename = "reply")]
    Reply {
        id: Value,
        ok: bool,
        #[serde(skip_serializing_if = "Option::is_none")]
        value: Option<Value>,
        #[serde(skip_serializing_if = "Option::is_none")]
        error: Option<String>,
    },
    /// Something the user did. Sent as the event itself, e.g. `{ "type": "card.click", "card": "x" }`.
    #[serde(skip_serializing)]
    Event(UiEvent),
    /// Asks the plugin to finish and exit. It is killed if it takes longer than a few seconds.
    #[serde(rename = "stop")]
    Stop {},
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct HostInfo {
    pub name: &'static str,
    pub version: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginInfoMessage {
    pub id: String,
    pub name: String,
    pub version: Option<String>,
    /// The folder the plugin was installed in. Read-only: updating a plugin replaces it.
    pub dir: String,
    /// A folder for the plugin's own files, kept across plugin and app updates. Created by Notch.
    pub data_dir: String,
}

impl ToPlugin {
    /// The `hello` for a plugin that is starting.
    pub fn hello(host_version: &str, plugin: PluginInfoMessage, options: Map<String, Value>, shell: ShellView) -> ToPlugin {
        ToPlugin::Hello {
            protocol: PROTOCOL_VERSION,
            host: HostInfo { name: "notch", version: host_version.to_owned() },
            plugin,
            options,
            shell,
        }
    }

    pub fn reply_ok(id: Value, value: Value) -> ToPlugin {
        ToPlugin::Reply { id, ok: true, value: Some(value), error: None }
    }

    pub fn reply_error(id: Value, error: impl Into<String>) -> ToPlugin {
        ToPlugin::Reply { id, ok: false, value: None, error: Some(error.into()) }
    }

    /// The line to write to the plugin, without the line break.
    pub fn encode(&self) -> String {
        // Serialising plain data cannot fail; an empty object is the harmless fallback.
        let encoded = match self {
            ToPlugin::Event(event) => serde_json::to_string(event),
            other => serde_json::to_string(other),
        };
        encoded.unwrap_or_else(|_| json!({}).to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(line: &str) -> FromPlugin {
        parse_line(line).unwrap_or_else(|e| panic!("{line}: {e:?}"))
    }

    #[test]
    fn reads_an_activity_and_scopes_its_id() {
        let FromPlugin::ActivityPublish { activity } = parse(
            r##"{"type":"activity.publish","activity":{"id":"reminder","tier":"attention","title":"Take a break","detail":"50 min","glyph":"!","progress":3,"glow":{"color":"Violet","pattern":"pulse"},"lifetimeMs":1500,"image":"iVBORw0KGgo="}}"##,
        ) else {
            panic!("wrong message");
        };
        let activity = activity.into_activity("notch.break").unwrap();
        assert_eq!(activity.id, "plugin.notch.break.reminder");
        assert_eq!(activity.tier, ActivityTier::Attention);
        assert_eq!(activity.progress, Some(1.0));
        assert_eq!(activity.lifetime, Some(Duration::from_millis(1500)));
        assert_eq!(activity.image.as_deref().map(<[u8]>::len), Some(8));
        let glow = activity.glow.unwrap();
        assert_eq!((glow.color, glow.pattern, glow.strength), (GlowColor::VIOLET, GlowPattern::Pulse, 1.0));
    }

    #[test]
    fn a_glow_can_be_just_a_colour() {
        let FromPlugin::ActivityPublish { activity } =
            parse(r##"{"type":"activity.publish","activity":{"id":"a","title":"T","glow":"#ff0000"}}"##)
        else {
            panic!("wrong message");
        };
        let glow = activity.into_activity("p").unwrap().glow.unwrap();
        assert_eq!((glow.color, glow.pattern), (GlowColor::new(255, 0, 0), GlowPattern::Steady));
    }

    #[test]
    fn an_activity_needs_an_id_and_valid_pictures() {
        let FromPlugin::ActivityPublish { activity } = parse(r#"{"type":"activity.publish","activity":{"id":" ","title":"T"}}"#) else {
            panic!("wrong message");
        };
        assert!(activity.into_activity("p").is_err());
        let FromPlugin::ActivityPublish { activity } =
            parse(r#"{"type":"activity.publish","activity":{"id":"a","title":"T","image":"***"}}"#)
        else {
            panic!("wrong message");
        };
        assert!(activity.into_activity("p").is_err());
    }

    #[test]
    fn a_notice_replaces_the_previous_one_and_flashes() {
        let notice = notice_activity("p", "Done", Some("all".into()), None, Some(GlowColor::GREEN), None);
        assert_eq!(notice.id, "plugin.p.notice");
        assert_eq!(notice.tier, ActivityTier::Transient);
        assert_eq!(notice.lifetime, Some(DEFAULT_NOTICE_LIFETIME));
        assert_eq!(notice.glow.map(|g| g.pattern), Some(GlowPattern::Flash));
        assert_eq!(notice.glyph.as_deref(), Some(""));
        assert!(notice_activity("p", "x", None, None, None, Some(Duration::from_secs(9))).glow.is_none());
    }

    #[test]
    fn reads_the_other_messages() {
        assert_eq!(parse(r#"{"type":"ready"}"#), FromPlugin::Ready {});
        assert!(matches!(parse(r#"{"type":"log","message":"hi"}"#), FromPlugin::Log { level: None, .. }));
        assert!(matches!(parse(r#"{"type":"card.set","card":{"id":"c","label":"L"}}"#), FromPlugin::CardSet { .. }));
        assert!(matches!(parse(r#"{"type":"page.set","page":{"id":"p","title":"P"}}"#), FromPlugin::PageSet { .. }));
        assert!(matches!(parse(r#"{"type":"page.append","page":"p","line":"x"}"#), FromPlugin::PageAppend { .. }));
        assert!(matches!(parse(r#"{"type":"notify","title":"T","color":"Red","lifetimeMs":900}"#), FromPlugin::Notify { lifetime_ms: Some(900), .. }));
        assert!(matches!(parse(r#"{"type":"settings.get","id":7,"key":"k"}"#), FromPlugin::SettingsGet { .. }));
        assert!(matches!(parse(r#"{"type":"bus.publish","topic":"t"}"#), FromPlugin::BusPublish { payload: None, .. }));
        assert_eq!(parse(r#"{"type":"shell.openSettings"}"#), FromPlugin::ShellOpenSettings {});
    }

    #[test]
    fn bad_lines_are_told_apart_from_newer_messages() {
        assert!(matches!(parse_line("not json"), Err(ParseError::Invalid(_))));
        assert!(matches!(parse_line("[1]"), Err(ParseError::Invalid(_))));
        assert!(matches!(parse_line(r#"{"id":1}"#), Err(ParseError::Invalid(_))));
        assert!(matches!(parse_line(r#"{"type":"card.set"}"#), Err(ParseError::Invalid(_))));
        assert_eq!(parse_line(r#"{"type":"teleport"}"#), Err(ParseError::Unknown("teleport".into())));
    }

    #[test]
    fn events_are_sent_flat() {
        let event = ToPlugin::Event(UiEvent::BlockToggle { page: "p".into(), block: "power".into(), value: true });
        let value: Value = serde_json::from_str(&event.encode()).unwrap();
        assert_eq!(value, json!({ "type": "block.toggle", "page": "p", "block": "power", "value": true }));

        let click: UiEvent = serde_json::from_value(json!({ "type": "card.click", "card": "status" })).unwrap();
        assert_eq!(click, UiEvent::CardClick { card: "status".into() });
        assert!(serde_json::from_value::<UiEvent>(json!({ "type": "shell.openSettings" })).is_err());
    }

    #[test]
    fn hello_carries_the_plugin_its_folders_and_options() {
        let shell = ShellView { expanded: false, page: None, cards_visible: false, dark: true, accent: None };
        let mut options = Map::new();
        options.insert("intervalMinutes".into(), json!(50));
        let hello = ToPlugin::hello(
            "0.9.0",
            PluginInfoMessage { id: "a.b".into(), name: "B".into(), version: None, dir: "/p".into(), data_dir: "/d".into() },
            options,
            shell,
        );
        let value: Value = serde_json::from_str(&hello.encode()).unwrap();
        assert_eq!(value["type"], "hello");
        assert_eq!(value["protocol"], 1);
        assert_eq!(value["host"]["name"], "notch");
        assert_eq!(value["plugin"]["dataDir"], "/d");
        assert_eq!(value["options"]["intervalMinutes"], 50);
        assert_eq!(value["shell"]["cardsVisible"], false);
    }

    #[test]
    fn replies_carry_the_request_id() {
        let ok: Value = serde_json::from_str(&ToPlugin::reply_ok(json!("r1"), json!(5)).encode()).unwrap();
        assert_eq!(ok, json!({ "type": "reply", "id": "r1", "ok": true, "value": 5 }));
        let error: Value = serde_json::from_str(&ToPlugin::reply_error(json!(2), "no").encode()).unwrap();
        assert_eq!(error, json!({ "type": "reply", "id": 2, "ok": false, "error": "no" }));
    }
}
