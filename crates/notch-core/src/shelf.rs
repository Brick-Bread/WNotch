//! The shelf: files and folders put aside on the notch, to drag out again later. Port of
//! `Notch.Core/Shelf` (FileShelf, ShelfStore, ShelfActivities).

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};
use std::time::Duration;

use crate::activity::{Activity, ActivityTier};
use crate::glow::{Glow, GlowColor, GlowPattern};

/// As many as the shelf keeps; adding more drops the ones put there longest ago.
pub const CAPACITY: usize = 24;

/// Id of the notice shown for a drop on the closed pill.
pub const ADDED_ID: &str = "shelf.added";

/// Files and folders the user has put aside. It holds their paths, not copies; the app reads
/// [`FileShelf::snapshot`] whenever a method reports a change.
pub struct FileShelf {
    paths: Mutex<Vec<String>>,
    ignore_case: bool,
}

impl Default for FileShelf {
    fn default() -> Self {
        Self::new(Vec::<String>::new())
    }
}

impl FileShelf {
    /// A shelf holding `paths`, newest first, as it was last saved. Two paths are the same file
    /// when they differ only in case on Windows, the one platform whose file systems ignore it.
    pub fn new(paths: impl IntoIterator<Item = impl AsRef<str>>) -> Self {
        Self::with_case_rule(paths, cfg!(windows))
    }

    /// Like [`FileShelf::new`] with the case rule given instead of the platform's.
    pub fn with_case_rule(paths: impl IntoIterator<Item = impl AsRef<str>>, ignore_case: bool) -> Self {
        let mut kept = tidy(paths, ignore_case);
        kept.truncate(CAPACITY);
        Self { paths: Mutex::new(kept), ignore_case }
    }

    fn lock(&self) -> MutexGuard<'_, Vec<String>> {
        self.paths.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn same(&self, a: &str, b: &str) -> bool {
        same_path(a, b, self.ignore_case)
    }

    /// Puts paths at the front of the shelf, in the order given. One that is already there moves
    /// to the front instead of showing twice. Returns whether the shelf changed.
    pub fn add(&self, paths: impl IntoIterator<Item = impl AsRef<str>>) -> bool {
        let added = tidy(paths, self.ignore_case);
        if added.is_empty() {
            return false;
        }

        let mut held = self.lock();
        let mut next = added.clone();
        next.extend(held.iter().filter(|old| !added.iter().any(|new| self.same(new, old))).cloned());
        next.truncate(CAPACITY);
        if next == *held {
            return false;
        }
        *held = next;
        true
    }

    /// Takes one path off the shelf. Returns whether it was there.
    pub fn remove(&self, path: &str) -> bool {
        let mut held = self.lock();
        let before = held.len();
        held.retain(|old| !self.same(old, path));
        held.len() != before
    }

    /// Takes everything off the shelf. Returns whether there was anything.
    pub fn clear(&self) -> bool {
        let mut held = self.lock();
        let had = !held.is_empty();
        held.clear();
        had
    }

    /// Drops what is no longer there, such as a file that was moved or deleted. `exists` may be
    /// slow; the shelf is not held up while it runs. Returns whether anything was dropped.
    pub fn prune(&self, exists: impl Fn(&str) -> bool) -> bool {
        let gone: Vec<String> = self.snapshot().into_iter().filter(|path| !exists(path)).collect();
        if gone.is_empty() {
            return false;
        }

        let mut held = self.lock();
        let before = held.len();
        held.retain(|old| !gone.iter().any(|gone| self.same(old, gone)));
        held.len() != before
    }

    /// Newest first.
    pub fn snapshot(&self) -> Vec<String> {
        self.lock().clone()
    }
}

fn same_path(a: &str, b: &str, ignore_case: bool) -> bool {
    if ignore_case {
        a.to_lowercase() == b.to_lowercase()
    } else {
        a == b
    }
}

/// Trimmed, without blanks and without repeats (the first of a repeat stays).
fn tidy(paths: impl IntoIterator<Item = impl AsRef<str>>, ignore_case: bool) -> Vec<String> {
    let mut kept: Vec<String> = Vec::new();
    for path in paths {
        let path = path.as_ref().trim();
        if !path.is_empty() && !kept.iter().any(|old| same_path(old, path, ignore_case)) {
            kept.push(path.to_owned());
        }
    }
    kept
}

