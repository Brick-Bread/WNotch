use std::fs;

use super::*;
use crate::activities::ActivityTier;

#[test]
fn new_files_go_to_the_front_in_the_order_they_were_dropped() {
    let mut shelf = FileShelf::default();

    shelf.add([r"C:\a.txt"]);
    shelf.add([r"C:\b.txt", r"C:\c.txt"]);

    assert_eq!(shelf.snapshot(), [r"C:\b.txt", r"C:\c.txt", r"C:\a.txt"]);
}

#[test]
fn a_file_already_on_the_shelf_moves_to_the_front_instead_of_showing_twice() {
    let mut shelf = FileShelf::new([r"C:\a.txt", r"C:\b.txt"]);

    shelf.add([r"c:\B.TXT"]);

    assert_eq!(shelf.snapshot(), [r"c:\B.TXT", r"C:\a.txt"]);
}

#[test]
fn a_full_shelf_drops_what_was_put_there_longest_ago() {
    let mut shelf = FileShelf::new((1..=CAPACITY).map(|i| format!(r"C:\old{i}.txt")));

    shelf.add([r"C:\new.txt"]);

    let paths = shelf.snapshot();
    assert_eq!(paths.len(), CAPACITY);
    assert_eq!(paths[0], r"C:\new.txt");
    assert!(!paths.contains(&format!(r"C:\old{CAPACITY}.txt")));
}

#[test]
fn blank_paths_and_repeats_in_one_drop_are_left_out() {
    let mut shelf = FileShelf::default();

    shelf.add(["", "  ", r"C:\a.txt", r"C:\A.txt"]);

    assert_eq!(shelf.snapshot(), [r"C:\a.txt"]);
}

#[test]
fn only_real_changes_are_reported() {
    let mut shelf = FileShelf::new([r"C:\a.txt"]);
    let mut changes = 0;

    changes += i32::from(shelf.add(Vec::<String>::new()));
    changes += i32::from(shelf.add([r"C:\a.txt"]));
    changes += i32::from(shelf.prune(|_| true));
    assert!(!shelf.remove(r"C:\missing.txt"));
    assert_eq!(changes, 0);

    changes += i32::from(shelf.add([r"C:\b.txt"]));
    let removed = shelf.remove(r"c:\A.TXT");
    assert!(removed);
    changes += i32::from(removed);
    changes += i32::from(shelf.clear());
    changes += i32::from(shelf.clear());
    assert_eq!(changes, 3);
}

#[test]
fn pruning_drops_files_that_are_gone() {
    let mut shelf = FileShelf::new([r"C:\here.txt", r"C:\moved.txt", r"C:\also-here.txt"]);

    shelf.prune(|path| !path.contains("moved"));

    assert_eq!(shelf.snapshot(), [r"C:\here.txt", r"C:\also-here.txt"]);
}

#[test]
fn the_shelf_is_saved_and_loaded_in_order() {
    let folder = tempfile::tempdir().unwrap();
    let store = ShelfStore::new(folder.path().join("notch").join("shelf.json"));
    assert!(store.load().is_empty());

    store.save(&[r"C:\b.txt".into(), r"C:\a.txt".into()]).unwrap();

    assert_eq!(FileShelf::new(store.load()).snapshot(), [r"C:\b.txt", r"C:\a.txt"]);
}

#[test]
fn a_corrupt_shelf_file_yields_an_empty_shelf() {
    let folder = tempfile::tempdir().unwrap();
    let store = ShelfStore::new(folder.path().join("shelf.json"));
    fs::write(store.file_path(), "{ not a list").unwrap();

    assert!(store.load().is_empty());
}

#[test]
fn a_drop_on_the_closed_pill_is_confirmed_with_a_count() {
    for (count, expected) in [(1, "1 item"), (3, "3 items")] {
        let notice = activities::added(count);

        assert_eq!(notice.tier, ActivityTier::Transient);
        assert_eq!(notice.detail.as_deref(), Some(expected));
    }
}

// Not in the C# tests: the file is shared with the C# app.
#[test]
fn the_file_is_an_indented_list_and_reads_what_csharp_writes() {
    let folder = tempfile::tempdir().unwrap();
    let store = ShelfStore::new(folder.path().join("shelf.json"));

    store.save(&[r"C:\a.txt".into()]).unwrap();
    assert_eq!(fs::read_to_string(store.file_path()).unwrap(), "[\n  \"C:\\\\a.txt\"\n]");
    assert!(!folder.path().join("shelf.json.tmp").exists());

    // System.Text.Json escapes non-ASCII text, and the file may start with a byte order mark.
    fs::write(store.file_path(), "\u{FEFF}[\n  \"C:\\\\caf\\u00E9.txt\",\n  null\n]").unwrap();
    assert_eq!(store.load(), [r"C:\café.txt"]);

    fs::write(store.file_path(), "null").unwrap();
    assert!(store.load().is_empty());
}
