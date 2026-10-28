//! Ported from `src/Notch.Core/Clipboard/ClipboardHistory.cs`.

use std::fs;
use std::io;
use std::path::Path;

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use chrono::{DateTime, Local, NaiveDateTime, Utc};
use serde_json::{Map, Value};

use crate::automation::parser::utf16_len;
use crate::json::{self, Mismatch, Read};
use crate::settings::tmp_path;
use crate::shelf::file_shelf::same_ignoring_case;

/// Stored by number in `clipboard-pins.json`, as System.Text.Json stores an enum by default.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClipboardKind {
    Text,
    Image,
    Files,
    /// A number in the file that is none of the kinds. C# keeps it and treats it like an image
    /// wherever it switches on the kind.
    Unknown(i32),
}

impl ClipboardKind {
    pub fn from_number(number: i32) -> Self {
        match number {
            0 => Self::Text,
            1 => Self::Image,
            2 => Self::Files,
            other => Self::Unknown(other),
        }
    }

    pub fn number(self) -> i32 {
        match self {
            Self::Text => 0,
            Self::Image => 1,
            Self::Files => 2,
            Self::Unknown(other) => other,
        }
    }
}

/// One thing that was copied.
#[derive(Debug, Clone, PartialEq)]
pub struct ClipboardItem {
    pub id: i64,
    pub kind: ClipboardKind,
    pub copied_at: DateTime<Utc>,
    /// The text, for [`ClipboardKind::Text`].
    pub text: Option<String>,
    /// PNG bytes, for [`ClipboardKind::Image`].
    pub image: Option<Vec<u8>>,
    /// Full paths, for [`ClipboardKind::Files`].
    pub files: Option<Vec<String>>,
    /// The name of the program it was copied from, when known.
    pub source: Option<String>,
    pub pinned: bool,
}

impl ClipboardItem {
    pub fn new(id: i64, kind: ClipboardKind, copied_at: DateTime<Utc>) -> Self {
        Self { id, kind, copied_at, text: None, image: None, files: None, source: None, pinned: false }
    }

    /// One line for the list: the first line of the text, the file names, or the picture's size.
    pub fn preview(&self) -> String {
        match self.kind {
            ClipboardKind::Text => first_line(self.text.as_deref().unwrap_or("")),
            ClipboardKind::Files => self
                .files
                .iter()
                .flatten()
                .map(|f| match file_name(f.trim_end_matches(['\\', '/'])) {
                    name if !name.is_empty() => name,
                    _ => f,
                })
                .collect::<Vec<_>>()
                .join(", "),
            _ => format!("Picture ({} KB)", self.image.as_ref().map_or(0, Vec::len) / 1024),
        }
    }

    /// Whether two items hold the same thing, so copying it again just moves it to the top.
    pub fn same_content_as(&self, other: &Self) -> bool {
        self.kind == other.kind
            && match self.kind {
                ClipboardKind::Text => self.text == other.text,
                ClipboardKind::Files => {
                    let (mine, theirs) = (self.files.as_deref().unwrap_or(&[]), other.files.as_deref().unwrap_or(&[]));
                    mine.len() == theirs.len() && mine.iter().zip(theirs).all(|(a, b)| same_ignoring_case(a, b))
                }
                _ => self.image.as_deref().unwrap_or(&[]) == other.image.as_deref().unwrap_or(&[]),
            }
    }

    /// The item as System.Text.Json writes the C# record, `Preview` included.
    fn to_json(&self) -> Value {
        let mut map = Map::new();
        map.insert("Id".into(), self.id.into());
        map.insert("Kind".into(), self.kind.number().into());
        map.insert("CopiedAt".into(), write_date_time(self.copied_at).into());
        map.insert("Text".into(), json::to_optional_string(self.text.as_deref()));
        map.insert("Image".into(), self.image.as_ref().map_or(Value::Null, |png| BASE64.encode(png).into()));
        map.insert("Files".into(), self.files.as_deref().map_or(Value::Null, json::to_string_list));
        map.insert("Source".into(), json::to_optional_string(self.source.as_deref()));
        map.insert("Pinned".into(), self.pinned.into());
        map.insert("Preview".into(), self.preview().into());
        Value::Object(map)
    }

