//! Which Windows notifications reach the pill, and how they look there. Ported from
//! `src/Notch.Core/Shell/NotificationMirror.cs`.

use std::collections::HashSet;
use std::time::Duration;

use crate::activities::{Activity, ActivityTier, Glow, GlowColor, GlowPattern};
use crate::automation::parser::utf16_len;

pub const ACTIVITY_PREFIX: &str = "notification.";
pub const MAX_TITLE_LENGTH: usize = 80;
pub const MAX_APP_LENGTH: usize = 40;
pub const LIFETIME: Duration = Duration::from_secs(5);

/// The Segoe Fluent Icons glyph the notice shows.
const GLYPH: &str = "\u{EA8F}";

/// A Windows notification as the watcher reads it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotificationInfo {
    pub id: i64,
    pub app: String,
    /// The first line of the toast: usually the sender or the subject.
    pub title: String,
    /// The rest of its text. Never shown; kept out of the pill on purpose.
    pub body: Option<String>,
}

/// Whether an app's notifications are shown: all of them when `allowed_apps` is empty, otherwise
/// only those from an app whose name contains one of the listed words, ignoring case.
pub fn allows(app: &str, allowed_apps: &[impl AsRef<str>]) -> bool {
    allowed_apps.is_empty()
        || allowed_apps
            .iter()
            .map(AsRef::as_ref)
            .any(|allowed| !allowed.trim().is_empty() && fold_case(app).contains(&fold_case(allowed.trim())))
}

/// The pill's short notice for a notification: the sender or subject as the title and the app as
/// the detail. The message itself stays out of the pill, where anyone looking at the screen could read it.
pub fn to_activity(notification: &NotificationInfo) -> Option<Activity> {
    let title = clip(&notification.title, MAX_TITLE_LENGTH);
    let app = clip(&notification.app, MAX_APP_LENGTH);
    if title.is_empty() && app.is_empty() {
        return None;
    }

    let detail = (!title.is_empty() && !app.is_empty()).then(|| app.clone());
    let mut activity = Activity::new(
        format!("{ACTIVITY_PREFIX}{}", notification.id),
        ActivityTier::Transient,
        if title.is_empty() { app } else { title },
    );
    activity.detail = detail;
    activity.glyph = Some(GLYPH.to_owned());
    activity.glow = Some(Glow::with_strength(GlowColor::BLUE, GlowPattern::Flash, 0.8));
    activity.lifetime = Some(LIFETIME);
    Some(activity)
}

/// One line, at most `length` UTF-16 units, ending in "…" when it was cut. A cut that would split
/// a surrogate pair drops the whole pair, where C# keeps half of it.
fn clip(text: &str, length: usize) -> String {
    let single = text.split(['\r', '\n']).map(str::trim).filter(|line| !line.is_empty()).collect::<Vec<_>>().join(" ");
    if utf16_len(&single) <= length {
        return single;
    }

    let mut clipped = String::new();
    let mut units = 0;
    for c in single.chars() {
        units += c.len_utf16();
        if units > length - 1 {
            break;
        }
        clipped.push(c);
    }
    clipped.push('…');
    clipped
}

/// `StringComparison.OrdinalIgnoreCase`: each character's simple uppercase mapping, except that
/// no other character becomes an ASCII one (ı stays apart from I, ſ from S).
fn fold_case(text: &str) -> String {
    text.chars()
        .map(|c| {
            let mut upper = c.to_uppercase();
            match (upper.next(), upper.next()) {
                (Some(single), None) if single.is_ascii() == c.is_ascii() => single,
                _ => c,
            }
        })
        .collect()
}

/// Remembers which notifications were already seen, so a poll only reports new ones.
#[derive(Debug, Default)]
pub struct NotificationDiff {
    seen: HashSet<i64>,
    primed: bool,
}

impl NotificationDiff {
    pub fn new() -> Self {
        Self::default()
    }

