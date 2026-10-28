//! The shared fixtures in `tests/fixtures`: inputs and the answers the C# app gives for them.

use std::fs;
use std::path::PathBuf;

use serde_json::Value;

pub fn dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../tests/fixtures")
}

pub fn text(relative: &str) -> String {
    let path = dir().join(relative);
    fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

pub fn json(relative: &str) -> Value {
    serde_json::from_str(&text(relative)).unwrap_or_else(|e| panic!("{relative}: {e}"))
}

/// The files in a fixture folder, by name.
pub fn files(relative: &str) -> Vec<PathBuf> {
    let mut files: Vec<PathBuf> = fs::read_dir(dir().join(relative))
        .unwrap_or_else(|e| panic!("{relative}: {e}"))
        .map(|entry| entry.unwrap().path())
        .collect();
    files.sort();
    files
}
