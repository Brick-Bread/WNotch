//! Reads iCalendar (.ics) feeds and keeps the next few days of events. Ported from
//! `src/Notch.Core/Widgets/CalendarFeed.cs` and `src/Notch.Core/Widgets/CalendarService.cs`.
//!
//! C# reads the text with Ical.Net. Here a small reader takes the parts the calendar widget uses
//! (events with their start, end or duration, title, location, RRULE, RDATE, EXDATE and
//! RECURRENCE-ID), and the `rrule` crate expands the recurrences. Time zones are found as Ical.Net
//! finds them: a TZID is an IANA id, a Windows id (as Outlook and Exchange write, such as
//! "W. Europe Standard Time"), a Windows display name, or text with an IANA id in it, such as
//! "/mozilla.org/20050126_1/Europe/Berlin". A TZID none of these match, like a date without a
//! time and a time without a zone ("floating"), means wall-clock time wherever the reader is.
//! VTIMEZONE definitions are not read, as Ical.Net does not read them either.

use std::str::FromStr;
use std::time::Duration;

use chrono::{DateTime, NaiveDate, NaiveDateTime, NaiveTime, Offset, TimeDelta, TimeZone, Utc};
use chrono_tz::Tz;
use rrule::{RRule, RRuleSet, Unvalidated};

use super::clock_zones;
use super::windows_zones::WINDOWS_ZONES;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CalendarEntry {
    pub title: String,
    /// For all-day events, midnight at the start of the day where the reader is. C# keeps local
    /// time; this keeps the instant, which the app shows in local time.
    pub start: DateTime<Utc>,
    pub end: DateTime<Utc>,
    pub is_all_day: bool,
    pub location: Option<String>,
    /// Position of the feed the event came from in the list of feeds, to tell calendars apart.
    pub feed: usize,
}

const INVALID: &str = "The feed is not a valid iCalendar file.";

/// Events that overlap [`from`, `to`), recurring ones expanded, earliest first. `local` is the
/// reader's time zone (`chrono::Local` in the app), for all-day and floating times.
///
/// Errors when the text is not a calendar.
pub fn read_entries<Z: TimeZone>(
    ics_text: &str,
    from: DateTime<Utc>,
    to: DateTime<Utc>,
    local: &Z,
) -> Result<Vec<CalendarEntry>, String> {
    let events = parse(ics_text).ok_or_else(|| INVALID.to_owned())?;

    let mut entries = Vec::new();
    for event in &events {
        let overridden: Vec<&Time> = events
            .iter()
            .filter(|o| o.uid.is_some() && o.uid == event.uid)
            .filter_map(|o| o.recurrence_id.as_ref())
            .collect();

        for (start, end) in occurrences(event, &overridden, from, to, local) {
            if end > from && start < to {
                entries.push(CalendarEntry {
                    title: event.summary.clone().unwrap_or_else(|| "(No title)".into()),
                    start,
                    end,
                    is_all_day: matches!(event.start, Some(Time::Date(_))),
                    location: event.location.clone(),
                    feed: 0,
                });
            }
        }
    }

    // C# orders titles by the current culture; this puts them in order ignoring case first.
    entries.sort_by(|a, b| {
        a.start
            .cmp(&b.start)
            .then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase()))
            .then(a.title.cmp(&b.title))
    });
    Ok(entries)
}

/// Downloads the user's calendar feeds and keeps the next few days of events.
///
/// C# fetches the feeds itself. Here the caller does, which keeps HTTP out of notch-core: it
/// fetches each of [`Self::feed_urls`] (webcal:// already turned into https://) and hands the
/// answers to [`Self::refresh`] in the same order, with the time the refresh started. Any failed
/// fetch counts as a failed feed. When the whole refresh is given up, such as on cancellation, the
/// caller does not call `refresh`, so the previous events stay. The app refreshes every 15 minutes
/// and after the settings are saved.
#[derive(Debug, Clone, Default)]
pub struct CalendarService {
    upcoming: Vec<CalendarEntry>,
    failed_feeds: usize,
}

