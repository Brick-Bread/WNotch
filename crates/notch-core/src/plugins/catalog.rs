//! Which plugins exist on disk and which of them should be running. The catalog decides; the
//! app starts and stops the processes and reports back with `mark_*`.

use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use serde::Serialize;

use super::manifest::{Manifest, FILE_NAME};
use super::options::OptionField;
use super::source::Origin;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    /// Installed but not switched on; none of its code has run.
    Disabled,
    Running,
    /// Switched on, but starting it failed or it kept crashing. See [`PluginInfo::error`].
    Failed,
    /// Its folder does not hold a usable plugin. See [`PluginInfo::error`].
    Invalid,
}

/// A plugin as listed in settings.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginInfo {
    /// None when the manifest could not be read.
    pub id: Option<String>,
    /// The manifest's name, or the folder name when there is no usable manifest.
    pub name: String,
    pub version: Option<String>,
    pub author: Option<String>,
    pub description: Option<String>,
    pub directory: String,
    pub status: Status,
    pub error: Option<String>,
    /// Started with `--plugin=`, so it runs regardless of the user's settings.
    pub always_enabled: bool,
    /// `owner/repo` to ask about updates, when known.
    pub repository: Option<String>,
    /// The release this copy was installed from.
    pub installed_tag: Option<String>,
    pub options: Vec<OptionField>,
}

/// What the app must do to match the user's choice.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Plan {
    Start(String),
    Stop(String),
}

struct Slot {
    directory: PathBuf,
    pinned: bool,
    manifest: Option<Manifest>,
    status: Status,
    error: Option<String>,
}

impl Slot {
    fn info(&self) -> PluginInfo {
        let manifest = self.manifest.as_ref();
        let origin = Origin::read(&self.directory);
        PluginInfo {
            id: manifest.map(|m| m.id.clone()),
            name: manifest.map_or_else(
                || self.directory.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
                |m| m.name.clone(),
            ),
            version: manifest.and_then(|m| m.version.clone()),
            author: manifest.and_then(|m| m.author.clone()),
            description: manifest.and_then(|m| m.description.clone()),
            directory: self.directory.to_string_lossy().into_owned(),
            status: self.status,
            error: self.error.clone(),
            always_enabled: self.pinned,
            repository: Origin::source_of(&self.directory, manifest).map(|s| s.to_string()),
            installed_tag: origin.map(|o| o.tag),
            options: manifest.map(|m| m.options.clone()).unwrap_or_default(),
        }
    }
}

/// Finds plugins on disk. Nothing in a plugin runs until the user has enabled it.
pub struct Catalog {
    plugins_directory: PathBuf,
    slots: Vec<Slot>,
    pinned: HashSet<PathBuf>,
}

impl Catalog {
    /// `plugins_directory`: each subfolder with a `plugin.json` is a plugin. Need not exist.
    pub fn new(plugins_directory: impl Into<PathBuf>) -> Self {
        Self { plugins_directory: plugins_directory.into(), slots: Vec::new(), pinned: HashSet::new() }
    }

    pub fn plugins_directory(&self) -> &Path {
        &self.plugins_directory
    }

    /// Reads the manifests of plugins that are not running, so new and replaced ones show up;
    /// plugins that are running or failed are left alone. This only lists them: the app starts
    /// them after [`Catalog::set_enabled`]. Returns lines for the log about folders that cannot be used.
    ///
    /// `pinned` are plugin folders outside the plugins directory that run without being enabled (`--plugin=`).
    pub fn discover(&mut self, pinned: &[PathBuf]) -> Vec<String> {
        // Forget every folder nothing is running from: it may have been fixed, replaced by a
        // newer version, or deleted since it was last read.
        self.slots.retain(|s| !matches!(s.status, Status::Invalid | Status::Disabled));

        self.pinned.extend(pinned.iter().map(|p| absolute(p)));
        let mut log = Vec::new();
        let pinned_now: Vec<PathBuf> = self.pinned.iter().cloned().collect();
        for directory in pinned_now {
            self.add(directory, true, &mut log);
        }
        for directory in subdirectories(&self.plugins_directory) {
            self.add(directory, false, &mut log);
        }
        log
    }

