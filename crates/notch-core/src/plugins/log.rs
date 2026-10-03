//! The log file all plugins and the plugin host write to. Failing to write is never an error.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

/// Rotated to `.old` beyond this size, so a chatty plugin cannot fill the disk.
const MAX_BYTES: u64 = 1024 * 1024;

pub struct PluginLog {
    path: PathBuf,
    gate: Mutex<()>,
}

impl PluginLog {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into(), gate: Mutex::new(()) }
    }

    pub fn path(&self) -> &std::path::Path {
        &self.path
    }

    /// Appends a line. `source` is a plugin id, or `notch` for the host itself; `level` is
    /// `info`, `warn` or `error`.
    pub fn write(&self, source: &str, level: &str, message: &str) {
        let _guard = self.gate.lock().unwrap_or_else(|e| e.into_inner());
        let entry = format!("{} [{level}] {source}: {}\n", timestamp(SystemTime::now()), message.trim_end());
        if let Some(folder) = self.path.parent() {
            if fs::create_dir_all(folder).is_err() {
                return;
            }
        }
        if fs::metadata(&self.path).is_ok_and(|m| m.len() > MAX_BYTES) {
            let _ = fs::rename(&self.path, self.path.with_extension("log.old"));
        }
        if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(&self.path) {
            let _ = file.write_all(entry.as_bytes());
        }
    }
}

/// `2026-10-02 14:03:09Z` (UTC).
fn timestamp(time: SystemTime) -> String {
    let seconds = time.duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or_default();
    let (days, rest) = (i64::try_from(seconds / 86_400).unwrap_or(0), seconds % 86_400);
    let (year, month, day) = civil_from_days(days);
    format!("{year:04}-{month:02}-{day:02} {:02}:{:02}:{:02}Z", rest / 3600, rest % 3600 / 60, rest % 60)
}

/// Days since 1970-01-01 to a calendar date (Howard Hinnant's algorithm).
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era = (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let mp = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    (year_of_era + era * 400 + i64::from(month <= 2), month, day)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::store::testing::TempDir;
    use std::time::Duration;

    #[test]
    fn dates_are_calendar_dates() {
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(civil_from_days(19_723), (2024, 1, 1));
        assert_eq!(civil_from_days(20_513), (2026, 3, 1));
        assert_eq!(timestamp(UNIX_EPOCH + Duration::from_secs(86_400 + 3_723)), "1970-01-02 01:02:03Z");
    }

    #[test]
    fn lines_carry_the_source_and_the_log_rotates() {
        let dir = TempDir::new("log");
        let log = PluginLog::new(dir.0.join("sub").join("plugins.log"));
        log.write("acme.x", "warn", "careful\n");
        let text = fs::read_to_string(log.path()).unwrap();
        assert!(text.contains("[warn] acme.x: careful"));
        assert!(text.ends_with("careful\n"));

        fs::write(log.path(), vec![b'x'; usize::try_from(MAX_BYTES).unwrap() + 1]).unwrap();
        log.write("notch", "info", "fresh");
        assert!(log.path().with_extension("log.old").is_file());
        assert!(fs::read_to_string(log.path()).unwrap().contains("fresh"));
    }
}