impl CalendarService {
    pub const HORIZON: Duration = Duration::from_secs(7 * 24 * 60 * 60);

    pub fn new() -> Self {
        Self::default()
    }

    /// Events from now through the next week, earliest first.
    pub fn upcoming(&self) -> &[CalendarEntry] {
        &self.upcoming
    }

    /// How many feeds failed to load in the last refresh.
    pub fn failed_feeds(&self) -> usize {
        self.failed_feeds
    }

    /// What to fetch for a refresh: the feeds that are not blank, as [`Self::normalize_url`] makes them.
    pub fn feed_urls<S: AsRef<str>>(feed_urls: &[S]) -> Vec<String> {
        feed_urls.iter().map(AsRef::as_ref).filter(|u| !u.trim().is_empty()).map(Self::normalize_url).collect()
    }

    /// Takes what fetching each of [`Self::feed_urls`] gave, in order.
    pub fn refresh<Z: TimeZone, E>(
        &mut self,
        fetched: impl IntoIterator<Item = Result<String, E>>,
        now: DateTime<Utc>,
        local: &Z,
    ) {
        let mut entries = Vec::new();
        let mut failed = 0;
        for (index, text) in fetched.into_iter().enumerate() {
            match text.map_err(|_| ()).and_then(|t| read_entries(&t, now, now + Self::HORIZON, local).map_err(|_| ())) {
                Ok(read) => entries.extend(read.into_iter().map(|e| CalendarEntry { feed: index, ..e })),
                Err(()) => failed += 1,
            }
        }

        self.failed_feeds = failed;
        entries.sort_by_key(|e| e.start);
        self.upcoming = entries;
    }

    /// Calendar apps hand out webcal:// links, which are plain HTTPS underneath.
    pub fn normalize_url(url: &str) -> String {
        let url = url.trim();
        match url.get(..9) {
            Some(scheme) if scheme.eq_ignore_ascii_case("webcal://") => format!("https://{}", &url[9..]),
            _ => url.to_owned(),
        }
    }
}

/// A DATE or DATE-TIME value.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Time {
    Date(NaiveDate),
    /// No zone, or a TZID no zone matches: wall-clock time wherever the reader is.
    Floating(NaiveDateTime),
    Utc(DateTime<Utc>),
    Zoned(NaiveDateTime, Tz),
}

#[derive(Debug, Default)]
struct Event {
    uid: Option<String>,
    recurrence_id: Option<Time>,
    start: Option<Time>,
    end: Option<Time>,
    duration: Option<TimeDelta>,
    rules: Vec<String>,
    rdates: Vec<Time>,
    exdates: Vec<Time>,
    summary: Option<String>,
    location: Option<String>,
}

struct Property<'a> {
    name: String,
    params: Vec<(String, &'a str)>,
    value: &'a str,
}

impl Property<'_> {
    fn param(&self, name: &str) -> Option<&str> {
        self.params.iter().find(|(n, _)| n == name).map(|(_, v)| *v)
    }
}

/// The events of the one calendar in the text, or `None` when it is not one calendar.
fn parse(text: &str) -> Option<Vec<Event>> {
    let lines = unfold(text);
    let mut events = Vec::new();
    let mut calendars = 0;
    let mut stack: Vec<String> = Vec::new();
    let mut event: Option<Event> = None;
    for line in &lines {
        if line.trim().is_empty() {
            continue;
        }

        let property = content_line(line)?;
        match property.name.as_str() {
            "BEGIN" => {
                let component = property.value.trim().to_ascii_uppercase();
                if stack.is_empty() {
                    if component != "VCALENDAR" {
                        return None;
                    }
                    calendars += 1;
                } else if component == "VEVENT" && stack.len() == 1 {
                    event = Some(Event::default());
                }
                stack.push(component);
            }
            "END" => {
                if stack.pop().as_deref() == Some("VEVENT")
                    && stack.len() == 1
                    && let Some(done) = event.take()
                {
                    events.push(done);
                }
            }
            _ if stack.is_empty() => return None,
            _ => {
                // Only the event's own properties; an alarm inside it has some of the same names.
                if stack.len() == 2
                    && let Some(event) = event.as_mut()
                {
                    read_property(event, &property);
                }
            }
        }
    }

    // Ical.Net loads a collection and wants exactly one calendar in it.
    (calendars == 1).then_some(events)
}