    fn read(value: &Value) -> Read<Self> {
        let Value::Object(map) = value else { return Err(Mismatch) };
        // Missing properties take the C# constructor's defaults; `Preview` is computed.
        let mut item = Self::new(0, ClipboardKind::Text, min_date_time());
        for (key, value) in map {
            match key.as_str() {
                "Id" => item.id = value.as_i64().ok_or(Mismatch)?,
                "Kind" => item.kind = ClipboardKind::from_number(json::int(value)?),
                "CopiedAt" => item.copied_at = read_date_time(value)?,
                "Text" => item.text = json::optional_string(value)?,
                "Image" => {
                    item.image = match json::optional_string(value)? {
                        Some(text) => Some(BASE64.decode(text).map_err(|_| Mismatch)?),
                        None => None,
                    }
                }
                "Files" => item.files = if value.is_null() { None } else { Some(json::string_list(value)?) },
                "Source" => item.source = json::optional_string(value)?,
                "Pinned" => item.pinned = json::bool(value)?,
                _ => {}
            }
        }
        Ok(item)
    }
}

pub const DEFAULT_CAPACITY: usize = 50;
pub const MAX_TEXT_LENGTH: usize = 100_000;
pub const MAX_IMAGE_BYTES: usize = 4 * 1024 * 1024;
pub const MAX_FILES: usize = 50;

/// What was copied lately, newest first. Kept in memory only: the history is gone when Notch
/// quits, except for what the user pinned, which [`pins`] saves.
///
/// In place of the C# `Changed` event, each method that can change the history says whether it
/// did: the `add_*` methods by returning the stored item, the others by returning `true`.
/// [`Self::restore`] always counts as a change, as in C#. The C# lock is the caller's `Mutex`, if
/// it needs one.
#[derive(Debug, Clone)]
pub struct ClipboardHistory {
    items: Vec<ClipboardItem>,
    capacity: usize,
    next_id: i64,
}

impl Default for ClipboardHistory {
    fn default() -> Self {
        Self::new(DEFAULT_CAPACITY)
    }
}

impl ClipboardHistory {
    pub fn new(capacity: usize) -> Self {
        Self { items: Vec::new(), capacity: capacity.max(1), next_id: 1 }
    }

    /// Adds what was copied. Returns the stored item, or `None` when it is not worth keeping
    /// (empty, or too large).
    pub fn add_text(&mut self, text: &str, now: DateTime<Utc>, source: Option<&str>) -> Option<ClipboardItem> {
        if text.trim().is_empty() || utf16_len(text) > MAX_TEXT_LENGTH {
            return None;
        }

        Some(self.add(ClipboardItem {
            text: Some(text.to_owned()),
            source: source.map(str::to_owned),
            ..ClipboardItem::new(0, ClipboardKind::Text, now)
        }))
    }

    pub fn add_image(&mut self, png: &[u8], now: DateTime<Utc>, source: Option<&str>) -> Option<ClipboardItem> {
        if png.is_empty() || png.len() > MAX_IMAGE_BYTES {
            return None;
        }

        Some(self.add(ClipboardItem {
            image: Some(png.to_vec()),
            source: source.map(str::to_owned),
            ..ClipboardItem::new(0, ClipboardKind::Image, now)
        }))
    }

    pub fn add_files<I, S>(&mut self, paths: I, now: DateTime<Utc>, source: Option<&str>) -> Option<ClipboardItem>
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let files: Vec<String> = paths
            .into_iter()
            .filter(|p| !p.as_ref().trim().is_empty())
            .take(MAX_FILES)
            .map(|p| p.as_ref().to_owned())
            .collect();
        if files.is_empty() {
            return None;
        }

