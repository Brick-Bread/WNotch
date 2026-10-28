use chrono::TimeZone;
use chrono_tz::Tz;

use super::*;

const ICS: &str = "BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//test//EN
BEGIN:VEVENT
UID:standup
DTSTAMP:20260101T000000Z
DTSTART:20261001T090000Z
DTEND:20261001T091500Z
RRULE:FREQ=DAILY;COUNT=5
SUMMARY:Standup
LOCATION:Room 2
END:VEVENT
BEGIN:VEVENT
UID:holiday
DTSTAMP:20260101T000000Z
DTSTART;VALUE=DATE:20261002
DTEND;VALUE=DATE:20261003
SUMMARY:Holiday
END:VEVENT
BEGIN:VEVENT
UID:old
DTSTAMP:20260101T000000Z
DTSTART:20260901T090000Z
DTEND:20260901T100000Z
SUMMARY:Long gone
END:VEVENT
END:VCALENDAR
";

fn utc(y: i32, m: u32, d: u32, h: u32, min: u32) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(y, m, d, h, min, 0).unwrap()
}

fn days(n: i64) -> TimeDelta {
    TimeDelta::days(n)
}

fn calendar(events: &str) -> String {
    format!("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n{events}END:VCALENDAR\r\n")
}

#[test]
fn webcal_links_become_https() {
    assert_eq!(CalendarService::normalize_url(" webcal://example.com/cal.ics "), "https://example.com/cal.ics");
    assert_eq!(CalendarService::normalize_url("WEBCAL://x"), "https://x");
    assert_eq!(CalendarService::normalize_url("https://x"), "https://x");
}

#[test]
fn feed_expands_recurrences_within_the_window() {
    let from = utc(2026, 10, 1, 0, 0);

    let entries = read_entries(ICS, from, from + days(3), &Utc).unwrap();

    assert_eq!(entries.iter().filter(|e| e.title == "Standup").count(), 3);
    assert!(!entries.iter().any(|e| e.title == "Long gone"));

    let standup = entries.iter().find(|e| e.title == "Standup").unwrap();
    assert_eq!(standup.start, utc(2026, 10, 1, 9, 0));
    assert_eq!(standup.end - standup.start, TimeDelta::minutes(15));
    assert_eq!(standup.location.as_deref(), Some("Room 2"));
    assert!(!standup.is_all_day);
}

#[test]
fn feed_reads_all_day_events_as_local_dates() {
    let from = utc(2026, 10, 1, 0, 0);
    for zone in [Tz::UTC, Tz::America__New_York, Tz::Asia__Tokyo] {
        let entries = read_entries(ICS, from, from + days(3), &zone).unwrap();
        let holidays: Vec<_> = entries.iter().filter(|e| e.title == "Holiday").collect();
        assert_eq!(holidays.len(), 1, "{zone}");

        assert!(holidays[0].is_all_day);
        let start = holidays[0].start.with_timezone(&zone).naive_local();
        assert_eq!(start, NaiveDate::from_ymd_opt(2026, 10, 2).unwrap().and_time(NaiveTime::MIN), "{zone}");
    }
}

#[test]
fn feed_rejects_text_that_is_not_a_calendar() {
    let now = Utc::now();
    assert_eq!(read_entries("<html>Sign in</html>", now, now + days(1), &Utc), Err(INVALID.into()));
    assert!(read_entries("", now, now + days(1), &Utc).is_err());
    assert!(read_entries("FOO:bar", now, now + days(1), &Utc).is_err());
    assert!(read_entries(&(calendar("") + &calendar("")), now, now + days(1), &Utc).is_err());
    assert_eq!(read_entries(&calendar(""), now, now + days(1), &Utc), Ok(vec![]));
}

#[test]
fn service_merges_feeds_and_counts_failures() {
    let now = utc(2026, 10, 1, 0, 0);
    let urls = CalendarService::feed_urls(&["webcal://feeds.test/good.ics", "  ", "https://feeds.test/missing.ics"]);
    assert_eq!(urls, ["https://feeds.test/good.ics", "https://feeds.test/missing.ics"]);

    let mut service = CalendarService::new();
    let fetched = urls.iter().map(|u| if u.contains("good") { Ok(ICS.to_owned()) } else { Err("404") });
    service.refresh(fetched, now, &Utc);

    assert_eq!(service.failed_feeds(), 1);
    assert!(service.upcoming().iter().any(|e| e.title == "Holiday"));
    assert!(service.upcoming().iter().all(|e| e.feed == 0));
    assert!(service.upcoming().windows(2).all(|w| w[0].start <= w[1].start));

    // A feed that is not a calendar fails too, and the others keep their place in the list.
    service.refresh([Ok::<_, ()>("nope".to_owned()), Ok(ICS.to_owned())], now, &Utc);
    assert_eq!(service.failed_feeds(), 1);
    assert!(service.upcoming().iter().all(|e| e.feed == 1));
}