fn read_property(event: &mut Event, property: &Property) {
    match property.name.as_str() {
        "UID" => event.uid = Some(property.value.to_owned()),
        "DTSTART" => event.start = time(property, property.value),
        "DTEND" => event.end = time(property, property.value),
        "DURATION" => event.duration = duration(property.value),
        "RECURRENCE-ID" => event.recurrence_id = time(property, property.value),
        "RRULE" => event.rules.push(property.value.to_owned()),
        "RDATE" if property.param("VALUE").is_none_or(|v| !v.eq_ignore_ascii_case("PERIOD")) => {
            event.rdates.extend(property.value.split(',').filter_map(|v| time(property, v)));
        }
        "EXDATE" => event.exdates.extend(property.value.split(',').filter_map(|v| time(property, v))),
        "SUMMARY" => event.summary = text(property.value),
        "LOCATION" => event.location = text(property.value),
        _ => {}
    }
}

/// The lines, with folded ones (continued on a line starting with a space or tab) put back together.
fn unfold(text: &str) -> Vec<String> {
    let mut lines: Vec<String> = Vec::new();
    for line in text.split('\n') {
        let line = line.strip_suffix('\r').unwrap_or(line);
        match (line.strip_prefix([' ', '\t']), lines.last_mut()) {
            (Some(rest), Some(last)) => last.push_str(rest),
            _ => lines.push(line.to_owned()),
        }
    }
    lines
}

/// `NAME;PARAM=value;PARAM="quoted":value`, or `None` when the line is not a content line.
fn content_line(line: &str) -> Option<Property<'_>> {
    let name_end = line.find([';', ':'])?;
    let name = &line[..name_end];
    if name.is_empty() || !name.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.')) {
        return None;
    }

    let mut params = Vec::new();
    let mut rest = &line[name_end..];
    while let Some(after) = rest.strip_prefix(';') {
        let equals = after.find('=')?;
        let param = after[..equals].trim().to_ascii_uppercase();
        let value_text = &after[equals + 1..];
        let (value, next) = if let Some(quoted) = value_text.strip_prefix('"') {
            let close = quoted.find('"')?;
            let after_quote = &quoted[close + 1..];
            let end = after_quote.find([';', ':']).unwrap_or(after_quote.len());
            (&quoted[..close], &after_quote[end..])
        } else {
            let end = value_text.find([';', ':'])?;
            (&value_text[..end], &value_text[end..])
        };
        params.push((param, value));
        rest = next;
    }

    let value = rest.strip_prefix(':')?;
    Some(Property { name: name.to_ascii_uppercase(), params, value })
}

/// A TEXT value unescaped and trimmed; `None` when that leaves nothing.
fn text(value: &str) -> Option<String> {
    let mut result = String::with_capacity(value.len());
    let mut chars = value.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            result.push(c);
            continue;
        }
        match chars.next() {
            Some('n' | 'N') => result.push('\n'),
            Some(other) => result.push(other),
            None => result.push('\\'),
        }
    }

    let trimmed = result.trim();
    (!trimmed.is_empty()).then(|| trimmed.to_owned())
}

fn time(property: &Property, value: &str) -> Option<Time> {
    let value = value.trim();
    let date = NaiveDate::parse_from_str(value.get(..8)?, "%Y%m%d").ok()?;
    let is_date = property.param("VALUE").is_some_and(|v| v.eq_ignore_ascii_case("DATE")) || value.len() == 8;
    if is_date {
        return Some(Time::Date(date));
    }

    let clock = value.get(8..)?.strip_prefix(['T', 't'])?;
    let (clock, utc) = match clock.strip_suffix(['Z', 'z']) {
        Some(clock) => (clock, true),
        None => (clock, false),
    };
    let wall = date.and_time(NaiveTime::parse_from_str(clock, "%H%M%S").ok()?);
    if utc {
        return Some(Time::Utc(wall.and_utc()));
    }

    Some(match property.param("TZID").and_then(find_zone) {
        Some(tz) => Time::Zoned(wall, tz),
        None => Time::Floating(wall),
    })
}

