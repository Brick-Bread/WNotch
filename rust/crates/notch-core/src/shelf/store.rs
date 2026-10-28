//! Ported from `src/Notch.Core/Shelf/ShelfStore.cs`.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::json;
use crate::settings::tmp_path;

/// Loads and saves what is on the [`FileShelf`](super::FileShelf) as a JSON list of paths
/// (`shelf.json`, shared with the C# app). A missing or corrupt file yields an empty shelf.
#[derive(Debug, Clone)]
pub struct ShelfStore {
    file_path: PathBuf,
}

impl ShelfStore {
    pub fn new(file_path: impl Into<PathBuf>) -> Self {
        Self { file_path: file_path.into() }
    }

    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    pub fn load(&self) -> Vec<String> {
        let Ok(text) = fs::read_to_string(&self.file_path) else { return Vec::new() };
        // `File.ReadAllText` drops a byte order mark.
        let text = text.strip_prefix('\u{FEFF}').unwrap_or(&text);
        serde_json::from_str::<Value>(text).ok().and_then(|value| json::string_list(&value).ok()).unwrap_or_default()
    }

    /// Writes the list as indented JSON. The shelf is a convenience: failing to persist it must
    /// not take the app down, so the caller may ignore the error, as C# does.
    pub fn save(&self, paths: &[String]) -> io::Result<()> {
        if let Some(folder) = self.file_path.parent() {
            fs::create_dir_all(folder)?;
        }

        // Write to a temporary file first so a crash cannot leave a half-written shelf.
        let temporary = tmp_path(&self.file_path);
        let text = serde_json::to_string_pretty(&json::to_string_list(paths)).map_err(io::Error::other)?;
        fs::write(&temporary, text)?;
        fs::rename(&temporary, &self.file_path)
    }
}
