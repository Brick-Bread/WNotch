use serde_json::{Value, json};

use super::*;
use crate::fixtures;

/// A command as the fixtures describe it (see `tests/Notch.Core.Tests/FixtureTests.cs`).
fn describe(command: &NotchCommand) -> Value {
    match command {
        NotchCommand::InstallPlugin { plugin_id } => json!({ "kind": "install", "pluginId": plugin_id }),
        NotchCommand::SetPluginEnabled { plugin_id, enabled } => {
            json!({ "kind": "setPluginEnabled", "pluginId": plugin_id, "enabled": enabled })
        }
        NotchCommand::StartTimer { duration } => json!({ "kind": "startTimer", "seconds": duration.as_secs() }),
        NotchCommand::TimerAction(action) => {
            let action = match action {
                TimerAction::Stop => "stop",
                TimerAction::Pause => "pause",
                TimerAction::Resume => "resume",
            };
            json!({ "kind": "timerAction", "action": action })
        }
        NotchCommand::OpenTab { tab } => json!({ "kind": "openTab", "tab": tab }),
        NotchCommand::OpenPage { page } => json!({ "kind": "openPage", "page": page }),
        NotchCommand::Toast(toast) => json!({
            "kind": "toast",
            "title": toast.title,
            "detail": toast.detail,
            "glyph": toast.glyph,
            "color": toast.color.map(GlowColor::to_hex),
            "lifetimeSeconds": toast.lifetime.map(|d| d.as_secs()),
        }),
        NotchCommand::OpenPalette => json!({ "kind": "openPalette" }),
        NotchCommand::OpenSettings => json!({ "kind": "openSettings" }),
    }
}

#[test]
fn every_command_fixture_parses_as_csharp_does() {
    let Value::Array(cases) = fixtures::json("commands/expected.json") else { panic!("not a list") };
    assert!(cases.len() > 50, "the command fixtures are missing");
    for case in cases {
        let input = &case["input"];
        let result = match (input.get("url"), input.get("args")) {
            (Some(url), _) => parse_url(url.as_str().unwrap()),
            (_, Some(Value::Array(args))) => {
                let args: Vec<String> = args.iter().map(|a| a.as_str().unwrap().to_owned()).collect();
                parse_arguments(&args)
            }
            _ => panic!("a case needs url or args: {input}"),
        };

        let mut actual = json!({ "input": input, "ok": result.is_ok() });
        match result {
            Ok(command) => actual["command"] = describe(&command),
            Err(error) => actual["error"] = json!(error),
        }
        assert_eq!(actual, case, "{input}");
    }
}

#[test]
fn plugin_ids_follow_the_manifest_rule() {
    for (id, valid) in [
        ("acme.tool", true),
        ("a", true),
        ("acme-tool.v2", true),
        ("Acme", false),
        ("a..b", false),
        (".a", false),
        ("a.", false),
        ("a b", false),
        ("", false),
    ] {
        assert_eq!(is_plugin_id(id), valid, "{id}");
    }
    assert!(is_plugin_id(&"a".repeat(64)));
    assert!(!is_plugin_id(&"a".repeat(65)));
}

#[test]
fn page_keys_need_a_plugin_and_a_printable_page() {
    assert!(is_page_key("acme/main"));
    assert!(is_page_key("acme/Time Sheet"));
    assert!(!is_page_key("/main"));
    assert!(!is_page_key("acme/"));
    assert!(!is_page_key("acme/ main"));
    assert!(!is_page_key("acme/\tmain"));
    assert!(!is_page_key(&format!("acme/{}", "p".repeat(65))));
}

#[test]
fn colours_are_names_or_hex() {
    assert_eq!(parse_color(" green "), Some(GlowColor::GREEN));
    assert_eq!(parse_color("#FF0000"), Some(GlowColor::new(255, 0, 0)));
    assert_eq!(parse_color("#ff00"), None);
    assert_eq!(parse_color("#gg0000"), None);
    assert_eq!(parse_color("#+f0000"), None);
    assert_eq!(parse_color("white"), None);
}

#[test]
fn a_command_line_counts_utf16_units() {
    // 1025 two-unit characters are 2050 units: too long, though fewer characters than the limit.
    let long = "😀".repeat(1025);
    assert_eq!(parse_arguments(&["toast".into(), long]), Err("That is too long.".into()));
}

#[test]
fn query_values_decode_and_bad_escapes_stay_as_they_are() {
    assert_eq!(unescape("a%20b"), "a b");
    assert_eq!(unescape("%E2%9C%93"), "✓");
    assert_eq!(unescape("100%"), "100%");
    assert_eq!(unescape("%zz"), "%zz");
    assert_eq!(unescape("%FF"), "%FF");
    assert_eq!(unescape("%é"), "%é");
}

#[test]
fn links_keep_the_verb_in_the_host_or_the_path() {
    let link = Link::parse("notch://user@open:80/x?tab=home#frag").unwrap();
    assert_eq!((link.scheme, link.host, link.path, link.query), ("notch", "open", "/x", "tab=home"));
    let link = Link::parse("notch:palette").unwrap();
    assert_eq!((link.host, link.path), ("", "palette"));
    assert!(Link::parse("1notch://x").is_none());
    assert!(Link::parse("no-colon").is_none());
    assert!(Link::parse("notch://in stall").is_none());
}