/// The zone a TZID names, looked for as Ical.Net looks: see the module's notes.
fn find_zone(tzid: &str) -> Option<Tz> {
    let tzid = tzid.trim();
    let tzid = tzid.strip_prefix('/').unwrap_or(tzid);
    if tzid.is_empty() {
        return None;
    }

    if let Ok(tz) = Tz::from_str(tzid) {
        return Some(tz);
    }

    if let Some(tz) = clock_zones::from_windows_id(tzid) {
        return Some(tz);
    }

    if let Some(&(_, iana, _)) = WINDOWS_ZONES.iter().find(|(_, _, display)| *display == tzid) {
        return iana.parse().ok();
    }

    if let Ok(tz) = Tz::from_str(&tzid.replace('-', "/")) {
        return Some(tz);
    }

    // The longest IANA id in the text, so "Europe/Berlin" wins over the "Berlin" or "GMT" in it.
    chrono_tz::TZ_VARIANTS
        .iter()
        .filter(|tz| tz.name().contains('/') && tzid.contains(tz.name()))
        .max_by_key(|tz| tz.name().len())
        .copied()
}

/// An RFC 5545 duration, such as "PT1H30M", "P1D" or "-P2W".
fn duration(value: &str) -> Option<TimeDelta> {
    let value = value.trim();
    let (sign, rest) = match value.as_bytes().first()? {
        b'-' => (-1, &value[1..]),
        b'+' => (1, &value[1..]),
        _ => (1, value),
    };
    let rest = rest.strip_prefix(['P', 'p'])?;

    let mut seconds: i64 = 0;
    let mut number = String::new();
    let mut in_time = false;
    for c in rest.chars() {
        match c.to_ascii_uppercase() {
            'T' => in_time = true,
            d if d.is_ascii_digit() => number.push(d),
            unit => {
                let n: i64 = number.parse().ok()?;
                number.clear();
                seconds += n * match (unit, in_time) {
                    ('W', false) => 7 * 86_400,
                    ('D', false) => 86_400,
                    ('H', true) => 3600,
                    ('M', true) => 60,
                    ('S', true) => 1,
                    _ => return None,
                };
            }
        }
    }

    number.is_empty().then(|| TimeDelta::seconds(sign * seconds))
}

/// Where an event's times are counted: wall-clock time (all-day, floating) or instants in a zone.
#[derive(Clone, Copy)]
enum Frame {
    Wall,
    Zone(rrule::Tz),
}

/// A time in an event's frame: wall-clock time, or an instant.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum Moment {
    Wall(NaiveDateTime),
    Instant(DateTime<Utc>),
}

impl Moment {
    fn add(self, delta: TimeDelta) -> Self {
        match self {
            Self::Wall(wall) => Self::Wall(wall + delta),
            Self::Instant(instant) => Self::Instant(instant + delta),
        }
    }

    fn since(self, earlier: Self) -> TimeDelta {
        match (self, earlier) {
            (Self::Wall(a), Self::Wall(b)) => a - b,
            (Self::Instant(a), Self::Instant(b)) => a - b,
            _ => TimeDelta::zero(),
        }
    }

    fn to_utc<Z: TimeZone>(self, local: &Z) -> DateTime<Utc> {
        match self {
            Self::Wall(wall) => from_wall(wall, local),
            Self::Instant(instant) => instant,
        }
    }

    fn date(self, frame: Frame) -> NaiveDate {
        match (self, frame) {
            (Self::Wall(wall), _) => wall.date(),
            (Self::Instant(instant), Frame::Zone(tz)) => instant.with_timezone(&tz).date_naive(),
            (Self::Instant(instant), Frame::Wall) => instant.date_naive(),
        }
    }
}

