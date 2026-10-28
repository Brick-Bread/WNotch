//! `StringComparison.OrdinalIgnoreCase`, which the agent and terminal code compare names and
//! paths with.
//!
//! .NET folds each character to its simple invariant upper case. For ASCII that is
//! `to_ascii_uppercase`; .NET never folds a non-ASCII character onto an ASCII one (`ı` does not
//! match `I`), so only non-ASCII results are taken from Rust's mapping.

use std::cmp::Ordering;

fn fold(c: char) -> char {
    if c.is_ascii() {
        return c.to_ascii_uppercase();
    }

    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) if !single.is_ascii() => single,
        _ => c,
    }
}

pub(crate) fn eq(a: &str, b: &str) -> bool {
    a.chars().map(fold).eq(b.chars().map(fold))
}

pub(crate) fn cmp(a: &str, b: &str) -> Ordering {
    a.chars().map(fold).cmp(b.chars().map(fold))
}

/// The rest of `text` after `prefix`, when `text` starts with it.
pub(crate) fn strip_prefix<'a>(text: &'a str, prefix: &str) -> Option<&'a str> {
    let mut rest = text.char_indices();
    for wanted in prefix.chars() {
        let (_, c) = rest.next()?;
        if fold(c) != fold(wanted) {
            return None;
        }
    }
    Some(rest.next().map_or("", |(i, _)| &text[i..]))
}

pub(crate) fn ends_with(text: &str, suffix: &str) -> bool {
    let count = suffix.chars().count();
    let Some((start, _)) = text.char_indices().rev().nth(count.wrapping_sub(1)) else {
        return count == 0;
    };
    count > 0 && eq(&text[start..], suffix)
}

pub(crate) fn contains(text: &str, needle: &str) -> bool {
    needle.is_empty() || text.char_indices().any(|(i, _)| strip_prefix(&text[i..], needle).is_some())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folds_like_dotnet() {
        assert!(eq("Notch.Hook", "notch.hook"));
        assert!(eq("é", "É"));
        assert!(!eq("aıb", "AIB"));
        assert!(contains(r"C:\Program Files\WindowsApps\Claude_1", r"\windowsapps\"));
        assert!(ends_with("codex.CMD", ".cmd"));
        assert!(!ends_with("md", ".md"));
        assert!(ends_with("x", ""));
        assert_eq!(strip_prefix("Description: x", "description:"), Some(" x"));
        assert_eq!(strip_prefix("name:", "name:"), Some(""));
        assert_eq!(cmp("alpha", "Beta"), Ordering::Less);
    }
}