    fn add(&mut self, directory: PathBuf, pinned: bool, log: &mut Vec<String>) {
        if self.slots.iter().any(|s| same_path(&s.directory, &directory)) {
            return;
        }
        let mut slot = Slot { directory: directory.clone(), pinned, manifest: None, status: Status::Disabled, error: None };
        let manifest_path = directory.join(FILE_NAME);
        let outcome = if manifest_path.is_file() {
            fs::read_to_string(&manifest_path)
                .map_err(|e| e.to_string())
                .and_then(|text| Manifest::parse(&text).map_err(|e| e.0))
        } else if pinned {
            Err(format!("There is no {FILE_NAME} in this folder."))
        } else {
            // Any other folder in the plugins directory is simply not a plugin.
            return;
        };

        match outcome {
            Ok(manifest) => match self.slots.iter().find(|s| s.manifest.as_ref().is_some_and(|m| m.id == manifest.id)) {
                Some(other) => {
                    let message = format!("Its id '{}' is already used by the plugin in {}.", manifest.id, other.directory.display());
                    slot.status = Status::Invalid;
                    slot.error = Some(message);
                }
                None => slot.manifest = Some(manifest),
            },
            Err(message) => {
                slot.status = Status::Invalid;
                slot.error = Some(message);
            }
        }
        if let (Status::Invalid, Some(error)) = (slot.status, &slot.error) {
            log.push(format!("Ignoring the plugin in {}: {error}", directory.display()));
        }
        self.slots.push(slot);
    }

    /// Matches the listed ids: returns the plugins to start (switched on, not running) and to
    /// stop (running, no longer wanted). Pinned plugins are always wanted.
    pub fn set_enabled(&mut self, plugin_ids: &[String]) -> Vec<Plan> {
        let enabled: HashSet<&str> = plugin_ids.iter().map(String::as_str).collect();
        let mut plans = Vec::new();
        for slot in &mut self.slots {
            let Some(manifest) = &slot.manifest else { continue };
            let wanted = slot.pinned || enabled.contains(manifest.id.as_str());
            match slot.status {
                Status::Disabled if wanted => plans.push(Plan::Start(manifest.id.clone())),
                Status::Running if !wanted => plans.push(Plan::Stop(manifest.id.clone())),
                // Switching a failed plugin off and on again is how the user retries it.
                Status::Failed if !wanted => {
                    slot.status = Status::Disabled;
                    slot.error = None;
                }
                _ => {}
            }
        }
        plans
    }

    fn slot_mut(&mut self, plugin_id: &str) -> Option<&mut Slot> {
        self.slots.iter_mut().find(|s| s.manifest.as_ref().is_some_and(|m| m.id == plugin_id))
    }

    pub fn mark_running(&mut self, plugin_id: &str) {
        if let Some(slot) = self.slot_mut(plugin_id) {
            slot.status = Status::Running;
            slot.error = None;
        }
    }

    pub fn mark_failed(&mut self, plugin_id: &str, error: &str) {
        if let Some(slot) = self.slot_mut(plugin_id) {
            slot.status = Status::Failed;
            slot.error = Some(error.to_owned());
        }
    }

    pub fn mark_stopped(&mut self, plugin_id: &str) {
        if let Some(slot) = self.slot_mut(plugin_id) {
            slot.status = Status::Disabled;
            slot.error = None;
        }
    }

    /// The manifest and folder of a listed plugin.
    pub fn find(&self, plugin_id: &str) -> Option<(&Manifest, &Path)> {
        self.slots
            .iter()
            .find_map(|s| s.manifest.as_ref().filter(|m| m.id == plugin_id).map(|m| (m, s.directory.as_path())))
    }

    pub fn status_of(&self, plugin_id: &str) -> Option<Status> {
        self.slots.iter().find(|s| s.manifest.as_ref().is_some_and(|m| m.id == plugin_id)).map(|s| s.status)
    }

    pub fn is_pinned(&self, plugin_id: &str) -> bool {
        self.slots.iter().any(|s| s.pinned && s.manifest.as_ref().is_some_and(|m| m.id == plugin_id))
    }

