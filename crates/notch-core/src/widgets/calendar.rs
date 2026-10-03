//! Calendar feeds: reads iCalendar (.ics) text as published by Google Calendar, Outlook and most
//! other calendars, expands recurring events, and keeps the next few days of events.

use std::collections::HashSet;
use std::str::FromStr;

use chrono::{DateTime, Duration, Local, NaiveDate, NaiveDateTime, NaiveTime, TimeZone, Utc};
use icalendar::parser::{self, Component, Property};
use rrule::{RRule, RRuleSet, Tz, Unvalidated};
use serde::{Serialize, Serializer};

/// How far ahead the service looks for events.
pub const HORIZON_DAYS: i64 = 7;
/// How many events the calendar card lists.
pub const MAX_CALENDAR_ENTRIES: usize = 4;
/// Upper bound on the occurrences of one recurring event inside the window.
const MAX_OCCURRENCES: u16 = 1000;

/// One event on the calendar.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CalendarEntry {
    pub title: String,
    /// In local time. For all-day events, midnight at the start of the day.
    pub start: DateTime<Local>,
    pub end: DateTime<Local>,
    pub is_all_day: bool,
    pub location: Option<String>,
    /// Position of the feed the event came from in the list of feeds, to tell calendars apart.
    pub feed: usize,
}

/// The text is not a calendar.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotACalendar(pub String);

impl std::fmt::Display for NotACalendar {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "The feed is not a valid iCalendar file: {}", self.0)
    }
}

impl std::error::Error for NotACalendar {}

/// Events that overlap `[from, to)`, recurring ones expanded, earliest first.
pub fn read_entries(ics_text: &str, from: DateTime<Local>, to: DateTime<Local>) -> Result<Vec<CalendarEntry>, NotACalendar> {
    let mut unfolded = parser::unfold(ics_text);
    if !unfolded.ends_with('\n') {
        unfolded.push_str("\r\n");
    }
    let roots = parser::read_components(&unfolded).map_err(NotACalendar)?;
    let calendars: Vec<&Component<'_>> = roots.iter().filter(|c| c.name.as_str().eq_ignore_ascii_case("VCALENDAR")).collect();
    if calendars.is_empty() {
        return Err(NotACalendar("there is no calendar in it".to_owned()));
    }

    let events: Vec<Event> = calendars
        .iter()
        .flat_map(|calendar| calendar.components.iter())
        .filter(|c| c.name.as_str().eq_ignore_ascii_case("VEVENT"))
        .filter_map(Event::read)
        .collect();

    // Edited single instances of a recurring event replace the generated ones.
    let overridden: HashSet<(String, i64)> = events
        .iter()
        .filter_map(|e| Some((e.uid.clone()?, e.recurrence_id?.timestamp())))
        .collect();

    let mut entries = Vec::new();
    for event in &events {
        for (start, end) in event.occurrences(from, to, &overridden) {
            if end > from && start < to {
                entries.push(CalendarEntry {
                    title: event.title.clone(),
                    start,
                    end,
                    is_all_day: event.start.kind == Kind::Date,
                    location: event.location.clone(),
                    feed: 0,
                });
            }
        }
    }
    entries.sort_by(|a, b| a.start.cmp(&b.start).then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase())));
    Ok(entries)
}

/// How a time in the feed is anchored.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    /// A date without a time: an all-day event.
    Date,
    /// Wall-clock time wherever the reader is.
    Floating,
    Utc,
    Zoned(chrono_tz::Tz),
}

#[derive(Debug, Clone, Copy)]
struct Time {
    naive: NaiveDateTime,
    kind: Kind,
}

impl Time {
    /// Reads a DATE or DATE-TIME value with its TZID parameter.
    fn parse(property: &Property<'_>) -> Option<Time> {
        let value = property.val.as_str().trim();
        let is_date = param(property, "VALUE").is_some_and(|v| v.eq_ignore_ascii_case("DATE")) || (value.len() == 8 && !value.contains('T'));
        if is_date {
            let date = NaiveDate::parse_from_str(value, "%Y%m%d").ok()?;
            return Some(Time { naive: date.and_time(NaiveTime::MIN), kind: Kind::Date });
        }
        let (text, utc) = match value.strip_suffix(['Z', 'z']) {
            Some(text) => (text, true),
            None => (value, false),
        };
        let naive = NaiveDateTime::parse_from_str(text, "%Y%m%dT%H%M%S").ok()?;
        let kind = if utc {
            Kind::Utc
        } else {
            // A zone this program does not know means the same as none: wall-clock time.
            param(property, "TZID").and_then(zone).map_or(Kind::Floating, Kind::Zoned)
        };
        Some(Time { naive, kind })
    }