/// Wall-clock time where the reader is, as C# reads it: `TimeZoneInfo.Local.GetUtcOffset`, which
/// gives the standard offset for a time that happens twice or not at all.
fn from_wall<Z: TimeZone>(wall: NaiveDateTime, local: &Z) -> DateTime<Utc> {
    match local.from_local_datetime(&wall) {
        chrono::LocalResult::Single(time) => time.with_timezone(&Utc),
        chrono::LocalResult::Ambiguous(_, standard) => standard.with_timezone(&Utc),
        chrono::LocalResult::None => {
            // Clocks went forward; the offset before the gap is the standard one.
            let before = local.offset_from_utc_datetime(&(wall - TimeDelta::days(1))).fix();
            (wall - TimeDelta::seconds(i64::from(before.local_minus_utc()))).and_utc()
        }
    }
}

fn resolve(wall: NaiveDateTime, tz: Tz) -> DateTime<Utc> {
    // A time skipped when the clocks went forward moves on by the gap, as NodaTime's lenient rule does.
    tz.from_local_datetime(&wall)
        .earliest()
        .or_else(|| tz.from_local_datetime(&(wall + TimeDelta::hours(1))).earliest())
        .map_or_else(|| wall.and_utc(), |t| t.with_timezone(&Utc))
}

fn frame_of(start: Time) -> Frame {
    match start {
        Time::Date(_) | Time::Floating(_) => Frame::Wall,
        Time::Utc(_) => Frame::Zone(rrule::Tz::UTC),
        Time::Zoned(_, tz) => Frame::Zone(rrule::Tz::Tz(tz)),
    }
}

/// A time in the frame of an event.
fn moment<Z: TimeZone>(time: Time, frame: Frame, local: &Z) -> Moment {
    match (frame, time) {
        (Frame::Wall, Time::Date(date)) => Moment::Wall(date.and_time(NaiveTime::MIN)),
        (Frame::Wall, Time::Floating(wall)) => Moment::Wall(wall),
        (Frame::Wall, Time::Utc(instant)) => Moment::Wall(instant.with_timezone(local).naive_local()),
        (Frame::Wall, Time::Zoned(wall, tz)) => Moment::Wall(resolve(wall, tz).with_timezone(local).naive_local()),
        (Frame::Zone(zone), Time::Date(date)) => Moment::Instant(in_zone(date.and_time(NaiveTime::MIN), zone)),
        (Frame::Zone(zone), Time::Floating(wall)) => Moment::Instant(in_zone(wall, zone)),
        (Frame::Zone(_), Time::Utc(instant)) => Moment::Instant(instant),
        (Frame::Zone(_), Time::Zoned(wall, tz)) => Moment::Instant(resolve(wall, tz)),
    }
}

fn in_zone(wall: NaiveDateTime, zone: rrule::Tz) -> DateTime<Utc> {
    match zone {
        rrule::Tz::Tz(tz) => resolve(wall, tz),
        rrule::Tz::Local(_) => wall.and_utc(),
    }
}

/// The start and end of each occurrence of an event that may overlap [`from`, `to`), less the
/// ones another event overrides.
fn occurrences<Z: TimeZone>(
    event: &Event,
    overridden: &[&Time],
    from: DateTime<Utc>,
    to: DateTime<Utc>,
    local: &Z,
) -> Vec<(DateTime<Utc>, DateTime<Utc>)> {
    let Some(start) = event.start else { return Vec::new() };
    let frame = frame_of(start);
    let first = moment(start, frame, local);

    let length = match (event.end, event.duration) {
        (Some(end), _) => moment(end, frame, local).since(first),
        (None, Some(duration)) => duration,
        (None, None) if matches!(start, Time::Date(_)) => TimeDelta::days(1),
        (None, None) => TimeDelta::zero(),
    };

    let mut starts = vec![first];
    // An override of one occurrence stands for itself; its own RRULE, if any, is not followed.
    if event.recurrence_id.is_none() {
        starts.extend(expand(event, first, frame, length, from, to, local));
        starts.extend(event.rdates.iter().map(|&t| moment(t, frame, local)));
    }
    starts.sort();
    starts.dedup();

    let excluded = |at: Moment, times: &[&Time]| {
        times.iter().any(|&&t| match t {
            Time::Date(date) if !matches!(start, Time::Date(_)) => at.date(frame) == date,
            other => moment(other, frame, local) == at,
        })
    };
    let exdates: Vec<&Time> = event.exdates.iter().collect();
    starts
        .into_iter()
        .filter(|&at| !excluded(at, &exdates) && (event.recurrence_id.is_some() || !excluded(at, overridden)))
        .map(|at| (at.to_utc(local), at.add(length).to_utc(local)))
        .collect()
}