/// Loads and saves what is on the [`FileShelf`] as a JSON list of paths. A missing or corrupt
/// file yields an empty shelf.
pub struct ShelfStore {
    path: PathBuf,
}

impl ShelfStore {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    /// `shelf.json` beside the settings.
    pub fn default_path() -> PathBuf {
        dirs::config_dir().unwrap_or_else(|| PathBuf::from(".")).join("notch").join("shelf.json")
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> Vec<String> {
        fs::read_to_string(&self.path)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default()
    }

    /// Writes a temporary file first so a crash cannot leave a half-written shelf. The shelf is a
    /// convenience: the caller logs a failure to persist it and carries on.
    pub fn save(&self, paths: &[String]) -> std::io::Result<()> {
        if let Some(folder) = self.path.parent() {
            fs::create_dir_all(folder)?;
        }
        let mut temporary = self.path.clone().into_os_string();
        temporary.push(".tmp");
        let temporary = PathBuf::from(temporary);
        fs::write(&temporary, serde_json::to_string_pretty(paths)?)?;
        fs::rename(&temporary, &self.path)
    }
}

/// What a tile calls a path: its last part, or the whole path for a root such as `C:\`.
pub fn display_name(path: &str) -> &str {
    let trimmed = path.trim_end_matches(['\\', '/']);
    match trimmed.rsplit(['\\', '/']).next() {
        Some(name) if !name.is_empty() => name,
        _ => path,
    }
}

/// Whether a tile shows a folder glyph until its picture arrives: a name without an extension is
/// taken for a folder, anything else for a document. Looking at the disk would hold the list up.
pub fn looks_like_folder(path: &str) -> bool {
    let name = display_name(path);
    !matches!(name.rfind('.'), Some(dot) if dot + 1 < name.len())
}

/// The `file://` URI of an absolute path, as the freedesktop thumbnail and file manager
/// specifications want it: everything but unreserved characters and `/` is percent-encoded.
pub fn file_uri(path: &str) -> String {
    let mut uri = String::from("file://");
    for byte in path.replace('\\', "/").bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'/' | b'-' | b'_' | b'.' | b'~') {
            uri.push(char::from(byte));
        } else {
            uri.push_str(&format!("%{byte:02X}"));
        }
    }
    uri
}

