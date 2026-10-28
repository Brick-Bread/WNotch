use std::fs;

use serde_json::{Value, json};

use super::*;
use crate::fixtures;
use crate::hud;

/// What this port reads a `null` as, for keys whose C# type is a string or list that may not be
/// null. C# keeps the `null`; this port reads it as empty (see [`crate::json`]).
fn empty_instead_of_null(key: &str) -> Option<Value> {
    match key {
        "OpenHotkey" | "AccentColor" | "PaletteHotkey" => Some(json!("")),
        "TimerPresets" | "HomeWidgets" | "CalendarFeeds" | "EnabledPlugins" | "TerminalPresets" | "RecentFolders"
        | "HiddenAgents" | "MirroredApps" => Some(json!([])),
        _ => None,
    }
}

/// What this port saves for a fixture, shaped to compare with what C# saved: keys C# does not know
/// are taken out (C# drops them), and new widget ids are written as the fixtures write them.
/// `input` is the file the settings were read from, if any: ids not in it were made up.
fn saved_like_csharp(settings: &AppSettings, input: &str) -> Value {
    let mut saved = settings.to_json_value();
    let map = saved.as_object_mut().unwrap();
    map.retain(|key, _| AppSettings::KEYS.contains(&key.as_str()));
    if let Some(Value::Array(widgets)) = map.get_mut("HomeWidgets") {
        for widget in widgets {
            let id = widget["Id"].as_str().unwrap().to_owned();
            if !input.contains(&format!("\"{id}\"")) {
                assert!(id.len() == 8 && id.bytes().all(|b| b.is_ascii_hexdigit()), "{id} is not a new widget id");
                widget["Id"] = json!("*generated*");
            }
        }
    }
    saved
}

fn expected(name: &str) -> Value {
    let mut expected = fixtures::json(&format!("settings/expected/{name}"));
    let map = expected.as_object_mut().unwrap();
    for (key, value) in map.iter_mut() {
        if value.is_null() {
            if let Some(empty) = empty_instead_of_null(key) {
                *value = empty;
            }
        }
    }
    expected
}

#[test]
fn every_settings_fixture_loads_and_saves_as_csharp_does() {
    let inputs = fixtures::files("settings/input");
    assert!(inputs.len() >= 10, "the settings fixtures are missing");
    for path in inputs {
        let name = path.file_name().unwrap().to_str().unwrap();
        let input = fs::read_to_string(&path).unwrap();
        assert_eq!(saved_like_csharp(&AppSettings::from_json(&input), &input), expected(name), "{name}");
    }
}

#[test]
fn a_missing_file_gives_the_defaults() {
    let folder = tempfile::tempdir().unwrap();
    let settings = SettingsStore::new(folder.path().join("settings.json")).load();
    assert_eq!(saved_like_csharp(&settings, ""), expected("missing-file.json"));
}

#[test]
fn keys_from_another_version_are_kept() {
    let input = fixtures::text("settings/input/older-version.json");
    let saved = AppSettings::from_json(&input).to_json_value();
    assert_eq!(saved["SomeSettingFromTheFuture"], json!({ "nested": [1, 2, 3] }));
    assert_eq!(saved["RetiredFlag"], json!(true));
    // They come after the known keys.
    let keys: Vec<&String> = saved.as_object().unwrap().keys().collect();
    assert_eq!(keys.len(), AppSettings::KEYS.len() + 2);
    assert_eq!(keys[AppSettings::KEYS.len()], "SomeSettingFromTheFuture");
}

#[test]
fn theme_follows_windows_only_when_asked_to() {
    assert!(!NotchTheme::Dark.is_light(true));
    assert!(NotchTheme::Light.is_light(false));
    assert!(NotchTheme::System.is_light(true));
    assert!(!NotchTheme::System.is_light(false));
}

fn with_store(test: impl FnOnce(&SettingsStore)) {
    let folder = tempfile::tempdir().unwrap();
    test(&SettingsStore::new(folder.path().join("nested").join("settings.json")));
}

