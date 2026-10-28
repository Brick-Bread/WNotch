//! Ranks the command palette's entries against what was typed. Ported from
//! `src/Notch.Core/Shell/PaletteSearch.cs`.
//!
//! Positions and lengths are in UTF-16 units, as C# counts them, so the scores come out the same.

pub const DEFAULT_MAX: usize = 8;

/// The entries that match every word of `query`, best first; the first `max` entries in their
/// given order when nothing was typed. A word matches at the start of the title or of one of its
/// words, anywhere in it, or as letters in order ("tm" finds "Timer"); the keywords count a little
/// less than the title.
pub fn rank<'a, T>(
    items: &'a [T],
    query: Option<&str>,
    title: impl Fn(&T) -> String,
    keywords: impl Fn(&T) -> Option<String>,
    max: usize,
) -> Vec<&'a T> {
    let words: Vec<Vec<u16>> =
        query.unwrap_or_default().split([' ', '\t']).filter(|word| !word.is_empty()).map(lower).collect();
    if words.is_empty() {
        return items.iter().take(max).collect();
    }

    let mut scored = Vec::new();
    for (index, item) in items.iter().enumerate() {
        let name = lower(&title(item));
        let extra = lower(&keywords(item).unwrap_or_default());

        let mut total = 0;
        for word in &words {
            let score = score(word, &name).max(score(word, &extra) * 7 / 10);
            if score == 0 {
                total = 0;
                break;
            }

            total += score;
        }

        if total > 0 {
            scored.push((item, total, index));
        }
    }

    scored.sort_by(|a, b| b.1.cmp(&a.1).then(a.2.cmp(&b.2)));
    scored.into_iter().take(max).map(|(item, _, _)| item).collect()
}

fn score(word: &[u16], text: &[u16]) -> i32 {
    if text.is_empty() {
        return 0;
    }

    if text.starts_with(word) {
        return 100;
    }

    let at = text.windows(word.len()).position(|window| window == word);
    if at.is_some_and(|at| at > 0 && !is_letter_or_digit(text[at - 1])) {
        return 80;
    }

    if at.is_some() {
        return 50;
    }

    // The letters in order, with a small bonus the closer together they are.
    let mut position = 0;
    let mut first = None;
    for &c in word {
        let Some(found) = text[position..].iter().position(|&t| t == c).map(|found| found + position) else {
            return 0;
        };

        first = first.or(Some(found));
        position = found + 1;
    }

    let span = (position - first.unwrap_or(0)) as i32;
    5.max(30 - (span - word.len() as i32))
}

/// `ToLowerInvariant`, as UTF-16 units. İ is the one character whose full lowercase mapping is
/// longer than its simple one; the simple one is its first character.
fn lower(text: &str) -> Vec<u16> {
    text.chars().map(|c| c.to_lowercase().next().unwrap_or(c)).collect::<String>().encode_utf16().collect()
}

/// `char.IsLetterOrDigit` on one UTF-16 unit; half a surrogate pair is neither. Rust's test also
/// counts letter-like numbers such as Ⅻ and ², which C# does not.
fn is_letter_or_digit(unit: u16) -> bool {
    char::from_u32(u32::from(unit)).is_some_and(char::is_alphanumeric)
}

#[cfg(test)]
mod tests {
    use super::*;

    const ITEMS: [&str; 7] = [
        "Open Home",
        "Open Terminal",
        "Start a 25 minute timer",
        "Pause the timer",
        "Open settings",
        "New Claude session",
        "Switch on Break reminder",
    ];

    fn titles<'a>(items: &'a [&'a str], query: Option<&str>, max: usize) -> Vec<&'a str> {
        rank(items, query, |item| item.to_string(), |_| None, max).into_iter().copied().collect()
    }

    fn rank_items(query: Option<&str>, max: usize) -> Vec<&'static str> {
        titles(&ITEMS, query, max)
    }

    #[test]
    fn with_nothing_typed_the_first_entries_are_listed_in_order() {
        assert_eq!(rank_items(Some(""), 8), ITEMS);
        assert_eq!(rank_items(None, 2), ITEMS[..2]);
        assert_eq!(rank_items(Some("   "), 3), ITEMS[..3]);
    }

    #[test]
    fn a_word_at_the_start_beats_one_inside() {
        assert_eq!(rank_items(Some("open"), 8)[0], "Open Home");
        assert_eq!(rank_items(Some("pau"), 8), ["Pause the timer"]);

        // "timer" starts a word in both, but "Pause the timer" has it later; both match.
        assert_eq!(rank_items(Some("timer"), 8), ["Start a 25 minute timer", "Pause the timer"]);
    }

    #[test]
    fn every_word_must_match() {
        assert_eq!(rank_items(Some("open term"), 8), ["Open Terminal"]);
        assert!(rank_items(Some("open banana"), 8).is_empty());
    }

    #[test]
    fn a_title_that_starts_with_the_word_beats_one_that_only_contains_it() {
        assert_eq!(titles(&["Unpause", "Pause"], Some("pause"), DEFAULT_MAX), ["Pause", "Unpause"]);
    }

    #[test]
    fn letters_in_order_still_find_it() {
        assert!(rank_items(Some("opst"), 8).contains(&"Open settings"));
        assert!(!rank_items(Some("opst"), 8).contains(&"Open Home"));
    }

    #[test]
    fn case_does_not_matter() {
        assert_eq!(rank_items(Some("claude"), 8), rank_items(Some("CLAUDE"), 8));
    }

    #[test]
    fn keywords_find_an_entry_the_title_does_not() {
        let items = ["Open Settings", "Open Home"];
        let ranked = rank(
            &items,
            Some("preferences"),
            |i| i.to_string(),
            |i| (*i == "Open Settings").then(|| "preferences options".to_owned()),
            DEFAULT_MAX,
        );
        assert_eq!(ranked, [&"Open Settings"]);
    }

    #[test]
    fn the_list_is_cut_to_the_maximum() {
        assert_eq!(rank_items(Some("o"), 3).len(), 3);
    }

    #[test]
    fn equal_matches_keep_their_given_order() {
        assert_eq!(titles(&["Open B", "Open A", "Open C"], Some("open"), DEFAULT_MAX), ["Open B", "Open A", "Open C"]);
    }
}