    fn tz(self) -> Tz {
        match self.kind {
            Kind::Utc => Tz::UTC,
            Kind::Zoned(zone) => Tz::Tz(zone),
            Kind::Date | Kind::Floating => Tz::LOCAL,
        }
    }

    fn resolve(self) -> Option<DateTime<Tz>> {
        resolve(self.tz(), self.naive)
    }
}

fn resolve(tz: Tz, naive: NaiveDateTime) -> Option<DateTime<Tz>> {
    // A wall-clock time inside a daylight-saving gap does not exist; use the moment just after it.
    tz.from_local_datetime(&naive).earliest().or_else(|| tz.from_local_datetime(&(naive + Duration::hours(1))).earliest())
}

fn param<'a>(property: &'a Property<'_>, key: &str) -> Option<&'a str> {
    property
        .params
        .iter()
        .find(|p| p.key.as_str().eq_ignore_ascii_case(key))
        .and_then(|p| p.val.as_ref())
        .map(|v| v.as_str().trim_matches('"'))
}

/// The IANA zone for a TZID, which may be a Windows zone name as Outlook writes them.
fn zone(tzid: &str) -> Option<chrono_tz::Tz> {
    let tzid = tzid.trim();
    if let Ok(zone) = chrono_tz::Tz::from_str(tzid) {
        return Some(zone);
    }
    // Some producers prefix the name: "/mozilla.org/20050126_1/Europe/Berlin".
    let mut parts = tzid.rsplit('/');
    if let (Some(city), Some(area)) = (parts.next(), parts.next()) {
        if let Ok(zone) = chrono_tz::Tz::from_str(&format!("{area}/{city}")) {
            return Some(zone);
        }
    }
    let iana = match tzid {
        "UTC" | "Coordinated Universal Time" => "UTC",
        "GMT Standard Time" => "Europe/London",
        "W. Europe Standard Time" => "Europe/Berlin",
        "Central Europe Standard Time" => "Europe/Budapest",
        "Central European Standard Time" => "Europe/Warsaw",
        "Romance Standard Time" => "Europe/Paris",
        "E. Europe Standard Time" => "Europe/Chisinau",
        "FLE Standard Time" => "Europe/Kiev",
        "GTB Standard Time" => "Europe/Bucharest",
        "Russian Standard Time" => "Europe/Moscow",
        "Turkey Standard Time" => "Europe/Istanbul",
        "Eastern Standard Time" => "America/New_York",
        "Central Standard Time" => "America/Chicago",
        "Mountain Standard Time" => "America/Denver",
        "US Mountain Standard Time" => "America/Phoenix",
        "Pacific Standard Time" => "America/Los_Angeles",
        "Alaskan Standard Time" => "America/Anchorage",
        "Hawaiian Standard Time" => "Pacific/Honolulu",
        "Atlantic Standard Time" => "America/Halifax",
        "Canada Central Standard Time" => "America/Regina",
        "Central America Standard Time" => "America/Guatemala",
        "Mexico Standard Time" => "America/Mexico_City",
        "SA Pacific Standard Time" => "America/Bogota",
        "E. South America Standard Time" => "America/Sao_Paulo",
        "Argentina Standard Time" => "America/Argentina/Buenos_Aires",
        "South Africa Standard Time" => "Africa/Johannesburg",
        "Egypt Standard Time" => "Africa/Cairo",
        "Israel Standard Time" => "Asia/Jerusalem",
        "Arab Standard Time" => "Asia/Riyadh",
        "Arabian Standard Time" => "Asia/Dubai",
        "India Standard Time" => "Asia/Kolkata",
        "Pakistan Standard Time" => "Asia/Karachi",
        "SE Asia Standard Time" => "Asia/Bangkok",
        "China Standard Time" => "Asia/Shanghai",
        "Singapore Standard Time" => "Asia/Singapore",
        "Taipei Standard Time" => "Asia/Taipei",
        "Tokyo Standard Time" => "Asia/Tokyo",
        "Korea Standard Time" => "Asia/Seoul",
        "AUS Eastern Standard Time" => "Australia/Sydney",
        "E. Australia Standard Time" => "Australia/Brisbane",
        "Cen. Australia Standard Time" => "Australia/Adelaide",
        "W. Australia Standard Time" => "Australia/Perth",
        "New Zealand Standard Time" => "Pacific/Auckland",
        _ => return None,
    };
    chrono_tz::Tz::from_str(iana).ok()
}

