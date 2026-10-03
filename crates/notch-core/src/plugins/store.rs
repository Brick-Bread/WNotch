//! A plugin's `settings.json`: a flat JSON object, read on first use and rewritten on every change.

use std::fs;
use std::path::PathBuf;
use std::sync::{Mutex, MutexGuard};

use serde_json::{Map, Value};

/// Key/value storage for one plugin. Users may edit the file by hand, so a missing or corrupt
/// file simply yields no values, like the app's own settings.
pub struct PluginStore {
    path: PathBuf,
    values: Mutex<Option<Map<String, Value>>>,
}

impl PluginStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into(), values: Mutex::new(None) }
    }

    fn lock(&self) -> MutexGuard<'_, Option<Map<String, Value>>> {
        self.values.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn load(&self, slot: &mut Option<Map<String, Value>>) {
        if slot.is_none() {
            let loaded = fs::read_to_string(&self.path)
                .ok()
                .and_then(|text| serde_json::from_str::<Value>(&text).ok())
                .and_then(|value| match value {
                    Value::Object(map) => Some(map),
                    _ => None,
                })
                .unwrap_or_default();
            *slot = Some(loaded);
        }
    }

    /// The value stored under `key`, or `None` when it is not set.
    pub fn get(&self, key: &str) -> Option<Value> {
        let mut slot = self.lock();
        self.load(&mut slot);
        slot.as_ref().and_then(|map| map.get(key).cloned())
    }

    /// Every stored value.
    pub fn all(&self) -> Map<String, Value> {
        let mut slot = self.lock();
        self.load(&mut slot);
        slot.clone().unwrap_or_default()
    }

    /// Stores a value and saves the file.
    pub fn set(&self, key: &str, value: Value) {
        let mut slot = self.lock();
        self.load(&mut slot);
        if let Some(map) = slot.as_mut() {
            map.insert(key.to_owned(), value);
            self.save(map);
        }
    }

    /// Deletes a key; false when it was not set.
    pub fn remove(&self, key: &str) -> bool {
        let mut slot = self.lock();
        self.load(&mut slot);
        let Some(map) = slot.as_mut() else { return false };
        if map.remove(key).is_none() {
            return false;
        }
        self.save(map);
        true
    }

    fn save(&self, map: &Map<String, Value>) {
        // The value still applies for this run if saving fails; it just will not survive a restart.
        let Ok(text) = serde_json::to_string_pretty(map) else { return };
        if let Some(folder) = self.path.parent() {
            if fs::create_dir_all(folder).is_err() {
                return;
            }
        }
        // Written to a temporary file first so a crash cannot leave a half-written settings file.
        let temporary = self.path.with_extension("json.tmp");
        if fs::write(&temporary, text).is_ok() && fs::rename(&temporary, &self.path).is_err() {
            let _ = fs::remove_file(&temporary);
        }
    }
}

#[cfg(test)]
pub(crate) mod testing {
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A fresh folder under the system temp dir, deleted on drop.
    pub struct TempDir(pub PathBuf);

    impl TempDir {
        pub fn new(name: &str) -> Self {
            static COUNTER: AtomicU32 = AtomicU32::new(0);
            let path = std::env::temp_dir().join(format!(
                "notch-core-{name}-{}-{}",
                std::process::id(),
                COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            let _ = std::fs::remove_dir_all(&path);
            std::fs::create_dir_all(&path).expect("temp dir");
            Self(path)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::testing::TempDir;
    use super::*;
    use serde_json::json;

    #[test]
    fn values_survive_a_restart_and_tolerate_wrong_types() {
        let dir = TempDir::new("store");
        let path = dir.0.join("data").join("settings.json");

        let store = PluginStore::new(&path);
        assert_eq!(store.get("n"), None);
        store.set("n", json!(5));
        store.set("name", json!("Notch"));

        let again = PluginStore::new(&path);
        assert_eq!(again.get("n"), Some(json!(5)));
        assert_eq!(again.get("name"), Some(json!("Notch")));

        // Hand-edited into something else: the caller sees whatever is there and decides.
        fs::write(&path, r#"{ "n": "five" }"#).unwrap();
        assert_eq!(PluginStore::new(&path).get("n"), Some(json!("five")));

        assert!(again.remove("n"));
        assert!(!again.remove("n"));
        assert_eq!(PluginStore::new(&path).get("name"), Some(json!("Notch")));
    }

    #[test]
    fn a_corrupt_file_yields_no_values() {
        let dir = TempDir::new("store-corrupt");
        let path = dir.0.join("settings.json");
        fs::write(&path, "{ not json").unwrap();
        assert!(PluginStore::new(&path).all().is_empty());
        fs::write(&path, "[1,2]").unwrap();
        assert!(PluginStore::new(&path).all().is_empty());
    }
}