#[test]
fn theme_is_saved_by_name() {
    with_store(|store| {
        assert_eq!(store.load().theme, NotchTheme::Dark);
        store.save(&AppSettings { theme: NotchTheme::System, ..Default::default() }).unwrap();
        assert!(fs::read_to_string(store.path()).unwrap().contains("\"Theme\": \"System\""));
        assert_eq!(store.load().theme, NotchTheme::System);
    });
}

#[test]
fn style_and_position_default_to_a_notch_at_the_top_and_are_saved_by_name() {
    with_store(|store| {
        let fresh = store.load();
        assert_eq!((fresh.style, fresh.position), (NotchStyle::Notch, NotchPosition::TopCenter));
        store
            .save(&AppSettings {
                style: NotchStyle::Island,
                position: NotchPosition::TaskbarLeft,
                ..Default::default()
            })
            .unwrap();
        let text = fs::read_to_string(store.path()).unwrap();
        assert!(text.contains("\"Style\": \"Island\""));
        assert!(text.contains("\"Position\": \"TaskbarLeft\""));
        assert_eq!(store.load().position, NotchPosition::TaskbarLeft);
    });
}

#[test]
fn a_save_leaves_no_temporary_file() {
    with_store(|store| {
        store.save(&AppSettings::default()).unwrap();
        assert!(store.path().exists());
        assert!(!tmp_path(store.path()).exists());
    });
}

#[test]
fn accent_defaults_to_a_preset_and_none_switches_it_off() {
    assert!(AppSettings::default().accent().is_some());
    assert!(GlowColor::from_name(NO_ACCENT).is_none());
}

#[test]
fn timer_presets_default_to_the_classic_four_and_round_trip() {
    with_store(|store| {
        let labels: Vec<String> = store.load().timers().iter().map(TimerPreset::label).collect();
        assert_eq!(labels, ["5m", "15m", "30m", "1h"]);
        store.save(&AppSettings { timer_presets: vec![TimerPreset::new("Tea", 180)], ..Default::default() }).unwrap();
        assert_eq!(store.load().timers(), [TimerPreset::new("Tea", 180)]);
        assert!(!fs::read_to_string(store.path()).unwrap().contains("Label"));
    });
}

#[test]
fn timer_presets_from_a_hand_edited_file_are_tidied() {
    let mut presets = vec![
        TimerPreset::new("  Stretch  ", 120),
        TimerPreset::new("Never", 0),
        TimerPreset::new("Far too long a name", 60),
        TimerPreset::new("", 100_000),
    ];
    presets.extend((1..=10).map(|i| TimerPreset::new("", i * 60)));
    let timers = AppSettings { timer_presets: presets, ..Default::default() }.timers();

    assert_eq!(timers.len(), MAX_TIMER_PRESETS);
    assert_eq!(timers[0], TimerPreset::new("Stretch", 120));
    assert_eq!(timers[1].name, "Far too lo");
    assert!(timers.iter().all(TimerPreset::is_valid));
}

#[test]
fn shelf_and_hotkey_settings_have_working_defaults_and_round_trip() {
    with_store(|store| {
        let fresh = store.load();
        assert!(fresh.shelf_opens_on_drag);
        assert_eq!(fresh.open_hotkey, "Alt+Shift+N");
        store
            .save(&AppSettings { shelf_opens_on_drag: false, open_hotkey: String::new(), ..Default::default() })
            .unwrap();
        let saved = store.load();
        assert!(!saved.shelf_opens_on_drag);
        assert_eq!(saved.open_hotkey, "");
    });
}

#[test]
fn switching_a_hud_off_suppresses_its_activity() {
    assert!(AppSettings::default().suppressed_activity_ids().is_empty());
    let settings = AppSettings { show_bluetooth: false, show_caps_lock: false, ..Default::default() };
    assert_eq!(settings.suppressed_activity_ids(), [hud::BLUETOOTH_ID, hud::CAPS_LOCK_ID]);
}