/// One VEVENT, read.
struct Event {
    uid: Option<String>,
    title: String,
    location: Option<String>,
    start: Time,
    /// Time from start to end.
    length: Length,
    rule: Option<RRule<Unvalidated>>,
    exdates: Vec<Time>,
    rdates: Vec<Time>,
    recurrence_id: Option<DateTime<Utc>>,
}

/// How long an event lasts: a wall-clock span for events measured in dates, an exact one otherwise.
#[derive(Clone, Copy)]
enum Length {
    Wall(Duration),
    Exact(Duration),
}

impl Length {
    fn duration(self) -> Duration {
        match self {
            Length::Wall(d) | Length::Exact(d) => d,
        }
    }
}

impl Event {
    fn read(component: &Component<'_>) -> Option<Event> {
        let find = |name: &str| component.properties.iter().find(|p| p.name.as_str().eq_ignore_ascii_case(name));
        let all = |name: &'static str| component.properties.iter().filter(move |p| p.name.as_str().eq_ignore_ascii_case(name));
        let text = |name: &str| {
            find(name)
                .map(|p| p.val.clone().unescape_text().as_str().trim().to_owned())
                .filter(|t| !t.is_empty())
        };

        let start = Time::parse(find("DTSTART")?)?;
        let length = if let Some(end) = find("DTEND").and_then(Time::parse) {
            match start.kind {
                Kind::Date | Kind::Floating => Length::Wall((end.naive - start.naive).max(Duration::zero())),
                _ => match (start.resolve(), end.resolve()) {
                    (Some(s), Some(e)) => Length::Exact((e.to_utc() - s.to_utc()).max(Duration::zero())),
                    _ => Length::Exact(Duration::zero()),
                },
            }
        } else if let Some(duration) = find("DURATION").and_then(|p| iso_duration(p.val.as_str())) {
            Length::Wall(duration)
        } else if start.kind == Kind::Date {
            Length::Wall(Duration::days(1))
        } else {
            Length::Exact(Duration::zero())
        };

        let times = |name: &'static str| -> Vec<Time> {
            all(name)
                .flat_map(|p| {
                    p.val.as_str().split(',').filter_map(move |value| {
                        // A period ("start/end") counts by its start.
                        let value = value.split('/').next().unwrap_or(value);
                        let mut single = p.clone();
                        single.val = value.to_owned().into();
                        Time::parse(&single)
                    })
                })
                .collect()
        };

        let rule = find("RRULE").and_then(|p| rule(p.val.as_str(), start));
        let title = text("SUMMARY").unwrap_or_else(|| "(No title)".to_owned());
        Some(Event {
            uid: text("UID"),
            title,
            location: text("LOCATION"),
            start,
            length,
            rule,
            exdates: times("EXDATE"),
            rdates: times("RDATE"),
            recurrence_id: find("RECURRENCE-ID").and_then(Time::parse).and_then(Time::resolve).map(|t| t.to_utc()),
        })
    }

    /// Starts and ends of the occurrences that may overlap `[from, to)`.
    fn occurrences(&self, from: DateTime<Local>, to: DateTime<Local>, overridden: &HashSet<(String, i64)>) -> Vec<(DateTime<Local>, DateTime<Local>)> {
        let Some(first) = self.start.resolve() else {
            return Vec::new();
        };

        let mut starts: Vec<DateTime<Tz>> = vec![first];
        let recurs = self.rule.is_some() || !self.rdates.is_empty();
        if recurs && self.recurrence_id.is_none() {
            starts.extend(self.expand(first, from, to));
        }

        let excluded: HashSet<i64> = self.exdates.iter().filter_map(|t| t.resolve()).map(|t| t.timestamp()).collect();
        starts.sort_by_key(DateTime::timestamp);
        starts.dedup_by_key(|t| t.timestamp());

        starts
            .into_iter()
            .filter(|start| !excluded.contains(&start.timestamp()))
            .filter(|start| self.uid.as_ref().is_none_or(|uid| self.recurrence_id.is_some() || !overridden.contains(&(uid.clone(), start.timestamp()))))
            .filter_map(|start| {
                let local = start.with_timezone(&Local);
                let end = match self.length {
                    Length::Exact(d) => local + d,
                    Length::Wall(d) => resolve(Tz::LOCAL, local.naive_local() + d)?.with_timezone(&Local),
                };
                Some((local, end))
            })
            .collect()
    }

    /// The recurrences of the rule and RDATEs near the window.
    fn expand(&self, first: DateTime<Tz>, from: DateTime<Local>, to: DateTime<Local>) -> Vec<DateTime<Tz>> {
        let mut set = RRuleSet::new(first);
        if let Some(rule) = self.rule.clone().and_then(|r| r.validate(first).ok()) {
            set = set.rrule(rule);
        }
        set = set.set_rdates(self.rdates.iter().filter_map(|t| t.resolve()).collect());

        // Start early enough to catch long events that began before the window.
        let earliest = from.with_timezone(&Utc) - Duration::days(1) - self.length.duration();
        let latest = to.with_timezone(&Utc);
        let (Some(after), Some(before)) = (resolve(Tz::UTC, earliest.naive_utc()), resolve(Tz::UTC, latest.naive_utc())) else {
            return Vec::new();
        };
        set.after(after).before(before).all(MAX_OCCURRENCES).dates
    }
}

