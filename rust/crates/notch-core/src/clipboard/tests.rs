use std::fs;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};

use super::*;

fn t() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 3, 1, 9, 0, 0).unwrap()
}

fn secs(seconds: i64) -> DateTime<Utc> {
    t() + TimeDelta::seconds(seconds)
}

fn texts(items: &[ClipboardItem]) -> Vec<&str> {
    items.iter().map(|i| i.text.as_deref().unwrap_or("")).collect()
}

#[test]
fn the_newest_copy_is_first() {
    let mut history = ClipboardHistory::default();
    history.add_text("one", t(), None);
    history.add_text("two", secs(1), None);
    history.add_text("three", secs(2), None);

    assert_eq!(texts(&history.snapshot(None)), ["three", "two", "one"]);
}

#[test]
fn copying_the_same_thing_again_moves_it_up() {
    let mut history = ClipboardHistory::default();
    history.add_text("a", t(), None);
    history.add_text("b", secs(1), None);
    history.add_text("a", secs(2), None);

    assert_eq!(texts(&history.snapshot(None)), ["a", "b"]);
}

#[test]
fn the_oldest_go_when_the_history_is_full_but_pins_stay() {
    let mut history = ClipboardHistory::new(3);
    let first = history.add_text("first", t(), None).unwrap();
    history.set_pinned(first.id, true);
    for i in 0..5 {
        history.add_text(&format!("item {i}"), secs(i + 1), None);
    }

    assert_eq!(texts(&history.snapshot(None)), ["first", "item 4", "item 3", "item 2"]);
}

#[test]
fn pinned_items_are_listed_first() {
    let mut history = ClipboardHistory::default();
    let old = history.add_text("old", t(), None).unwrap();
    history.add_text("new", secs(5), None);
    history.set_pinned(old.id, true);

    assert_eq!(texts(&history.snapshot(None)), ["old", "new"]);
}

#[test]
fn empty_text_is_not_kept() {
    // C#'s null text cannot be passed here; the empty string stands in for it.
    for text in ["", "   \r\n"] {
        assert_eq!(ClipboardHistory::default().add_text(text, t(), None), None, "{text:?}");
    }
}

#[test]
fn huge_copies_are_not_kept() {
    let mut history = ClipboardHistory::default();
    assert_eq!(history.add_text(&"x".repeat(MAX_TEXT_LENGTH + 1), t(), None), None);
    assert_eq!(history.add_image(&vec![0; MAX_IMAGE_BYTES + 1], t(), None), None);
    assert!(history.snapshot(None).is_empty());
}

#[test]
fn pictures_and_files_are_kept_and_matched_by_content() {
    let mut history = ClipboardHistory::default();
    history.add_image(&[1, 2, 3], t(), None);
    history.add_image(&[1, 2, 3], secs(1), None);
    history.add_files([r"C:\a\one.txt", r"C:\a\two.txt"], secs(2), None);
    history.add_files([r"c:\A\ONE.txt", r"C:\a\two.txt"], secs(3), None);

    let snapshot = history.snapshot(None);
    assert_eq!(snapshot.len(), 2);
    assert_eq!(snapshot.iter().find(|i| i.kind == ClipboardKind::Files).unwrap().preview(), "one.txt, two.txt");
    assert_eq!(history.add_files(Vec::<String>::new(), t(), None), None);
}

#[test]
fn the_list_can_be_searched() {
    let mut history = ClipboardHistory::default();
    history.add_text("hello world", t(), None);
    history.add_text("goodbye", secs(1), None);
    history.add_files([r"C:\docs\World Cup.xlsx"], secs(2), None);

    assert_eq!(history.snapshot(Some("WORLD")).len(), 2);
    assert_eq!(history.snapshot(Some("goodbye")).len(), 1);
    assert!(history.snapshot(Some("nothing here")).is_empty());
}

#[test]
fn previews_show_the_first_line() {
    let item = ClipboardItem {
        text: Some("\n  first line\r\nsecond line".into()),
        ..ClipboardItem::new(1, ClipboardKind::Text, t())
    };
    assert_eq!(item.preview(), "first line");

    let long = ClipboardItem { text: Some("x".repeat(500)), ..ClipboardItem::new(1, ClipboardKind::Text, t()) };
    assert!(long.preview().ends_with('…'));
    assert_eq!(long.preview().chars().count(), 201);
}

#[test]
fn clearing_keeps_pins_unless_told_otherwise() {
    let mut history = ClipboardHistory::default();
    let keep = history.add_text("keep", t(), None).unwrap();
    history.add_text("lose", secs(1), None);
    history.set_pinned(keep.id, true);

    history.clear(false);
    assert_eq!(texts(&history.snapshot(None)), ["keep"]);

    history.clear(true);
    assert!(history.snapshot(None).is_empty());
}

