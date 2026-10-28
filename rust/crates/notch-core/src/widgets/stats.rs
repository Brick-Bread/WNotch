//! A sample of system load and how the stats widget and tab write it. Ported from
//! `src/Notch.Core/Widgets/SystemStats.cs`.

use crate::activities::glow::round;

/// One sample of system load.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SystemStats {
    /// 0..1 across all cores.
    pub cpu_usage: f64,
    pub memory_used_bytes: i64,
    pub memory_total_bytes: i64,
    /// 0..1 for the busiest GPU engine, or `None` when it cannot be measured.
    pub gpu_usage: Option<f64>,
    pub download_bytes_per_second: f64,
    pub upload_bytes_per_second: f64,
    /// `None` on machines without a battery.
    pub battery_percent: Option<i32>,
    /// Degrees Celsius, or `None` when the machine does not report it.
    pub cpu_temperature: Option<f64>,
    /// Degrees Celsius of the hottest GPU, or `None` when no driver reports it.
    pub gpu_temperature: Option<f64>,
}

impl SystemStats {
    pub fn memory_usage(&self) -> f64 {
        if self.memory_total_bytes > 0 { self.memory_used_bytes as f64 / self.memory_total_bytes as f64 } else { 0.0 }
    }
}

const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];

/// "512 B", "1.5 MB", "12 GB": one decimal below 10, none above.
///
/// C# writes the number in the current culture, so a German machine shows "1,5 MB"; this always
/// writes a point.
pub fn bytes(bytes: f64) -> String {
    let mut bytes = bytes.max(0.0);
    let mut unit = 0;
    while bytes >= 1024.0 && unit < UNITS.len() - 1 {
        bytes /= 1024.0;
        unit += 1;
    }

    let number = if unit == 0 || bytes >= 10.0 { fixed(bytes, 0) } else { fixed(bytes, 1) };
    format!("{number} {}", UNITS[unit])
}

pub fn rate(bytes_per_second: f64) -> String {
    bytes(bytes_per_second) + "/s"
}

pub fn percent(fraction: f64) -> String {
    format!("{}%", round(fraction.clamp(0.0, 1.0) * 100.0))
}

/// "59 °C", or "138 °F" where the region does not use the metric system.
pub fn temperature(celsius: f64, metric: bool) -> String {
    if metric { format!("{} °C", round(celsius)) } else { format!("{} °F", round(celsius * 9.0 / 5.0 + 32.0)) }
}

/// A number with `decimals` places as .NET's "0" and "0.0" formats write it: first cut to 15
/// significant digits, then halves rounded away from zero (Rust rounds them to even).
pub(crate) fn fixed(value: f64, decimals: u32) -> String {
    let sign = if value.is_sign_negative() { "-" } else { "" };
    let scientific = format!("{:.14e}", value.abs());
    let (mantissa, exponent) = scientific.split_once('e').unwrap_or((&scientific, "0"));
    let digits: u64 = mantissa.replace('.', "").parse().unwrap_or(0);
    let shift = exponent.parse::<i32>().unwrap_or(0) - 14 + decimals as i32;
    let scaled = if shift >= 0 {
        match 10u64.checked_pow(shift as u32).and_then(|p| digits.checked_mul(p)) {
            Some(scaled) => scaled,
            None => return format!("{value:.*}", decimals as usize),
        }
    } else if shift < -19 {
        0
    } else {
        let divisor = 10u64.pow((-shift) as u32);
        digits / divisor + u64::from(digits % divisor >= divisor.div_ceil(2))
    };

    let unit = 10u64.pow(decimals);
    if decimals == 0 {
        format!("{sign}{scaled}")
    } else {
        format!("{sign}{}.{:0width$}", scaled / unit, scaled % unit, width = decimals as usize)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn byte_sizes_are_compact() {
        for (value, expected) in [
            (512.0, "512 B"),
            (1536.0, "1.5 KB"),
            (12.4 * 1024.0 * 1024.0, "12 MB"),
            (3.25 * 1024.0 * 1024.0 * 1024.0, "3.3 GB"),
        ] {
            assert_eq!(bytes(value), expected);
        }
    }

    #[test]
    fn temperatures_follow_the_region() {
        assert_eq!(temperature(58.85, true), "59 °C");
        assert_eq!(temperature(58.85, false), "138 °F");
    }

    #[test]
    fn rates_and_percents() {
        assert_eq!(rate(2048.0), "2.0 KB/s");
        assert_eq!(percent(0.425), "42%");
        assert_eq!(percent(1.5), "100%");
        assert_eq!(bytes(-5.0), "0 B");
        // .NET reads 9.95 to 15 digits first, so it rounds up although the double is a little less.
        assert_eq!(bytes(9.95 * 1024.0), "10.0 KB");
        assert_eq!(bytes(2.5), "3 B");
    }
}