    /// The notifications in `current` that have not been seen. The first call only learns what is
    /// already there and reports nothing, so switching the feature on does not replay the action centre.
    pub fn fresh<'a>(&mut self, current: &'a [NotificationInfo]) -> Vec<&'a NotificationInfo> {
        let mut fresh = Vec::new();
        for notification in current {
            if self.seen.insert(notification.id) && self.primed {
                fresh.push(notification);
            }
        }

        // Forget ones that were dismissed, so the set does not grow forever and an id that comes back counts as new.
        let ids: HashSet<i64> = current.iter().map(|n| n.id).collect();
        self.seen.retain(|id| ids.contains(id));
        self.primed = true;
        fresh
    }

    pub fn reset(&mut self) {
        self.seen.clear();
        self.primed = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn n(id: i64) -> NotificationInfo {
        with(id, "Mail", "Ada Lovelace")
    }

    fn with(id: i64, app: &str, title: &str) -> NotificationInfo {
        NotificationInfo { id, app: app.into(), title: title.into(), body: Some("Lunch tomorrow?".into()) }
    }

    fn ids(fresh: Vec<&NotificationInfo>) -> Vec<i64> {
        fresh.iter().map(|n| n.id).collect()
    }

    #[test]
    fn with_no_list_every_app_is_shown() {
        assert!(allows("Anything", &[] as &[&str]));
    }

    #[test]
    fn a_list_limits_it_to_the_apps_named_in_any_case() {
        let allowed = ["outlook", " Teams "];
        assert!(allows("Microsoft Outlook", &allowed));
        assert!(allows("Microsoft Teams (work)", &allowed));
        assert!(!allows("Slack", &allowed));
    }

    #[test]
    fn case_is_ignored_as_ordinal_ignore_case_ignores_it() {
        assert!(allows("ÉCOLE", &["école"]));
        assert!(!allows("Iİ", &["ı"]));
        assert!(!allows("Slack", &["ſlack"]));
    }

    #[test]
    fn blank_lines_in_the_list_do_not_let_everything_in() {
        assert!(!allows("Slack", &["", "  "]));
    }

    #[test]
    fn the_pill_shows_the_sender_and_the_app_but_never_the_message() {
        let activity = to_activity(&n(7)).unwrap();

        assert_eq!(activity.id, "notification.7");
        assert_eq!(activity.tier, ActivityTier::Transient);
        assert_eq!(activity.title, "Ada Lovelace");
        assert_eq!(activity.detail.as_deref(), Some("Mail"));
        assert!(!format!("{}{}", activity.title, activity.detail.unwrap_or_default()).contains("Lunch"));
        assert_eq!(activity.lifetime, Some(LIFETIME));
    }

    #[test]
    fn long_and_multi_line_titles_are_shortened_to_one_line() {
        let activity =
            to_activity(&with(1, "Mail", &format!("first line\r\nsecond line {}", "x".repeat(200)))).unwrap();
        assert!(!activity.title.contains('\n'));
        assert!(utf16_len(&activity.title) <= MAX_TITLE_LENGTH);
        assert!(activity.title.ends_with('…'));
    }

    #[test]
    fn a_notification_without_a_title_falls_back_to_the_app() {
        let activity = to_activity(&with(2, "Backup", "")).unwrap();
        assert_eq!(activity.title, "Backup");
        assert_eq!(activity.detail, None);
    }

    #[test]
    fn a_notification_with_nothing_to_show_is_skipped() {
        assert_eq!(to_activity(&with(3, " ", "")), None);
    }

    #[test]
    fn the_first_poll_learns_what_is_there_and_reports_nothing() {
        let mut diff = NotificationDiff::new();
        assert!(diff.fresh(&[n(1), n(2)]).is_empty());
        assert!(diff.fresh(&[n(1), n(2)]).is_empty());
    }

    #[test]
    fn only_new_notifications_are_reported_afterwards() {
        let mut diff = NotificationDiff::new();
        diff.fresh(&[n(1)]);

        assert_eq!(ids(diff.fresh(&[n(1), n(2), n(3)])), [2, 3]);
        assert!(diff.fresh(&[n(1), n(2), n(3)]).is_empty());
    }

    #[test]
    fn a_dismissed_notification_that_comes_back_counts_as_new() {
        let mut diff = NotificationDiff::new();
        diff.fresh(&[n(1)]);
        diff.fresh(&[]);

        assert_eq!(diff.fresh(&[n(1)]).len(), 1);
    }

    #[test]
    fn resetting_forgets_everything() {
        let mut diff = NotificationDiff::new();
        diff.fresh(&[n(1)]);
        diff.reset();

        assert!(diff.fresh(&[n(1), n(2)]).is_empty());
    }
}