#[test]
fn windows_and_iana_time_zones_are_read() {
    let ics = calendar(
        "BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=W. Europe Standard Time:20261005T100000\r\nDTEND;TZID=W. Europe Standard Time:20261005T110000\r\nSUMMARY:Outlook\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nUID:b\r\nDTSTART;TZID=America/New_York:20261005T100000\r\nDURATION:PT30M\r\nSUMMARY:Google\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nUID:c\r\nDTSTART;TZID=\"/mozilla.org/20050126_1/Asia/Tokyo\":20261005T100000\r\nSUMMARY:Mozilla\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nUID:d\r\nDTSTART;TZID=\"(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna\":20261006T100000\r\nSUMMARY:Display name\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nUID:e\r\nDTSTART;TZID=Customized Time Zone:20261006T100000\r\nSUMMARY:Unknown zone\r\nEND:VEVENT\r\n",
    );
    let from = utc(2026, 10, 4, 0, 0);
    let entries = read_entries(&ics, from, from + days(7), &Tz::America__Los_Angeles).unwrap();
    let start = |title: &str| entries.iter().find(|e| e.title == title).map(|e| (e.start, e.end - e.start)).unwrap();

    // Berlin is on summer time (UTC+2) on 5 October.
    assert_eq!(start("Outlook"), (utc(2026, 10, 5, 8, 0), TimeDelta::hours(1)));
    assert_eq!(start("Google"), (utc(2026, 10, 5, 14, 0), TimeDelta::minutes(30)));
    assert_eq!(start("Mozilla"), (utc(2026, 10, 5, 1, 0), TimeDelta::zero()));
    assert_eq!(start("Display name").0, utc(2026, 10, 6, 8, 0));
    // An unknown zone is wall-clock time where the reader is: Los Angeles, UTC-7.
    assert_eq!(start("Unknown zone").0, utc(2026, 10, 6, 17, 0));
}

#[test]
fn recurrences_follow_the_zone_across_a_clock_change() {
    // Berlin leaves summer time on 25 October 2026; a 09:00 meeting stays at 09:00 there.
    let ics = calendar(
        "BEGIN:VEVENT\r\nUID:w\r\nDTSTART;TZID=W. Europe Standard Time:20261023T090000\r\nDTEND;TZID=W. Europe Standard Time:20261023T093000\r\n\
         RRULE:FREQ=DAILY;UNTIL=20261027T070000Z\r\nEXDATE;TZID=W. Europe Standard Time:20261024T090000\r\nSUMMARY:Daily\r\nEND:VEVENT\r\n",
    );
    let from = utc(2026, 10, 22, 0, 0);
    let entries = read_entries(&ics, from, from + days(7), &Utc).unwrap();

    assert_eq!(
        entries.iter().map(|e| e.start).collect::<Vec<_>>(),
        [utc(2026, 10, 23, 7, 0), utc(2026, 10, 25, 8, 0), utc(2026, 10, 26, 8, 0)]
    );
}

#[test]
fn moved_occurrences_replace_the_ones_they_override() {
    let ics = calendar(
        "BEGIN:VEVENT\r\nUID:m\r\nDTSTART:20261001T090000Z\r\nDTEND:20261001T100000Z\r\nRRULE:FREQ=WEEKLY;COUNT=3\r\nSUMMARY:Weekly\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nUID:m\r\nRECURRENCE-ID:20261008T090000Z\r\nDTSTART:20261009T130000Z\r\nDTEND:20261009T140000Z\r\nSUMMARY:Weekly (moved)\r\nEND:VEVENT\r\n",
    );
    let from = utc(2026, 9, 30, 0, 0);
    let entries = read_entries(&ics, from, from + days(30), &Utc).unwrap();

    assert_eq!(
        entries.iter().map(|e| (e.title.as_str(), e.start)).collect::<Vec<_>>(),
        [
            ("Weekly", utc(2026, 10, 1, 9, 0)),
            ("Weekly (moved)", utc(2026, 10, 9, 13, 0)),
            ("Weekly", utc(2026, 10, 15, 9, 0)),
        ]
    );
}

#[test]
fn events_in_progress_and_floating_times_are_found() {
    let ics = calendar(
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20260928\r\nDTEND;VALUE=DATE:20261010\r\nSUMMARY:Conference\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nDTSTART:20261002T080000\r\nRRULE:FREQ=DAILY;UNTIL=20261003\r\nSUMMARY:  Floating \\, early  \r\n\
         BEGIN:VALARM\r\nSUMMARY:Alarm\r\nEND:VALARM\r\nEND:VEVENT\r\n\
         BEGIN:VEVENT\r\nDTSTART:20261002T080000Z\r\nSUMMARY: \r\nLOCATION:  \r\nEND:VEVENT\r\n",
    );
    let from = utc(2026, 10, 1, 0, 0);
    let entries = read_entries(&ics, from, from + days(3), &Tz::Asia__Tokyo).unwrap();
    let titles: Vec<_> = entries.iter().map(|e| e.title.as_str()).collect();

    assert_eq!(titles, ["Conference", "Floating , early", "(No title)", "Floating , early"]);
    assert_eq!(entries[1].start, utc(2026, 10, 1, 23, 0));
    assert_eq!(entries[2].location, None);
}

#[test]
fn folded_lines_and_durations_are_read() {
    assert_eq!(unfold("A:b\r\n c\r\n\td\r\nE:f"), ["A:bcd", "E:f"]);
    assert_eq!(duration("P1W"), Some(TimeDelta::days(7)));
    assert_eq!(duration("-PT1H30M"), Some(TimeDelta::minutes(-90)));
    assert_eq!(duration("P1DT2S"), Some(TimeDelta::seconds(86_402)));
    assert_eq!(duration("PT5"), None);
    assert_eq!(duration("P1H"), None);
}