    /// Ids of the plugins with a live process.
    pub fn running_ids(&self) -> Vec<String> {
        self.slots
            .iter()
            .filter(|s| s.status == Status::Running)
            .filter_map(|s| s.manifest.as_ref().map(|m| m.id.clone()))
            .collect()
    }

    /// The plugins as listed in settings, ordered by folder.
    pub fn infos(&self) -> Vec<PluginInfo> {
        let mut slots: Vec<&Slot> = self.slots.iter().collect();
        slots.sort_by_key(|s| s.directory.to_string_lossy().to_lowercase());
        slots.into_iter().map(Slot::info).collect()
    }
}

fn absolute(path: &Path) -> PathBuf {
    std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf())
}

fn same_path(a: &Path, b: &Path) -> bool {
    a.to_string_lossy().eq_ignore_ascii_case(&b.to_string_lossy())
}

/// Names starting with a dot are the installer's work folders.
fn subdirectories(directory: &Path) -> Vec<PathBuf> {
    let Ok(entries) = fs::read_dir(directory) else { return Vec::new() };
    let mut folders: Vec<PathBuf> = entries
        .filter_map(Result::ok)
        .filter(|e| !e.file_name().to_string_lossy().starts_with('.'))
        .map(|e| e.path())
        .filter(|p| p.is_dir())
        .collect();
    folders.sort_by_key(|p| p.to_string_lossy().to_lowercase());
    folders
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::store::testing::TempDir;

    fn manifest(id: &str) -> String {
        format!(r#"{{ "id": "{id}", "name": "Plugin {id}", "protocol": 1, "entry": ["{id}"] }}"#)
    }

    fn plugin(root: &Path, folder: &str, id: &str) -> PathBuf {
        let directory = root.join(folder);
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join(FILE_NAME), manifest(id)).unwrap();
        directory
    }

    fn ids(values: &[&str]) -> Vec<String> {
        values.iter().map(|v| (*v).to_owned()).collect()
    }

    #[test]
    fn installed_plugins_do_not_run_until_enabled() {
        let root = TempDir::new("catalog");
        plugin(&root.0, "one", "one");
        plugin(&root.0, "two", "two");
        let mut catalog = Catalog::new(&root.0);
        catalog.discover(&[]);

        let infos = catalog.infos();
        assert_eq!(infos.len(), 2);
        assert!(infos.iter().all(|i| i.status == Status::Disabled));

        assert_eq!(catalog.set_enabled(&ids(&["two"])), [Plan::Start("two".into())]);
        catalog.mark_running("two");
        assert_eq!(catalog.status_of("two"), Some(Status::Running));
        assert_eq!(catalog.running_ids(), ["two"]);

        // Already running: nothing to do. Switched off: stop it.
        assert!(catalog.set_enabled(&ids(&["two"])).is_empty());
        assert_eq!(catalog.set_enabled(&ids(&[])), [Plan::Stop("two".into())]);
        catalog.mark_stopped("two");
        assert_eq!(catalog.status_of("two"), Some(Status::Disabled));
    }

    #[test]
    fn a_failed_plugin_is_retried_by_switching_it_off_and_on() {
        let root = TempDir::new("catalog-failed");
        plugin(&root.0, "one", "one");
        let mut catalog = Catalog::new(&root.0);
        catalog.discover(&[]);
        catalog.set_enabled(&ids(&["one"]));
        catalog.mark_failed("one", "it crashed");

        let failed = &catalog.infos()[0];
        assert_eq!((failed.status, failed.error.as_deref()), (Status::Failed, Some("it crashed")));
        assert!(catalog.set_enabled(&ids(&["one"])).is_empty());

        assert!(catalog.set_enabled(&ids(&[])).is_empty());
        assert_eq!(catalog.status_of("one"), Some(Status::Disabled));
        assert_eq!(catalog.set_enabled(&ids(&["one"])), [Plan::Start("one".into())]);
    }

    #[test]
    fn unusable_folders_are_listed_with_the_reason() {
        let root = TempDir::new("catalog-invalid");
        let broken = root.0.join("broken");
        fs::create_dir_all(&broken).unwrap();
        fs::write(broken.join(FILE_NAME), "{ not json").unwrap();
        plugin(&root.0, "first", "same");
        plugin(&root.0, "second", "same");
        fs::create_dir_all(root.0.join("not-a-plugin")).unwrap();
        fs::create_dir_all(root.0.join(".installing-x")).unwrap();
        let mut catalog = Catalog::new(&root.0);

        let log = catalog.discover(&[]);

        let infos = catalog.infos();
        let names: Vec<&str> = infos.iter().map(|i| i.name.as_str()).collect();
        assert_eq!(names, ["broken", "Plugin same", "second"]);
        assert_eq!(infos[0].status, Status::Invalid);
        assert!(infos[0].error.as_deref().unwrap().contains("could not be read"));
        assert!(infos[2].error.as_deref().unwrap().contains("already used"));
        assert_eq!(infos[2].id, None);
        assert_eq!(log.len(), 2);
    }

    #[test]
    fn discovering_again_finds_new_plugins_and_leaves_running_ones_alone() {
        let root = TempDir::new("catalog-again");
        let one = plugin(&root.0, "one", "one");
        let mut catalog = Catalog::new(&root.0);
        catalog.discover(&[]);
        catalog.set_enabled(&ids(&["one"]));
        catalog.mark_running("one");

        // Replaced on disk while running: the running copy keeps its manifest.
        fs::write(one.join(FILE_NAME), manifest("one").replace("Plugin one", "Renamed")).unwrap();
        plugin(&root.0, "two", "two");
        catalog.discover(&[]);

        let infos = catalog.infos();
        assert_eq!(infos.len(), 2);
        assert_eq!(infos[0].name, "Plugin one");
        assert_eq!(infos[0].status, Status::Running);

        // Switched off, it is read again.
        catalog.mark_stopped("one");
        catalog.discover(&[]);
        assert_eq!(catalog.infos()[0].name, "Renamed");
    }

    #[test]
    fn pinned_plugins_run_without_being_enabled() {
        let root = TempDir::new("catalog-pinned");
        let elsewhere = TempDir::new("catalog-dev");
        let dev = plugin(&elsewhere.0, "build-output", "dev");
        let mut catalog = Catalog::new(root.0.join("plugins"));

        catalog.discover(&[dev.clone()]);

        assert!(catalog.infos()[0].always_enabled);
        assert!(catalog.is_pinned("dev"));
        assert_eq!(catalog.set_enabled(&[]), [Plan::Start("dev".into())]);
        catalog.mark_running("dev");
        assert!(catalog.set_enabled(&[]).is_empty());

        // Pinned again on the next discovery, even without being passed again.
        catalog.mark_stopped("dev");
        catalog.discover(&[]);
        assert_eq!(catalog.infos().len(), 1);
    }

    #[test]
    fn a_pinned_folder_without_a_manifest_is_reported() {
        let elsewhere = TempDir::new("catalog-empty-dev");
        let mut catalog = Catalog::new(elsewhere.0.join("plugins"));
        catalog.discover(&[elsewhere.0.clone()]);
        let info = &catalog.infos()[0];
        assert_eq!(info.status, Status::Invalid);
        assert!(info.error.as_deref().unwrap().contains("no plugin.json"));
    }

    #[test]
    fn the_listing_carries_options_and_the_repository() {
        let root = TempDir::new("catalog-info");
        let directory = root.0.join("one");
        fs::create_dir_all(&directory).unwrap();
        fs::write(
            directory.join(FILE_NAME),
            r#"{ "id": "one", "name": "One", "protocol": 1, "entry": ["one"], "repository": "acme/one",
                 "options": [ { "key": "a", "type": "bool" } ] }"#,
        )
        .unwrap();
        let mut catalog = Catalog::new(&root.0);
        catalog.discover(&[]);

        let info = &catalog.infos()[0];
        assert_eq!(info.repository.as_deref(), Some("acme/one"));
        assert_eq!(info.options.len(), 1);
        assert_eq!(info.installed_tag, None);
    }
}