#[test]
fn pomodoro_lengths_out_of_range_fall_back_to_the_classic_ones() {
    let settings = AppSettings {
        pomodoro_focus_minutes: 50,
        pomodoro_short_break_minutes: 0,
        pomodoro_long_break_minutes: 601,
        ..Default::default()
    };
    let durations = settings.pomodoro_durations();
    assert_eq!(durations.focus, Duration::from_secs(50 * 60));
    assert_eq!(durations.short_break, PomodoroDurations::CLASSIC.short_break);
    assert_eq!(durations.long_break, PomodoroDurations::CLASSIC.long_break);
}

#[test]
fn recent_folders_are_most_recent_first_without_repeats() {
    let mut settings = AppSettings::default();
    for i in 0..10 {
        settings.remember_folder(&format!("C:\\f{i}"));
    }
    settings.remember_folder("c:\\F3");
    assert_eq!(settings.recent_folders.len(), 8);
    assert_eq!(settings.recent_folders[0], "c:\\F3");
    assert_eq!(settings.recent_folders.iter().filter(|f| f.eq_ignore_ascii_case("c:\\f3")).count(), 1);
}

#[test]
fn a_debug_build_starts_from_the_installed_settings_without_what_reaches_outside_notch() {
    let root = tempfile::tempdir().unwrap();
    let installed = root.path().join("Notch").join("settings.json");
    let debug = root.path().join("Notch-Debug").join("settings.json");
    let original = AppSettings {
        accent_color: "Green".into(),
        webhook_enabled: true,
        global_agent_hooks: true,
        enabled_plugins: vec!["chomp.desk".into()],
        ..Default::default()
    };
    SettingsStore::new(&installed).save(&original).unwrap();

    assert!(folders::seed_settings(&installed, &debug));

    let copy = SettingsStore::new(&debug).load();
    assert_eq!(copy.accent_color, "Green");
    assert_eq!(copy.enabled_plugins, ["chomp.desk"]);
    assert!(!copy.webhook_enabled);
    assert!(!copy.global_agent_hooks);
    assert!(SettingsStore::new(&installed).load().webhook_enabled);
}

#[test]
fn settings_are_copied_only_once_and_never_onto_themselves() {
    let root = tempfile::tempdir().unwrap();
    let installed = root.path().join("Notch").join("settings.json");
    let debug = root.path().join("Notch-Debug").join("settings.json");
    SettingsStore::new(&installed).save(&AppSettings { accent_color: "Green".into(), ..Default::default() }).unwrap();
    assert!(folders::seed_settings(&installed, &debug));

    SettingsStore::new(&debug).save(&AppSettings { accent_color: "Red".into(), ..Default::default() }).unwrap();
    assert!(!folders::seed_settings(&installed, &debug));
    assert_eq!(SettingsStore::new(&debug).load().accent_color, "Red");
    assert!(!folders::seed_settings(&installed, &installed));
    assert!(!folders::seed_settings(
        &root.path().join("missing.json"),
        &root.path().join("other").join("settings.json")
    ));
}

#[test]
fn a_debug_build_uses_its_own_folders_unless_told_to_share() {
    let app_data = Path::new("/AppData/Roaming");
    let local = Path::new("/AppData/Local");
    let debug = DataFolders::under(app_data, local, true, &[]);
    assert_eq!(debug.roaming, app_data.join("Notch-Debug"));
    assert_eq!(debug.local, local.join("Notch-Debug"));
    assert_eq!(debug.release_roaming, app_data.join("Notch"));

    let shared = DataFolders::under(app_data, local, true, &["Notch.exe".into(), "--SHARED-DATA".into()]);
    assert_eq!(shared.roaming, app_data.join("Notch"));
    assert_eq!(DataFolders::under(app_data, local, false, &[]).roaming, app_data.join("Notch"));
}