/// Reads the rule, with a date-only UNTIL widened to the end of that day for all-day events.
fn rule(text: &str, start: Time) -> Option<RRule<Unvalidated>> {
    let text = if start.kind == Kind::Date {
        text.split(';')
            .map(|part| match part.split_once('=') {
                Some((key, value)) if key.eq_ignore_ascii_case("UNTIL") && value.len() == 8 => format!("{key}={value}T235959"),
                _ => part.to_owned(),
            })
            .collect::<Vec<_>>()
            .join(";")
    } else {
        text.to_owned()
    };
    text.parse().ok()
}

/// "P1D", "PT30M", "P1W", "PT1H30M" and the like.
fn iso_duration(text: &str) -> Option<Duration> {
    let text = text.trim();
    let (negative, text) = match text.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, text.strip_prefix('+').unwrap_or(text)),
    };
    let mut rest = text.strip_prefix('P')?;
    let mut total = Duration::zero();
    let mut in_time = false;
    while !rest.is_empty() {
        if let Some(after) = rest.strip_prefix('T') {
            in_time = true;
            rest = after;
            continue;
        }
        let digits = rest.find(|c: char| !c.is_ascii_digit())?;
        let value: i64 = rest[..digits].parse().ok()?;
        let unit = rest[digits..].chars().next()?;
        total += match (unit, in_time) {
            ('W', false) => Duration::weeks(value),
            ('D', false) => Duration::days(value),
            ('H', true) => Duration::hours(value),
            ('M', true) => Duration::minutes(value),
            ('S', true) => Duration::seconds(value),
            _ => return None,
        };
        rest = &rest[digits + unit.len_utf8()..];
    }
    Some(if negative { -total } else { total })
}

/// Calendar apps hand out webcal:// links, which are plain HTTPS underneath.
pub fn normalize_url(url: &str) -> String {
    let url = url.trim();
    match url.get(..9) {
        Some(scheme) if scheme.eq_ignore_ascii_case("webcal://") => format!("https://{}", &url[9..]),
        _ => url.to_owned(),
    }
}

/// The next week of events from all of the user's feeds.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CalendarSnapshot {
    /// Events from now through the next week, earliest first.
    pub upcoming: Vec<CalendarEntry>,
    /// How many feeds failed to load.
    pub failed_feeds: usize,
}

impl CalendarSnapshot {
    /// Loads every non-blank feed with `fetch` (which returns the feed's text) and merges the events.
    /// A feed that cannot be fetched or read counts as failed; the others still show.
    pub fn load<E>(feed_urls: &[String], now: DateTime<Local>, mut fetch: impl FnMut(&str) -> Result<String, E>) -> CalendarSnapshot {
        let horizon = now + Duration::days(HORIZON_DAYS);
        let mut upcoming = Vec::new();
        let mut failed_feeds = 0;

        for (feed, url) in feed_urls.iter().filter(|u| !u.trim().is_empty()).enumerate() {
            match fetch(&normalize_url(url)).ok().and_then(|text| read_entries(&text, now, horizon).ok()) {
                Some(entries) => upcoming.extend(entries.into_iter().map(|e| CalendarEntry { feed, ..e })),
                None => failed_feeds += 1,
            }
        }
        upcoming.sort_by_key(|e| e.start);
        CalendarSnapshot { upcoming, failed_feeds }
    }

