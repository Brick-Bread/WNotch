//! Samples CPU, memory, GPU, network and battery. Rates are measured between consecutive calls
//! to [`Sampler::sample`], so the first sample reports zero CPU and network activity. Not
//! thread-safe: call from one thread at a time.

use std::time::Instant;

use notch_core::widgets::stats::SystemStats;
use sysinfo::{MemoryRefreshKind, Networks, System};

#[cfg(target_os = "linux")]
mod linux;
#[cfg(windows)]
mod win;

#[cfg(target_os = "linux")]
use linux as platform;
#[cfg(windows)]
use win as platform;

/// No GPU or battery sources on platforms without a port; the CPU, memory and network still work.
#[cfg(not(any(windows, target_os = "linux")))]
mod platform {
    pub struct Gpu;

    impl Gpu {
        pub fn new() -> Self {
            Gpu
        }

        pub fn usage(&mut self) -> Option<f64> {
            None
        }

        pub fn is_present(&self) -> bool {
            false
        }
    }

    pub fn battery_percent() -> Option<u8> {
        None
    }
}

/// Reads the machine's load.
pub struct Sampler {
    system: System,
    networks: Networks,
    gpu: platform::Gpu,
    network_baseline: Option<NetworkBaseline>,
}

struct NetworkBaseline {
    at: Instant,
    received: u64,
    sent: u64,
}

impl Sampler {
    pub fn new() -> Self {
        Self {
            system: System::new(),
            networks: Networks::new_with_refreshed_list(),
            gpu: platform::Gpu::new(),
            network_baseline: None,
        }
    }

    /// Whether the machine has a GPU whose load can be shown (the card is hidden when it has none).
    pub fn has_gpu(&self) -> bool {
        self.gpu.is_present()
    }

    pub fn sample(&mut self) -> SystemStats {
        self.system.refresh_cpu_usage();
        self.system.refresh_memory_specifics(MemoryRefreshKind::nothing().with_ram());
        let total = self.system.total_memory();
        let (download, upload) = self.network_rates();

        SystemStats {
            cpu_usage: (f64::from(self.system.global_cpu_usage()) / 100.0).clamp(0.0, 1.0),
            memory_used_bytes: total.saturating_sub(self.system.available_memory()),
            memory_total_bytes: total,
            gpu_usage: self.gpu.usage(),
            download_bytes_per_second: download,
            upload_bytes_per_second: upload,
            battery_percent: platform::battery_percent(),
        }
    }

    /// Bytes per second received and sent across all real interfaces since the last call.
    fn network_rates(&mut self) -> (f64, f64) {
        self.networks.refresh(true);
        let (mut received, mut sent) = (0u64, 0u64);
        for (name, data) in self.networks.iter() {
            if is_loopback(name) {
                continue;
            }
            received = received.saturating_add(data.total_received());
            sent = sent.saturating_add(data.total_transmitted());
        }

        let now = Instant::now();
        let rates = self.network_baseline.as_ref().map_or((0.0, 0.0), |last| {
            let seconds = now.duration_since(last.at).as_secs_f64();
            if seconds > 0.0 {
                // An adapter disappearing makes the totals drop; report zero rather than a negative rate.
                (received.saturating_sub(last.received) as f64 / seconds, sent.saturating_sub(last.sent) as f64 / seconds)
            } else {
                (0.0, 0.0)
            }
        });
        self.network_baseline = Some(NetworkBaseline { at: now, received, sent });
        rates
    }
}

fn is_loopback(name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    name == "lo" || name.contains("loopback")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loopback_interfaces_are_ignored() {
        assert!(is_loopback("lo"));
        assert!(is_loopback("Loopback Pseudo-Interface 1"));
        assert!(!is_loopback("eth0"));
        assert!(!is_loopback("Ethernet"));
    }

    #[test]
    fn the_first_sample_reports_no_network_activity() {
        let mut sampler = Sampler::new();
        let first = sampler.sample();
        assert_eq!((first.download_bytes_per_second, first.upload_bytes_per_second), (0.0, 0.0));
        assert!(first.memory_total_bytes > 0);
        assert!(first.memory_used_bytes <= first.memory_total_bytes);
    }
}
