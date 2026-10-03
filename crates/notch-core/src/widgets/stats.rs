//! One sample of system load and how the Stats tab words it.

use serde::Serialize;

/// One sample of system load.
#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemStats {
    /// 0..1 across all cores.
    pub cpu_usage: f64,
    pub memory_used_bytes: u64,
    pub memory_total_bytes: u64,
    /// 0..1 for the busiest GPU engine, or `None` when it cannot be measured.
    pub gpu_usage: Option<f64>,
    pub download_bytes_per_second: f64,
    pub upload_bytes_per_second: f64,
    /// `None` on machines without a battery.
    pub battery_percent: Option<u8>,
}

impl SystemStats {
    /// Fraction of memory in use, 0..1.
    pub fn memory_usage(&self) -> f64 {
        if self.memory_total_bytes > 0 {
            self.memory_used_bytes as f64 / self.memory_total_bytes as f64
        } else {
            0.0
        }
    }

    /// The sample as the Stats tab shows it.
    pub fn view(&self) -> StatsView {
        StatsView {
            cpu: percent(self.cpu_usage),
            cpu_fraction: self.cpu_usage.clamp(0.0, 1.0),
            memory: percent(self.memory_usage()),
            memory_detail: format!("{} of {}", bytes(self.memory_used_bytes as f64), bytes(self.memory_total_bytes as f64)),
            memory_fraction: self.memory_usage().clamp(0.0, 1.0),
            gpu: self.gpu_usage.map_or_else(|| "n/a".to_owned(), percent),
            gpu_fraction: self.gpu_usage.unwrap_or(0.0).clamp(0.0, 1.0),
            download: rate(self.download_bytes_per_second),
            upload: rate(self.upload_bytes_per_second),
            battery: self.battery_percent.map_or_else(|| "None".to_owned(), |p| format!("{p}%")),
            battery_percent: self.battery_percent,
        }
    }
}

/// What the `stats` event carries: texts for the cards and fractions for the charts.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatsView {
    pub cpu: String,
    pub cpu_fraction: f64,
    pub memory: String,
    pub memory_detail: String,
    pub memory_fraction: f64,
    pub gpu: String,
    pub gpu_fraction: f64,
    pub download: String,
    pub upload: String,
    pub battery: String,
    pub battery_percent: Option<u8>,
}

const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];

/// "512 B", "1.5 MB", "12 GB": one decimal below 10, none above.
pub fn bytes(value: f64) -> String {
    let mut value = value.max(0.0);
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    // Round halves up, as the digits on a gauge are expected to.
    let number = if unit == 0 || value >= 10.0 { format!("{}", value.round()) } else { format!("{:.1}", (value * 10.0).round() / 10.0) };
    format!("{number} {}", UNITS[unit])
}

/// A transfer speed: "1.5 MB/s".
pub fn rate(bytes_per_second: f64) -> String {
    format!("{}/s", bytes(bytes_per_second))
}

/// "37%" for 0.37; values outside 0..1 are clamped.
pub fn percent(fraction: f64) -> String {
    format!("{}%", (fraction.clamp(0.0, 1.0) * 100.0).round_ties_even())
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
            (-5.0, "0 B"),
        ] {
            assert_eq!(bytes(value), expected);
        }
    }

    #[test]
    fn the_view_words_a_sample() {
        let stats = SystemStats {
            cpu_usage: 0.374,
            memory_used_bytes: 8 * 1024 * 1024 * 1024,
            memory_total_bytes: 16 * 1024 * 1024 * 1024,
            gpu_usage: None,
            download_bytes_per_second: 1536.0,
            upload_bytes_per_second: 0.0,
            battery_percent: None,
        };
        let view = stats.view();
        assert_eq!((view.cpu.as_str(), view.memory.as_str(), view.memory_detail.as_str()), ("37%", "50%", "8.0 GB of 16 GB"));
        assert_eq!((view.gpu.as_str(), view.download.as_str(), view.upload.as_str(), view.battery.as_str()), ("n/a", "1.5 KB/s", "0 B/s", "None"));
        assert_eq!(SystemStats { battery_percent: Some(80), gpu_usage: Some(1.4), ..stats }.view().gpu, "100%");
    }
}