    /// The events the calendar card lists at `now`: those not yet over, earliest first, at most `max`.
    pub fn rows(&self, now: DateTime<Local>, max: usize) -> Vec<CalendarRow> {
        self.upcoming
            .iter()
            .filter(|e| e.end > now)
            .take(max)
            .map(|e| CalendarRow {
                start: e.start,
                all_day: e.is_all_day,
                title: e.title.clone(),
                feed: e.feed,
                today: e.start.date_naive() <= now.date_naive(),
            })
            .collect()
    }
}

/// One line of the calendar card. The UI words the time in the reader's locale.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CalendarRow {
    /// Sent as milliseconds since the Unix epoch.
    #[serde(serialize_with = "millis")]
    pub start: DateTime<Local>,
    pub all_day: bool,
    pub title: String,
    pub feed: usize,
    /// The event starts today or has already started.
    pub today: bool,
}

fn millis<S: Serializer>(time: &DateTime<Local>, serializer: S) -> Result<S::Ok, S::Error> {
    serializer.serialize_i64(time.timestamp_millis())
}

#[cfg(test)]
mod tests {
    use super::*;

    const ICS: &str = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n\
        BEGIN:VEVENT\r\nUID:standup\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20261001T090000Z\r\nDTEND:20261001T091500Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nSUMMARY:Standup\r\nLOCATION:Room 2\r\nEND:VEVENT\r\n\
        BEGIN:VEVENT\r\nUID:holiday\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:20261002\r\nDTEND;VALUE=DATE:20261003\r\nSUMMARY:Holiday\r\nEND:VEVENT\r\n\
        BEGIN:VEVENT\r\nUID:old\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\nSUMMARY:Long gone\r\nEND:VEVENT\r\n\
        END:VCALENDAR\r\n";

    fn utc(y: i32, m: u32, d: u32, h: u32) -> DateTime<Local> {
        Utc.with_ymd_and_hms(y, m, d, h, 0, 0).single().expect("valid time").with_timezone(&Local)
    }

    #[test]
    fn webcal_links_become_https() {
        assert_eq!(normalize_url(" webcal://example.com/cal.ics "), "https://example.com/cal.ics");
        assert_eq!(normalize_url("https://example.com/cal.ics"), "https://example.com/cal.ics");
    }

    #[test]
    fn feed_expands_recurrences_within_the_window() {
        let from = utc(2026, 10, 1, 0);
        let entries = read_entries(ICS, from, from + Duration::days(3)).expect("calendar");

        assert_eq!(entries.iter().filter(|e| e.title == "Standup").count(), 3);
        assert!(entries.iter().all(|e| e.title != "Long gone"));

        let standup = entries.iter().find(|e| e.title == "Standup").expect("standup");
        assert_eq!(standup.start.to_utc(), Utc.with_ymd_and_hms(2026, 10, 1, 9, 0, 0).single().expect("time"));
        assert_eq!(standup.end - standup.start, Duration::minutes(15));
        assert_eq!(standup.location.as_deref(), Some("Room 2"));
        assert!(!standup.is_all_day);
    }

    #[test]
    fn feed_reads_all_day_events_as_local_dates() {
        let from = utc(2026, 10, 1, 0);
        let entries = read_entries(ICS, from, from + Duration::days(3)).expect("calendar");
        let holiday = entries.iter().find(|e| e.title == "Holiday").expect("holiday");

        assert!(holiday.is_all_day);
        assert_eq!(holiday.start.naive_local(), NaiveDate::from_ymd_opt(2026, 10, 2).expect("date").and_time(NaiveTime::MIN));
        assert_eq!(holiday.end - holiday.start, Duration::days(1));
    }

    #[test]
    fn feed_rejects_text_that_is_not_a_calendar() {
        let now = Local::now();
        assert!(read_entries("<html>Sign in</html>", now, now + Duration::days(1)).is_err());
    }