/// The starts the event's RRULEs give, from a little before `from` (so events in progress are
/// found) to `to`. A rule that cannot be read is left out, and the event keeps its first start.
fn expand<Z: TimeZone>(
    event: &Event,
    first: Moment,
    frame: Frame,
    length: TimeDelta,
    from: DateTime<Utc>,
    to: DateTime<Utc>,
    local: &Z,
) -> Vec<Moment> {
    if event.rules.is_empty() {
        return Vec::new();
    }

    // Wall-clock times are expanded as if they were UTC, then read where the reader is.
    let zone = match frame {
        Frame::Wall => rrule::Tz::UTC,
        Frame::Zone(zone) => zone,
    };
    let as_rrule = |at: Moment| match at {
        Moment::Wall(wall) => wall.and_utc().with_timezone(&rrule::Tz::UTC),
        Moment::Instant(instant) => instant.with_timezone(&zone),
    };
    let dt_start = as_rrule(first);

    let mut set = RRuleSet::new(dt_start);
    for rule in &event.rules {
        if let Some(rule) = rule_until(rule, first, frame, local).and_then(|r| r.validate(dt_start).ok()) {
            set = set.rrule(rule);
        }
    }
    if set.get_rrule().is_empty() {
        return Vec::new();
    }

    // Two days either side covers any difference between UTC and wall-clock time.
    let slack = TimeDelta::days(2);
    let window = |at: DateTime<Utc>| match frame {
        Frame::Wall => Moment::Wall(at.naive_utc()),
        Frame::Zone(_) => Moment::Instant(at),
    };
    let after = as_rrule(window(from - TimeDelta::days(1) - slack).add(-length.max(TimeDelta::zero())));
    let before = as_rrule(window(to + slack));

    set.after(after)
        .before(before)
        .all(u16::MAX)
        .dates
        .into_iter()
        .map(|at| match frame {
            Frame::Wall => Moment::Wall(at.naive_utc()),
            Frame::Zone(_) => Moment::Instant(at.with_timezone(&Utc)),
        })
        .collect()
}

/// The rule with its UNTIL, which the `rrule` crate wants in UTC, written the way the crate takes
/// it. Feeds write UNTIL as a date, a UTC time or a local time; a date counts to the end of the day.
/// `None` when the rule cannot be read or ends before it starts.
fn rule_until<Z: TimeZone>(rule: &str, first: Moment, frame: Frame, local: &Z) -> Option<RRule<Unvalidated>> {
    let mut until = None;
    let mut parts = Vec::new();
    for part in rule.split(';').filter(|p| !p.trim().is_empty()) {
        match part.split_once('=') {
            Some((name, value)) if name.trim().eq_ignore_ascii_case("UNTIL") => until = Some(value.trim()),
            _ => parts.push(part),
        }
    }

    let parsed: RRule<Unvalidated> = parts.join(";").parse().ok()?;
    let Some(until) = until else { return Some(parsed) };

    let until_property = Property { name: "UNTIL".into(), params: Vec::new(), value: until };
    let end = match time(&until_property, until)? {
        Time::Date(date) => Time::Floating(date.and_hms_opt(23, 59, 59)?),
        other => other,
    };
    let end = moment(end, frame, local);
    if end < first {
        return None;
    }

    let end = match end {
        Moment::Wall(wall) => wall.and_utc(),
        Moment::Instant(instant) => instant,
    };
    Some(parsed.until(end.with_timezone(&rrule::Tz::UTC)))
}

#[cfg(test)]
mod tests;