#[test]
fn changes_are_announced_once_each() {
    let mut history = ClipboardHistory::default();
    let mut changes = 0;

    let item = history.add_text("a", t(), None).unwrap();
    changes += 1;
    changes += i32::from(history.set_pinned(item.id, true));
    changes += i32::from(history.set_pinned(item.id, true));
    changes += i32::from(history.remove(item.id));
    changes += i32::from(history.remove(item.id));

    assert_eq!(changes, 3);
}

#[test]
fn marked_copies_are_skipped() {
    assert!(privacy::should_skip(true, None, None));
    assert!(privacy::should_skip(false, Some(0), None));
    assert!(!privacy::should_skip(false, Some(1), Some("notepad")));
    assert!(!privacy::should_skip(false, None, None));
}

#[test]
fn password_managers_are_skipped_even_when_they_do_not_mark_their_copies() {
    for program in ["KeePassXC", "1Password", "Bitwarden"] {
        assert!(privacy::should_skip(false, None, Some(program)), "{program}");
    }
}

#[test]
fn only_pins_are_saved() {
    let folder = tempfile::tempdir().unwrap();
    let file = folder.path().join("clipboard-pins.json");

    let mut history = ClipboardHistory::default();
    let pinned = history.add_text("pinned text", t(), None).unwrap();
    history.add_text("not pinned", secs(1), None);
    history.add_files([r"C:\x\y.txt"], secs(2), None);
    history.set_pinned(pinned.id, true);

    pins::save(&file, &history.snapshot(None)).unwrap();
    let loaded = pins::load(&file);

    assert_eq!(loaded.len(), 1);
    assert_eq!(loaded[0].text.as_deref(), Some("pinned text"));

    let mut next = ClipboardHistory::default();
    next.restore(loaded);
    let snapshot = next.snapshot(None);
    assert_eq!(snapshot.len(), 1);
    assert!(snapshot[0].pinned);

    pins::save(&file, &[]).unwrap();
    assert!(!file.exists());
}

// Not in the C# tests: the file is shared with the C# app.
#[test]
fn pins_are_written_the_way_csharp_writes_them() {
    let folder = tempfile::tempdir().unwrap();
    let file = folder.path().join("clipboard-pins.json");
    let at = t() + TimeDelta::milliseconds(250);
    let item = ClipboardItem {
        image: Some(vec![1, 2, 3]),
        source: Some("mspaint".into()),
        pinned: true,
        ..ClipboardItem::new(7, ClipboardKind::Image, at)
    };

    pins::save(&file, std::slice::from_ref(&item)).unwrap();

    let expected = r#"[
  {
    "Id": 7,
    "Kind": 1,
    "CopiedAt": "2026-03-01T09:00:00.25Z",
    "Text": null,
    "Image": "AQID",
    "Files": null,
    "Source": "mspaint",
    "Pinned": true,
    "Preview": "Picture (0 KB)"
  }
]"#;
    assert_eq!(fs::read_to_string(&file).unwrap(), expected);
    assert_eq!(pins::load(&file), [item]);
}

#[test]
fn pins_written_by_csharp_are_read() {
    let folder = tempfile::tempdir().unwrap();
    let file = folder.path().join("clipboard-pins.json");
    // What the C# app writes for a copy made at local time: an offset, seven decimals, escaped text.
    fs::write(
        &file,
        "\u{FEFF}[{\"Id\":3,\"Kind\":2,\"CopiedAt\":\"2026-03-01T10:00:00.1234567+01:00\",\"Text\":null,\
         \"Image\":null,\"Files\":[\"C:\\\\caf\\u00E9\\\\a.txt\"],\"Source\":null,\"Pinned\":true,\
         \"Preview\":\"a.txt\"},{\"Id\":4,\"Kind\":9,\"CopiedAt\":\"2026-03-01T09:00:00\",\"Pinned\":true}]",
    )
    .unwrap();

    let loaded = pins::load(&file);
    assert_eq!(loaded.len(), 2);
    assert_eq!(loaded[0].kind, ClipboardKind::Files);
    assert_eq!(loaded[0].copied_at, t() + TimeDelta::nanoseconds(123_456_700));
    assert_eq!(loaded[0].files.as_deref(), Some(&[r"C:\café\a.txt".to_owned()][..]));
    // A kind with no name is kept, and shows as a picture as in C#.
    assert_eq!(loaded[1].kind, ClipboardKind::Unknown(9));
    assert_eq!(loaded[1].preview(), "Picture (0 KB)");

    // The kind is a number, never a name; anything of the wrong shape loses the whole file.
    fs::write(&file, r#"[{"Id":1,"Kind":"Text","CopiedAt":"2026-03-01T09:00:00Z"}]"#).unwrap();
    assert!(pins::load(&file).is_empty());
    fs::write(&file, "{ not a list").unwrap();
    assert!(pins::load(&file).is_empty());
}
