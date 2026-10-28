//! Writes JSON the way System.Text.Json's `JsonNode.ToJsonString` does with its default
//! encoder, so the files and pipe lines Notch writes come out byte for byte as the C# app's.
//!
//! - Strings escape `"`, `&`, `'`, `+`, `<`, `>`, `` ` ``, control characters and everything
//!   outside ASCII as `\uXXXX` (upper-case hex, UTF-16 units); `\n`, `\r`, `\t`, `\b`, `\f` and
//!   `\\` keep their short forms.
//! - Indented output uses two spaces, `": "` after a name, `[]` and `{}` for empty containers, no
//!   newline at the end, and `\r\n` between lines: `Environment.NewLine` on Windows, where the app
//!   runs.
//!
//! One difference: C# keeps a parsed number's own spelling (`1.50`, `1e3`), while `serde_json`
//! writes its value (`1.5`, `1000.0`). Integers come out the same.

use std::fmt::Write;

use serde_json::Value;

const NEWLINE: &str = "\r\n";
const INDENT: &str = "  ";

/// `ToJsonString()` with no options.
pub(crate) fn compact(value: &Value) -> String {
    let mut out = String::new();
    write(&mut out, value, None);
    out
}

/// `ToJsonString(new JsonSerializerOptions { WriteIndented = true })`.
pub(crate) fn indented(value: &Value) -> String {
    let mut out = String::new();
    write(&mut out, value, Some(0));
    out
}

fn write(out: &mut String, value: &Value, depth: Option<usize>) {
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => out.push_str(&n.to_string()),
        Value::String(s) => string(out, s),
        Value::Array(items) => {
            if items.is_empty() {
                out.push_str("[]");
                return;
            }

            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                line(out, depth.map(|d| d + 1));
                write(out, item, depth.map(|d| d + 1));
            }
            line(out, depth);
            out.push(']');
        }
        Value::Object(map) => {
            if map.is_empty() {
                out.push_str("{}");
                return;
            }

            out.push('{');
            for (i, (key, item)) in map.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                line(out, depth.map(|d| d + 1));
                string(out, key);
                out.push_str(if depth.is_some() { ": " } else { ":" });
                write(out, item, depth.map(|d| d + 1));
            }
            line(out, depth);
            out.push('}');
        }
    }
}

fn line(out: &mut String, depth: Option<usize>) {
    if let Some(depth) = depth {
        out.push_str(NEWLINE);
        for _ in 0..depth {
            out.push_str(INDENT);
        }
    }
}

fn string(out: &mut String, text: &str) {
    out.push('"');
    for c in text.chars() {
        match c {
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\\' => out.push_str("\\\\"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            ' '..='~' if !matches!(c, '"' | '&' | '\'' | '+' | '<' | '>' | '`') => out.push(c),
            _ => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    // Writing to a String cannot fail.
                    let _ = write!(out, "\\u{unit:04X}");
                }
            }
        }
    }
    out.push('"');
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strings_are_escaped_as_the_default_encoder_does() {
        let value = Value::String("x\"<>&'+`\u{e9}\u{7f}\u{1}\n\t/\u{1F600}".into());
        assert_eq!(
            compact(&value),
            r#""x\u0022\u003C\u003E\u0026\u0027\u002B\u0060\u00E9\u007F\u0001\n\t/\uD83D\uDE00""#
        );
    }

    #[test]
    fn indented_output_matches_write_indented() {
        let value: Value = serde_json::from_str(r#"{"d":[],"e":{},"g":[1,{"h":null}]}"#).unwrap();
        assert_eq!(
            indented(&value),
            "{\r\n  \"d\": [],\r\n  \"e\": {},\r\n  \"g\": [\r\n    1,\r\n    {\r\n      \"h\": null\r\n    }\r\n  ]\r\n}"
        );
        assert_eq!(compact(&value), r#"{"d":[],"e":{},"g":[1,{"h":null}]}"#);
    }
}