/// The notice for files dropped on the pill while the notch stayed closed, so the drop is seen to
/// have landed.
pub fn added(count: usize) -> Activity {
    Activity {
        id: ADDED_ID.to_owned(),
        tier: ActivityTier::Transient,
        title: "On the shelf".to_owned(),
        detail: Some(if count == 1 { "1 item".to_owned() } else { format!("{count} items") }),
        glyph: Some("🗂".to_owned()),
        image: None,
        progress: None,
        glow: Some(Glow { color: GlowColor::WHITE, pattern: GlowPattern::Flash, strength: 0.6 }),
        lifetime: Some(Duration::from_millis(3500)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn shelf(paths: &[&str]) -> FileShelf {
        FileShelf::with_case_rule(paths.iter().copied(), true)
    }

    #[test]
    fn new_files_go_to_the_front_in_the_order_they_were_dropped() {
        let shelf = shelf(&[]);
        shelf.add(["C:\\a.txt"]);
        shelf.add(["C:\\b.txt", "C:\\c.txt"]);
        assert_eq!(shelf.snapshot(), ["C:\\b.txt", "C:\\c.txt", "C:\\a.txt"]);
    }

    #[test]
    fn a_file_already_on_the_shelf_moves_to_the_front_instead_of_showing_twice() {
        let shelf = shelf(&["C:\\a.txt", "C:\\b.txt"]);
        shelf.add(["c:\\B.TXT"]);
        assert_eq!(shelf.snapshot(), ["c:\\B.TXT", "C:\\a.txt"]);
    }

    #[test]
    fn paths_that_differ_in_case_are_two_files_where_case_matters() {
        let shelf = FileShelf::with_case_rule(["/home/a.txt"], false);
        shelf.add(["/home/A.txt"]);
        assert_eq!(shelf.snapshot(), ["/home/A.txt", "/home/a.txt"]);
    }

    #[test]
    fn a_full_shelf_drops_what_was_put_there_longest_ago() {
        let old: Vec<String> = (1..=CAPACITY).map(|i| format!("C:\\old{i}.txt")).collect();
        let shelf = FileShelf::with_case_rule(&old, true);

        shelf.add(["C:\\new.txt"]);

        let paths = shelf.snapshot();
        assert_eq!(paths.len(), CAPACITY);
        assert_eq!(paths[0], "C:\\new.txt");
        assert!(!paths.contains(&format!("C:\\old{CAPACITY}.txt")));
    }

    #[test]
    fn blank_paths_and_repeats_in_one_drop_are_left_out() {
        let shelf = shelf(&[]);
        shelf.add(["", "  ", "C:\\a.txt", "C:\\A.txt"]);
        assert_eq!(shelf.snapshot(), ["C:\\a.txt"]);
    }

    #[test]
    fn only_real_changes_are_reported() {
        let shelf = shelf(&["C:\\a.txt"]);

        assert!(!shelf.add(Vec::<String>::new()));
        assert!(!shelf.add(["C:\\a.txt"]));
        assert!(!shelf.prune(|_| true));
        assert!(!shelf.remove("C:\\missing.txt"));

        assert!(shelf.add(["C:\\b.txt"]));
        assert!(shelf.remove("c:\\A.TXT"));
        assert!(shelf.clear());
        assert!(!shelf.clear());
    }

    #[test]
    fn pruning_drops_files_that_are_gone() {
        let shelf = shelf(&["C:\\here.txt", "C:\\moved.txt", "C:\\also-here.txt"]);
        assert!(shelf.prune(|path| !path.contains("moved")));
        assert_eq!(shelf.snapshot(), ["C:\\here.txt", "C:\\also-here.txt"]);
    }

    #[test]
    fn the_shelf_is_saved_and_loaded_in_order() {
        with_store("saved", |store| {
            assert!(store.load().is_empty());

            store.save(&["C:\\b.txt".to_owned(), "C:\\a.txt".to_owned()]).expect("save");

            assert_eq!(FileShelf::with_case_rule(store.load(), true).snapshot(), ["C:\\b.txt", "C:\\a.txt"]);
        });
    }

    #[test]
    fn a_corrupt_shelf_file_yields_an_empty_shelf() {
        with_store("corrupt", |store| {
            fs::create_dir_all(store.path().parent().expect("parent")).expect("folder");
            fs::write(store.path(), "{ not a list").expect("write");
            assert!(store.load().is_empty());
        });
    }

    #[test]
    fn a_drop_on_the_closed_pill_is_confirmed_with_a_count() {
        for (count, expected) in [(1, "1 item"), (3, "3 items")] {
            let notice = added(count);
            assert_eq!(notice.tier, ActivityTier::Transient);
            assert_eq!(notice.detail.as_deref(), Some(expected));
        }
    }

    #[test]
    fn a_tile_is_named_after_the_last_part_of_the_path() {
        assert_eq!(display_name("C:\\work\\report.pdf"), "report.pdf");
        assert_eq!(display_name("/home/me/photos/"), "photos");
        assert_eq!(display_name("C:\\"), "C:\\");
    }

    #[test]
    fn a_name_without_an_extension_gets_the_folder_glyph() {
        assert!(looks_like_folder("C:\\work\\photos"));
        assert!(looks_like_folder("/home/me/photos/"));
        assert!(!looks_like_folder("C:\\work\\report.pdf"));
        assert!(!looks_like_folder("/home/me/.bashrc"));
        assert!(looks_like_folder("/home/me/trailing."));
    }

    #[test]
    fn a_file_uri_is_percent_encoded() {
        assert_eq!(file_uri("/home/me/a b#1.png"), "file:///home/me/a%20b%231.png");
        assert_eq!(file_uri("C:\\x\\é"), "file://C%3A/x/%C3%A9");
    }

    fn with_store(name: &str, test: impl FnOnce(&ShelfStore)) {
        let folder = std::env::temp_dir().join(format!("notch-shelf-test-{}-{name}", std::process::id()));
        test(&ShelfStore::new(folder.join("shelf.json")));
        let _ = fs::remove_dir_all(&folder);
    }
}