    #[test]
    fn zoned_times_exceptions_and_titles_are_honoured() {
        let ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n\
            BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=America/New_York:20261001T090000\r\nDTEND;TZID=America/New_York:20261001T100000\r\n\
            RRULE:FREQ=DAILY;COUNT=3\r\nEXDATE;TZID=America/New_York:20261002T090000\r\nSUMMARY:Sync\\, weekly\r\nEND:VEVENT\r\n\
            BEGIN:VEVENT\r\nUID:b\r\nDTSTART:20261001T120000Z\r\nSUMMARY:  \r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        let from = utc(2026, 10, 1, 0);
        let entries = read_entries(ics, from, from + Duration::days(4)).expect("calendar");

        let sync: Vec<_> = entries.iter().filter(|e| e.title == "Sync, weekly").collect();
        assert_eq!(sync.len(), 2, "one of three days is excluded");
        // 09:00 in New York in October is 13:00 UTC.
        assert_eq!(sync[0].start.to_utc(), Utc.with_ymd_and_hms(2026, 10, 1, 13, 0, 0).single().expect("time"));
        assert_eq!(sync[1].start.to_utc(), Utc.with_ymd_and_hms(2026, 10, 3, 13, 0, 0).single().expect("time"));
        assert!(entries.iter().any(|e| e.title == "(No title)"));
    }

    #[test]
    fn edited_instances_replace_the_generated_one() {
        let ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n\
            BEGIN:VEVENT\r\nUID:a\r\nDTSTART:20261001T090000Z\r\nDTEND:20261001T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Gym\r\nEND:VEVENT\r\n\
            BEGIN:VEVENT\r\nUID:a\r\nRECURRENCE-ID:20261002T090000Z\r\nDTSTART:20261002T170000Z\r\nDTEND:20261002T180000Z\r\nSUMMARY:Gym (moved)\r\nEND:VEVENT\r\n\
            END:VCALENDAR\r\n";
        let from = utc(2026, 10, 1, 0);
        let titles: Vec<String> = read_entries(ics, from, from + Duration::days(4)).expect("calendar").into_iter().map(|e| e.title).collect();
        assert_eq!(titles, ["Gym", "Gym (moved)", "Gym"]);
    }

    #[test]
    fn all_day_events_may_repeat_until_a_date() {
        let ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n\
            BEGIN:VEVENT\r\nUID:a\r\nDTSTART;VALUE=DATE:20261001\r\nRRULE:FREQ=DAILY;UNTIL=20261002\r\nSUMMARY:Trip\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        let from = utc(2026, 9, 30, 0);
        let entries = read_entries(ics, from, from + Duration::days(10)).expect("calendar");
        assert_eq!(entries.len(), 2);
        assert!(entries.iter().all(|e| e.is_all_day));
    }

    #[test]
    fn iso_durations_are_read() {
        assert_eq!(iso_duration("PT1H30M"), Some(Duration::minutes(90)));
        assert_eq!(iso_duration("P1W"), Some(Duration::weeks(1)));
        assert_eq!(iso_duration("P2DT3H"), Some(Duration::hours(51)));
        assert_eq!(iso_duration("1H"), None);
    }

    #[test]
    fn snapshot_merges_feeds_and_counts_failures() {
        let now = utc(2026, 10, 1, 0);
        let urls: Vec<String> = ["webcal://feeds.test/good.ics", "https://feeds.test/missing.ics"].iter().map(|u| (*u).to_owned()).collect();
        let mut asked = Vec::new();
        let snapshot = CalendarSnapshot::load(&urls, now, |url| {
            asked.push(url.to_owned());
            if url.contains("good") { Ok(ICS.to_owned()) } else { Err("404") }
        });

        assert_eq!(asked[0], "https://feeds.test/good.ics");
        assert_eq!(snapshot.failed_feeds, 1);
        assert!(snapshot.upcoming.iter().any(|e| e.title == "Holiday"));
        assert!(snapshot.upcoming.iter().all(|e| e.feed == 0));
        assert!(snapshot.upcoming.windows(2).all(|pair| pair[0].start <= pair[1].start));
    }

    #[test]
    fn rows_skip_finished_events_and_cap_the_list() {
        let now = utc(2026, 10, 1, 12);
        let make = |title: &str, start_hour: i64, hours: i64| {
            let start = now + Duration::hours(start_hour);
            CalendarEntry { title: title.to_owned(), start, end: start + Duration::hours(hours), is_all_day: false, location: None, feed: 1 }
        };
        let snapshot = CalendarSnapshot {
            upcoming: vec![make("over", -3, 1), make("now", -1, 2), make("a", 1, 1), make("b", 2, 1), make("c", 3, 1), make("d", 4, 1)],
            failed_feeds: 0,
        };
        let titles: Vec<String> = snapshot.rows(now, MAX_CALENDAR_ENTRIES).into_iter().map(|r| r.title).collect();
        assert_eq!(titles, ["now", "a", "b", "c"]);
        assert!(snapshot.rows(now, 1)[0].today, "an event in progress counts as today");
    }
}
