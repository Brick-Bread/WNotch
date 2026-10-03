//! Lenient JSON for hand-written files: `//` and `/* */` comments and trailing commas are
//! accepted in `plugin.json`, as the C# plugin loader did.

/// Removes comments and trailing commas outside of strings, leaving valid JSON for `serde_json`.
pub fn relax(text: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    let mut out = String::with_capacity(text.len());
    let mut i = 0;
    let mut in_string = false;
    while i < chars.len() {
        let c = chars[i];
        if in_string {
            out.push(c);
            if c == '\\' && i + 1 < chars.len() {
                out.push(chars[i + 1]);
                i += 1;
            } else if c == '"' {
                in_string = false;
            }
            i += 1;
            continue;
        }
        match c {
            '"' => {
                in_string = true;
                out.push(c);
            }
            '/' if chars.get(i + 1) == Some(&'/') => {
                while i < chars.len() && chars[i] != '\n' {
                    i += 1;
                }
                continue;
            }
            '/' if chars.get(i + 1) == Some(&'*') => {
                i += 2;
                while i + 1 < chars.len() && !(chars[i] == '*' && chars[i + 1] == '/') {
                    i += 1;
                }
                i += 2;
                continue;
            }
            ',' => {
                let next = chars[i + 1..].iter().find(|c| !c.is_whitespace());
                if !matches!(next, Some('}') | Some(']')) {
                    out.push(c);
                }
            }
            _ => out.push(c),
        }
        i += 1;
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_comments_and_trailing_commas() {
        let text = "{ // note\n \"a\": [1, 2,], /* x */ \"b\": \"// not a comment, }\", }";
        let value: serde_json::Value = serde_json::from_str(&relax(text)).unwrap();
        assert_eq!(value["a"], serde_json::json!([1, 2]));
        assert_eq!(value["b"], "// not a comment, }");
    }

    #[test]
    fn leaves_valid_json_alone() {
        let text = r#"{"a":"x\"y","b":[true,null]}"#;
        assert_eq!(relax(text), text);
    }
}