        Some(self.add(ClipboardItem {
            files: Some(files),
            source: source.map(str::to_owned),
            ..ClipboardItem::new(0, ClipboardKind::Files, now)
        }))
    }

    /// Puts items the user pinned in an earlier run back, below nothing newer.
    pub fn restore(&mut self, pinned: impl IntoIterator<Item = ClipboardItem>) {
        for item in pinned {
            let id = self.next_id;
            self.next_id += 1;
            self.items.push(ClipboardItem { id, pinned: true, ..item });
        }
    }

    pub fn set_pinned(&mut self, id: i64, pinned: bool) -> bool {
        let Some(item) = self.items.iter_mut().find(|i| i.id == id) else { return false };
        if item.pinned == pinned {
            return false;
        }

        item.pinned = pinned;
        self.trim();
        true
    }

    pub fn remove(&mut self, id: i64) -> bool {
        let before = self.items.len();
        self.items.retain(|i| i.id != id);
        self.items.len() != before
    }

    /// Forgets everything except pinned items, or everything when `include_pinned`.
    pub fn clear(&mut self, include_pinned: bool) -> bool {
        let before = self.items.len();
        self.items.retain(|i| !include_pinned && i.pinned);
        self.items.len() != before
    }

    /// Pinned items first, then the rest newest first. A blank `filter` matches everything.
    pub fn snapshot(&self, filter: Option<&str>) -> Vec<ClipboardItem> {
        let needle = filter.map(str::trim).filter(|f| !f.is_empty());
        let mut items: Vec<ClipboardItem> =
            self.items.iter().filter(|i| needle.is_none_or(|n| matches(i, n))).cloned().collect();
        items.sort_by(|a, b| b.pinned.cmp(&a.pinned).then(b.copied_at.cmp(&a.copied_at)).then(b.id.cmp(&a.id)));
        items
    }

    pub fn find(&self, id: i64) -> Option<&ClipboardItem> {
        self.items.iter().find(|i| i.id == id)
    }

    fn add(&mut self, item: ClipboardItem) -> ClipboardItem {
        // Copying the same thing again moves it up (and keeps it pinned) instead of listing it twice.
        let stored = match self.items.iter().position(|i| i.same_content_as(&item)) {
            Some(index) => {
                let same = self.items.remove(index);
                ClipboardItem { copied_at: item.copied_at, source: item.source.or(same.source.clone()), ..same }
            }
            None => {
                let id = self.next_id;
                self.next_id += 1;
                ClipboardItem { id, ..item }
            }
        };

        self.items.push(stored.clone());
        self.trim();
        stored
    }

    /// Drops the oldest unpinned items beyond the capacity.
    fn trim(&mut self) {
        while self.items.iter().filter(|i| !i.pinned).count() > self.capacity {
            let oldest = self
                .items
                .iter()
                .enumerate()
                .filter(|(_, i)| !i.pinned)
                .min_by(|(_, a), (_, b)| a.copied_at.cmp(&b.copied_at).then(a.id.cmp(&b.id)))
                .map(|(index, _)| index);
            match oldest {
                Some(index) => self.items.remove(index),
                None => break,
            };
        }
    }
}

fn matches(item: &ClipboardItem, needle: &str) -> bool {
    match item.kind {
        ClipboardKind::Text => contains_ignoring_case(item.text.as_deref().unwrap_or(""), needle),
        ClipboardKind::Files => item.files.iter().flatten().any(|f| contains_ignoring_case(f, needle)),
        _ => contains_ignoring_case("picture image", needle),
    }
}

/// `string.Contains` with `StringComparison.OrdinalIgnoreCase`.
fn contains_ignoring_case(haystack: &str, needle: &str) -> bool {
    haystack.to_lowercase().contains(&needle.to_lowercase())
}

fn first_line(text: &str) -> String {
    let trimmed = text.trim_start();
    let line = trimmed.split(['\r', '\n']).next().unwrap_or("").trim();
    if utf16_len(line) <= 200 {
        return line.to_owned();
    }

    // The first 200 UTF-16 units. C# may cut a surrogate pair in half; this leaves the pair out.
    let mut units = 0;
    let mut short: String = line
        .chars()
        .take_while(|c| {
            units += c.len_utf16();
            units <= 200
        })
        .collect();
    short.push('…');
    short
}

/// `Path.GetFileName` as it works on Windows, where the paths come from, on any platform: what
/// follows the last `\`, `/` or `:`.
fn file_name(path: &str) -> &str {
    path.rfind(['\\', '/', ':']).map_or(path, |index| &path[index + 1..])
}

/// `DateTime.MinValue`, what C# gives a missing `CopiedAt`.
fn min_date_time() -> DateTime<Utc> {
    DateTime::from_timestamp(-62_135_596_800, 0).unwrap_or_default()
}

