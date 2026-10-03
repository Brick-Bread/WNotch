//! Out-of-process plugins: the manifest, the JSON-lines protocol, the boards that hold what
//! plugins show, the installer and the bookkeeping of which plugins exist and run.
//!
//! Everything here is plain logic with no processes, windows or network, so it is unit-tested.
//! `src-tauri/src/plugins.rs` supplies the processes and the UI glue. The protocol is documented
//! for plugin authors in `docs/plugins.md`.

pub mod blocks;
pub mod bus;
pub mod catalog;
pub mod install;
pub mod json;
pub mod log;
pub mod manifest;
pub mod options;
pub mod pages;
pub mod protocol;
pub mod shell;
pub mod source;
pub mod store;
pub mod supervise;

use std::fmt;

/// The version of the host/plugin protocol this build of Notch speaks. A plugin states the
/// version it was written for in its manifest (`protocol`); Notch runs plugins written for this
/// version or an older one, and refuses those that need a newer one.
pub const PROTOCOL_VERSION: u32 = 1;

/// A plugin could not be read, installed or used. The message is meant for the user.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginError(pub String);

impl PluginError {
    pub fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

impl fmt::Display for PluginError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for PluginError {}

/// The id a plugin's activity gets in the activity manager: private to the plugin, so it cannot
/// collide with the app's own activities or another plugin's.
pub fn activity_id_for(plugin_id: &str, activity_id: &str) -> String {
    format!("plugin.{plugin_id}.{activity_id}")
}
