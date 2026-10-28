//! Where Notch keeps its files. Ported from `src/Notch.Core/Settings/DataFolders.cs`.

use std::path::{Path, PathBuf};

use super::{AppSettings, SettingsStore};

/// Where Notch keeps its files: settings, plugins and their data, logs. A debug build keeps its
/// own, under "Notch-Debug", so running one beside an installed copy never has the two overwrite
/// each other's settings; `--shared-data` makes a debug run use the installed copy's folders.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataFolders {
    /// Settings, plugins and what they keep: `%AppData%\Notch`.
    pub roaming: PathBuf,
    /// Logs, caches and downloads: `%LocalAppData%\Notch`.
    pub local: PathBuf,
    /// The installed copy's roaming folder, which a debug build's first run copies settings from.
    pub release_roaming: PathBuf,
}

impl DataFolders {
    /// Makes a debug build use the installed copy's folders for this run.
    pub const SHARED_FLAG: &str = "--shared-data";

    const RELEASE_NAME: &str = "Notch";
    const DEBUG_NAME: &str = "Notch-Debug";

    /// The folders' name for this build and these command-line arguments.
    pub fn name(debug_build: bool, arguments: &[String]) -> &'static str {
        if debug_build && !arguments.iter().any(|a| a.eq_ignore_ascii_case(Self::SHARED_FLAG)) {
            Self::DEBUG_NAME
        } else {
            Self::RELEASE_NAME
        }
    }

    /// The folders under the given `%AppData%` and `%LocalAppData%`.
    pub fn under(app_data: &Path, local_app_data: &Path, debug_build: bool, arguments: &[String]) -> Self {
        let name = Self::name(debug_build, arguments);
        Self {
            roaming: app_data.join(name),
            local: local_app_data.join(name),
            release_roaming: app_data.join(Self::RELEASE_NAME),
        }
    }

    /// This process's folders: `%AppData%` and `%LocalAppData%` from the environment, the build
    /// kind from how this was compiled, and the command line.
    pub fn current() -> Self {
        let folder = |variable: &str| std::env::var_os(variable).map(PathBuf::from).unwrap_or_default();
        let arguments: Vec<String> = std::env::args().collect();
        Self::under(&folder("APPDATA"), &folder("LOCALAPPDATA"), cfg!(debug_assertions), &arguments)
    }

    pub fn settings_file(&self) -> PathBuf {
        self.roaming.join("settings.json")
    }

    /// A debug build's first run starts from a copy of the installed copy's settings, so it looks
    /// the same. What reaches outside Notch's own folders is left to the installed copy: the copy
    /// has the webhook off (it would want the same port) and leaves the coding agents' hook files
    /// alone (they would be pointed at the debug build). True when a copy was made.
    pub fn seed_settings(&self) -> bool {
        seed_settings(&self.release_roaming.join("settings.json"), &self.settings_file())
    }
}

pub(crate) fn seed_settings(from: &Path, to: &Path) -> bool {
    let same = match (std::path::absolute(from), std::path::absolute(to)) {
        (Ok(a), Ok(b)) => a.to_string_lossy().eq_ignore_ascii_case(&b.to_string_lossy()),
        _ => false,
    };
    if same || to.exists() || !from.exists() {
        return false;
    }

    let mut settings: AppSettings = SettingsStore::new(from).load();
    settings.webhook_enabled = false;
    settings.global_agent_hooks = false;
    SettingsStore::new(to).save(&settings).is_ok()
}
