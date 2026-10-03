//! The options a plugin lists in its `plugin.json` under `"options"`, shown in Notch's settings.

use serde::Serialize;
use serde_json::{Map, Value};

use super::store::PluginStore;

/// How an option is edited and what its stored value looks like.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum OptionType {
    /// One line of text.
    Text,
    /// A number, whole unless `step` says otherwise.
    Number,
    /// A tick box.
    Bool,
    /// Text that is hidden while typing and never shown again. Left empty, it keeps the stored value.
    Secret,
    /// One of the field's `options`.
    Choice,
    /// Several lines of text; stored as a JSON array of strings.
    List,
}

impl OptionType {
    fn parse(text: &str) -> Option<Self> {
        Some(match text.to_ascii_lowercase().as_str() {
            "text" => Self::Text,
            "number" => Self::Number,
            "bool" => Self::Bool,
            "secret" => Self::Secret,
            "choice" => Self::Choice,
            "list" => Self::List,
            _ => return None,
        })
    }
}

/// An option the plugin lists in its manifest. Notch shows it in Settings under the plugin and
/// keeps the value in the plugin's `settings.json`, under `key`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OptionField {
    pub key: String,
    pub label: String,
    #[serde(rename = "type")]
    pub kind: OptionType,
    /// One line under the label.
    pub description: Option<String>,
    /// Greyed text in an empty box.
    pub hint: Option<String>,
    pub min: Option<f64>,
    pub max: Option<f64>,
    /// For numbers: how far the value may move at a time, 1 for whole numbers (the default).
    pub step: f64,
    pub options: Vec<String>,
}

/// Most options a plugin can list.
pub const MAX_OPTIONS: usize = 40;

/// Reads the manifest's `"options"` array. Entries that cannot be used are skipped.
pub fn parse_all(array: Option<&Value>) -> Vec<OptionField> {
    let Some(Value::Array(items)) = array else {
        return Vec::new();
    };
    let mut fields: Vec<OptionField> = Vec::new();
    for item in items.iter().take(MAX_OPTIONS) {
        let Some(key) = text(item, "key") else { continue };
        if fields.iter().any(|f| f.key == key) {
            continue;
        }
        let kind = text(item, "type").and_then(|t| OptionType::parse(&t)).unwrap_or(OptionType::Text);
        let options: Vec<String> = match item.get("options") {
            Some(Value::Array(list)) => list.iter().filter_map(|x| x.as_str().map(str::to_owned)).collect(),
            _ => Vec::new(),
        };
        if kind == OptionType::Choice && options.is_empty() {
            continue;
        }
        fields.push(OptionField {
            label: text(item, "label").unwrap_or_else(|| key.clone()),
            key,
            kind,
            description: text(item, "description"),
            hint: text(item, "hint"),
            min: number(item, "min"),
            max: number(item, "max"),
            step: number(item, "step").filter(|s| *s > 0.0).unwrap_or(1.0),
            options,
        });
    }
    fields
}

fn text(item: &Value, name: &str) -> Option<String> {
    let value = item.get(name)?.as_str()?.trim();
    (!value.is_empty()).then(|| value.to_owned())
}

fn number(item: &Value, name: &str) -> Option<f64> {
    item.get(name)?.as_f64()
}

/// The current value of each declared option, for the Settings panel. Secrets are never
/// returned, and options that are not set are missing from the result.
pub fn setting_values(store: &PluginStore, fields: &[OptionField]) -> Map<String, Value> {
    let mut values = Map::new();
    for field in fields.iter().filter(|f| f.kind != OptionType::Secret) {
        if let Some(value) = store.get(&field.key) {
            values.insert(field.key.clone(), value);
        }
    }
    values
}

/// Saves the options the user changed, ignoring keys the manifest does not list. Returns the
/// keys that were stored, in the order given.
pub fn apply_user_values(store: &PluginStore, fields: &[OptionField], values: &Map<String, Value>) -> Vec<String> {
    let mut changed = Vec::new();
    for (key, value) in values {
        if fields.iter().any(|f| &f.key == key) {
            store.set(key, value.clone());
            changed.push(key.clone());
        }
    }
    changed
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn sample() -> Value {
        json!([
            { "key": "url", "label": "Address", "hint": "https://..." },
            { "key": "poll", "type": "number", "min": 5, "max": 60, "step": 5 },
            { "key": "key", "type": "SECRET" },
            { "key": "mode", "type": "choice", "options": ["a", "b"] },
            { "key": "pick", "type": "choice" },
            { "key": "names", "type": "list" },
            { "key": "on", "type": "bool", "description": "Switch it on" },
            { "key": "url" },
            { "label": "no key" },
            "junk"
        ])
    }

    #[test]
    fn lists_options_and_skips_the_unusable_ones() {
        let fields = parse_all(Some(&sample()));
        let keys: Vec<&str> = fields.iter().map(|f| f.key.as_str()).collect();
        // "pick" is a choice without options, the second "url" a duplicate.
        assert_eq!(keys, ["url", "poll", "key", "mode", "names", "on"]);
        assert_eq!(fields[1].kind, OptionType::Number);
        assert_eq!((fields[1].min, fields[1].max, fields[1].step), (Some(5.0), Some(60.0), 5.0));
        assert_eq!(fields[0].label, "Address");
        assert_eq!(fields[2].label, "key");
        assert_eq!(fields[2].kind, OptionType::Secret);
        assert_eq!(fields[0].step, 1.0);
    }

    #[test]
    fn anything_but_an_array_is_no_options() {
        assert!(parse_all(None).is_empty());
        assert!(parse_all(Some(&json!({ "key": "x" }))).is_empty());
    }

    #[test]
    fn at_most_forty_options_are_read() {
        let many: Vec<Value> = (0..60).map(|i| json!({ "key": format!("k{i}") })).collect();
        assert_eq!(parse_all(Some(&Value::Array(many))).len(), MAX_OPTIONS);
    }

    #[test]
    fn user_values_are_stored_and_unlisted_keys_ignored() {
        let dir = crate::plugins::store::testing::TempDir::new("options");
        let store = PluginStore::new(dir.0.join("settings.json"));
        let fields = parse_all(Some(&sample()));
        let mut values = Map::new();
        values.insert("poll".into(), json!(30));
        values.insert("key".into(), json!("s3cret"));
        values.insert("sneaky".into(), json!(true));

        let changed = apply_user_values(&store, &fields, &values);

        assert_eq!(changed, ["poll", "key"]);
        assert_eq!(store.get("poll"), Some(json!(30)));
        assert_eq!(store.get("sneaky"), None);
    }

    #[test]
    fn secrets_are_never_handed_back_to_the_settings_panel() {
        let dir = crate::plugins::store::testing::TempDir::new("options-secret");
        let store = PluginStore::new(dir.0.join("settings.json"));
        let fields = parse_all(Some(&sample()));
        store.set("key", json!("s3cret"));
        store.set("url", json!("https://x"));

        let shown = setting_values(&store, &fields);

        assert_eq!(shown.get("url"), Some(&json!("https://x")));
        assert!(!shown.contains_key("key"));
        assert!(!shown.contains_key("poll"));
    }
}