/// As System.Text.Json writes a UTC `DateTime`: at most seven decimals, trailing zeros dropped.
fn write_date_time(at: DateTime<Utc>) -> String {
    let mut text = at.format("%Y-%m-%dT%H:%M:%S").to_string();
    let fraction = format!("{:07}", at.timestamp_subsec_nanos() / 100);
    let fraction = fraction.trim_end_matches('0');
    if !fraction.is_empty() {
        text.push('.');
        text.push_str(fraction);
    }
    text.push('Z');
    text
}

/// The C# app writes local times with their offset. One without an offset is read as local time,
/// as C# would hold it.
fn read_date_time(value: &Value) -> Read<DateTime<Utc>> {
    let Value::String(text) = value else { return Err(Mismatch) };
    if let Ok(at) = DateTime::parse_from_rfc3339(text) {
        return Ok(at.with_timezone(&Utc));
    }

    let naive = NaiveDateTime::parse_from_str(text, "%Y-%m-%dT%H:%M:%S%.f").map_err(|_| Mismatch)?;
    naive.and_local_timezone(Local).earliest().map(|at| at.with_timezone(&Utc)).ok_or(Mismatch)
}

/// Whether a copy is meant to stay out of clipboard histories, and which programs never go in.
pub mod privacy {
    /// Programs whose copies are never kept, as a safety net for password managers that do not
    /// mark them.
    pub const DEFAULT_EXCLUDED_PROGRAMS: [&str; 11] = [
        "keepass",
        "keepassxc",
        "1password",
        "bitwarden",
        "lastpass",
        "dashlane",
        "enpass",
        "nordpass",
        "roboform",
        "proton pass",
        "protonpass",
    ];

    /// Password managers and a few other programs mark what they put on the clipboard so that
    /// histories skip it: the format `ExcludeClipboardContentFromMonitorProcessing`, or
    /// `CanIncludeInClipboardHistory` set to 0. Checks against [`DEFAULT_EXCLUDED_PROGRAMS`].
    pub fn should_skip(
        has_exclude_format: bool,
        can_include_in_history: Option<i32>,
        source_program: Option<&str>,
    ) -> bool {
        should_skip_with(has_exclude_format, can_include_in_history, source_program, &DEFAULT_EXCLUDED_PROGRAMS)
    }

    /// [`should_skip`] with a list of programs of the caller's own.
    pub fn should_skip_with(
        has_exclude_format: bool,
        can_include_in_history: Option<i32>,
        source_program: Option<&str>,
        excluded_programs: &[impl AsRef<str>],
    ) -> bool {
        if has_exclude_format || can_include_in_history == Some(0) {
            return true;
        }

        let Some(source_program) = source_program.filter(|s| !s.is_empty()) else { return false };
        excluded_programs.iter().any(|p| super::contains_ignoring_case(source_program, p.as_ref()))
    }
}

/// The only part of the history that survives a restart: what the user pinned, in
/// `clipboard-pins.json`, shared with the C# app.
pub mod pins {
    use super::*;

    /// A missing or corrupt file gives no pins.
    pub fn load(file: &Path) -> Vec<ClipboardItem> {
        let Ok(text) = fs::read_to_string(file) else { return Vec::new() };
        // `File.ReadAllText` drops a byte order mark.
        let text = text.strip_prefix('\u{FEFF}').unwrap_or(&text);
        serde_json::from_str::<Value>(text)
            .ok()
            .and_then(|value| json::list(&value, ClipboardItem::read).ok())
            .unwrap_or_default()
    }

    /// Saves the pinned ones among `items` as indented JSON, through a temporary file, and
    /// deletes the file when none is pinned. Pins are a convenience: failing to keep them must
    /// not take the app down, so the caller may ignore the error, as C# does.
    pub fn save(file: &Path, items: &[ClipboardItem]) -> io::Result<()> {
        let pinned: Vec<Value> = items.iter().filter(|i| i.pinned).map(ClipboardItem::to_json).collect();
        if pinned.is_empty() {
            return match fs::remove_file(file) {
                Err(e) if e.kind() != io::ErrorKind::NotFound => Err(e),
                _ => Ok(()),
            };
        }

        if let Some(folder) = file.parent() {
            fs::create_dir_all(folder)?;
        }
        let temporary = tmp_path(file);
        fs::write(&temporary, serde_json::to_string_pretty(&Value::Array(pinned)).map_err(io::Error::other)?)?;
        fs::rename(&temporary, file)
    }
}
