//! Samples CPU, memory, GPU, network and battery. Ported from
//! `src/Notch.Platform/Stats/SystemStatsSampler.cs`.

use std::collections::HashMap;
use std::time::Instant;

use notch_core::widgets::stats::SystemStats;
use windows::System::Power::{BatteryStatus, PowerManager};
use windows::Wdk::Graphics::Direct3D::{
    D3DKMT_ADAPTER_PERFDATA, D3DKMT_ADAPTERINFO, D3DKMT_CLOSEADAPTER, D3DKMT_ENUMADAPTERS2, D3DKMT_QUERYADAPTERINFO,
    D3DKMTCloseAdapter, D3DKMTEnumAdapters2, D3DKMTQueryAdapterInfo, KMTQAITYPE_ADAPTERPERFDATA,
};
use windows::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, FILETIME, NO_ERROR};
use windows::Win32::NetworkManagement::IpHelper::{
    GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER, GAA_FLAG_SKIP_MULTICAST, GAA_FLAG_SKIP_UNICAST,
    GetAdaptersAddresses, GetIfEntry2, IF_TYPE_SOFTWARE_LOOPBACK, IF_TYPE_TUNNEL, IP_ADAPTER_ADDRESSES_LH, MIB_IF_ROW2,
};
use windows::Win32::NetworkManagement::Ndis::IfOperStatusUp;
use windows::Win32::Networking::WinSock::AF_UNSPEC;
use windows::Win32::System::Performance::{
    PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_DOUBLE, PDH_HCOUNTER, PDH_HQUERY, PDH_MORE_DATA, PdhAddEnglishCounterW,
    PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterArrayW, PdhOpenQueryW,
};
use windows::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};
use windows::Win32::System::Threading::GetSystemTimes;
use windows::core::{PCWSTR, w};

/// One performance counter path with every instance, read through PDH.
struct Counter {
    query: PDH_HQUERY,
    counter: PDH_HCOUNTER,
}

impl Counter {
    fn open(path: PCWSTR) -> Option<Self> {
        let mut query = PDH_HQUERY::default();
        let mut counter = PDH_HCOUNTER::default();
        // SAFETY: both out-pointers are valid; the query is closed on drop.
        unsafe {
            if PdhOpenQueryW(PCWSTR::null(), 0, &mut query) != 0 {
                return None;
            }
            if PdhAddEnglishCounterW(query, path, 0, &mut counter) != 0 {
                let _ = PdhCloseQuery(query);
                return None;
            }
        }
        Some(Self { query, counter })
    }

    /// Takes a new reading; rates come out of the difference between two.
    fn collect(&self) -> bool {
        // SAFETY: the query is open.
        unsafe { PdhCollectQueryData(self.query) == 0 }
    }

    /// Every instance's value from the last readings, or `None` when there is none yet.
    fn values(&self) -> Option<Vec<(String, f64)>> {
        let (mut size, mut count) = (0u32, 0u32);
        // SAFETY: a size query, then a read into a buffer of that size; the names point into it.
        unsafe {
            let status = PdhGetFormattedCounterArrayW(self.counter, PDH_FMT_DOUBLE, &mut size, &mut count, None);
            if status != PDH_MORE_DATA {
                return (status == 0).then(Vec::new);
            }
            let mut buffer = vec![0u8; size as usize];
            let items = buffer.as_mut_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>();
            if PdhGetFormattedCounterArrayW(self.counter, PDH_FMT_DOUBLE, &mut size, &mut count, Some(items)) != 0 {
                return None;
            }
            let items = std::slice::from_raw_parts(items, count as usize);
            Some(
                items
                    .iter()
                    .filter(|item| item.FmtValue.CStatus == 0)
                    .map(|item| (item.szName.to_string().unwrap_or_default(), item.FmtValue.Anonymous.doubleValue))
                    .collect(),
            )
        }
    }
}

impl Drop for Counter {
    fn drop(&mut self) {
        // SAFETY: the query was opened in `open`.
        unsafe {
            let _ = PdhCloseQuery(self.query);
        }
    }
}

/// Samples system load. Rates are measured between consecutive calls to [`Self::sample`], so the
/// first sample reports zero CPU, GPU and network activity. Use from one thread at a time.
pub struct SystemStatsSampler {
    started: Instant,
    last_idle: u64,
    last_total: u64,
    last_received: u64,
    last_sent: u64,
    last_network: Option<f64>,
    gpu: Option<Counter>,
    gpu_unavailable: bool,
    gpu_has_baseline: bool,
    thermal: Option<Counter>,
    cpu_temperature_unavailable: bool,
    gpu_temperature_unavailable: bool,
}

impl Default for SystemStatsSampler {
    fn default() -> Self {
        Self::new()
    }
}

impl SystemStatsSampler {
    pub fn new() -> Self {
        Self {
            started: Instant::now(),
            last_idle: 0,
            last_total: 0,
            last_received: 0,
            last_sent: 0,
            last_network: None,
            gpu: None,
            gpu_unavailable: false,
            gpu_has_baseline: false,
            thermal: None,
            cpu_temperature_unavailable: false,
            gpu_temperature_unavailable: false,
        }
    }

    pub fn sample(&mut self) -> SystemStats {
        let (memory_used_bytes, memory_total_bytes) = memory();
        let (download, upload) = self.network();
        let battery_percent = match PowerManager::BatteryStatus() {
            Ok(status) if status != BatteryStatus::NotPresent => PowerManager::RemainingChargePercent().ok(),
            _ => None,
        };

        SystemStats {
            cpu_usage: self.cpu(),
            memory_used_bytes,
            memory_total_bytes,
            gpu_usage: self.gpu(),
            download_bytes_per_second: download,
            upload_bytes_per_second: upload,
            battery_percent,
            cpu_temperature: self.cpu_temperature(),
            gpu_temperature: self.gpu_temperature(),
        }
    }

    fn cpu(&mut self) -> f64 {
        let (mut idle, mut kernel, mut user) = (FILETIME::default(), FILETIME::default(), FILETIME::default());
        // SAFETY: three valid out-pointers.
        if unsafe { GetSystemTimes(Some(&mut idle), Some(&mut kernel), Some(&mut user)) }.is_err() {
            return 0.0;
        }

        let ticks = |t: FILETIME| u64::from(t.dwHighDateTime) << 32 | u64::from(t.dwLowDateTime);
        // Kernel time includes idle time.
        let (idle, total) = (ticks(idle), ticks(kernel) + ticks(user));
        let (idle_delta, total_delta) = (idle.wrapping_sub(self.last_idle), total.wrapping_sub(self.last_total));
        let first = self.last_total == 0;
        self.last_idle = idle;
        self.last_total = total;

        if first || total_delta == 0 { 0.0 } else { (1.0 - idle_delta as f64 / total_delta as f64).clamp(0.0, 1.0) }
    }

    fn network(&mut self) -> (f64, f64) {
        let Some((received, sent)) = network_totals() else { return (0.0, 0.0) };
        let now = self.started.elapsed().as_secs_f64();
        let mut rates = (0.0, 0.0);

        // An adapter disappearing makes the totals drop; report zero rather than a negative rate.
        if let Some(last) = self.last_network {
            let seconds = now - last;
            if seconds > 0.0 {
                rates = (
                    received.saturating_sub(self.last_received) as f64 / seconds,
                    sent.saturating_sub(self.last_sent) as f64 / seconds,
                );
            }
        }

        self.last_received = received;
        self.last_sent = sent;
        self.last_network = Some(now);
        rates
    }

    /// The busiest engine type (3D, video decode, ...) summed over all processes, as Task Manager shows it.
    fn gpu(&mut self) -> Option<f64> {
        if self.gpu_unavailable {
            return None;
        }
        if self.gpu.is_none() {
            self.gpu = Counter::open(w!(r"\GPU Engine(*)\Utilization Percentage"));
        }
        let Some(counter) = self.gpu.as_ref().filter(|c| c.collect()) else {
            // No GPU counters on this machine (very old drivers, or counters disabled).
            self.gpu_unavailable = true;
            return None;
        };

        if !self.gpu_has_baseline {
            self.gpu_has_baseline = true;
            return Some(0.0);
        }

        let mut by_engine: HashMap<String, f64> = HashMap::new();
        for (name, usage) in counter.values().unwrap_or_default() {
            // Instance names look like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D".
            let engine = name.rfind("engtype_").map_or("", |at| &name[at..]).to_owned();
            *by_engine.entry(engine).or_default() += usage;
        }
        let busiest = by_engine.into_values().fold(f64::NAN, f64::max);
        Some(if busiest.is_nan() { 0.0 } else { (busiest / 100.0).clamp(0.0, 1.0) })
    }

    /// The hottest ACPI thermal zone, which on most PCs is the processor's. Windows publishes it as
    /// a performance counter in kelvin, which needs no administrator rights.
    fn cpu_temperature(&mut self) -> Option<f64> {
        if self.cpu_temperature_unavailable {
            return None;
        }
        if self.thermal.is_none() {
            self.thermal = Counter::open(w!(r"\Thermal Zone Information(*)\Temperature"));
        }
        let zones = self.thermal.as_ref().filter(|c| c.collect()).and_then(Counter::values).unwrap_or_default();
        // No thermal zones on this machine, as on many desktops and virtual machines.
        self.cpu_temperature_unavailable = zones.is_empty();
        zones.into_iter().map(|(_, kelvin)| kelvin - 273.15).filter(|&c| plausible(c)).reduce(f64::max)
    }

    /// The hottest GPU, from the performance data the driver gives the graphics kernel: what Task
    /// Manager shows. Drivers older than WDDM 2.9, and most integrated GPUs, report no temperature.
    fn gpu_temperature(&mut self) -> Option<f64> {
        if self.gpu_temperature_unavailable {
            return None;
        }

        // SAFETY: the adapter array is sized by the first call; each opened adapter is closed.
        unsafe {
            let mut enumerate = D3DKMT_ENUMADAPTERS2::default();
            if D3DKMTEnumAdapters2(&mut enumerate).is_err() || enumerate.NumAdapters == 0 {
                self.gpu_temperature_unavailable = true;
                return None;
            }

            let mut adapters = vec![D3DKMT_ADAPTERINFO::default(); enumerate.NumAdapters as usize];
            enumerate.pAdapters = adapters.as_mut_ptr();
            if D3DKMTEnumAdapters2(&mut enumerate).is_err() {
                return None;
            }

            let mut hottest: Option<f64> = None;
            for adapter in &adapters[..enumerate.NumAdapters as usize] {
                let mut data = D3DKMT_ADAPTER_PERFDATA::default();
                let mut query = D3DKMT_QUERYADAPTERINFO {
                    hAdapter: adapter.hAdapter,
                    Type: KMTQAITYPE_ADAPTERPERFDATA,
                    pPrivateDriverData: (&raw mut data).cast(),
                    PrivateDriverDataSize: size_of::<D3DKMT_ADAPTER_PERFDATA>() as u32,
                };

                // Tenths of a degree; 0 from a driver that does not measure it.
                if D3DKMTQueryAdapterInfo(&mut query).is_ok() && data.Temperature > 0 {
                    let celsius = f64::from(data.Temperature) / 10.0;
                    if plausible(celsius) {
                        hottest = Some(hottest.unwrap_or(0.0).max(celsius));
                    }
                }

                let _ = D3DKMTCloseAdapter(&D3DKMT_CLOSEADAPTER { hAdapter: adapter.hAdapter });
            }
            hottest
        }
    }
}

fn plausible(celsius: f64) -> bool {
    celsius > 0.0 && celsius < 125.0
}

fn memory() -> (i64, i64) {
    let mut status = MEMORYSTATUSEX { dwLength: size_of::<MEMORYSTATUSEX>() as u32, ..Default::default() };
    // SAFETY: `status` is sized as the call expects.
    match unsafe { GlobalMemoryStatusEx(&mut status) } {
        Ok(()) => ((status.ullTotalPhys - status.ullAvailPhys) as i64, status.ullTotalPhys as i64),
        Err(_) => (0, 0),
    }
}

/// Bytes received and sent by the adapters that are up, leaving out loopback and tunnels, as
/// .NET's `NetworkInterface` list gives them.
fn network_totals() -> Option<(u64, u64)> {
    let flags = GAA_FLAG_SKIP_UNICAST | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    let mut size = 15_000u32;
    let mut buffer: Vec<u64>;
    // SAFETY: the buffer is u64-aligned and `size` bytes long; the list is walked while it lives.
    unsafe {
        loop {
            buffer = vec![0u64; (size as usize).div_ceil(8)];
            let first = buffer.as_mut_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
            match GetAdaptersAddresses(AF_UNSPEC.0 as u32, flags, None, Some(first), &mut size) {
                code if code == NO_ERROR.0 => break,
                code if code == ERROR_BUFFER_OVERFLOW.0 => continue,
                _ => return None,
            }
        }

        let (mut received, mut sent) = (0u64, 0u64);
        let mut adapter = buffer.as_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
        while let Some(current) = adapter.as_ref() {
            if current.OperStatus == IfOperStatusUp
                && current.IfType != IF_TYPE_SOFTWARE_LOOPBACK
                && current.IfType != IF_TYPE_TUNNEL
            {
                let mut row = MIB_IF_ROW2 { InterfaceLuid: current.Luid, ..Default::default() };
                if GetIfEntry2(&mut row).is_ok() {
                    received += row.InOctets;
                    sent += row.OutOctets;
                }
            }
            adapter = current.Next;
        }
        Some((received, sent))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn samples_sensible_values() {
        let mut sampler = SystemStatsSampler::new();
        let first = sampler.sample();
        assert_eq!(first.cpu_usage, 0.0);
        assert!(first.memory_total_bytes > 0);
        assert!(first.memory_used_bytes > 0 && first.memory_used_bytes <= first.memory_total_bytes);

        std::thread::sleep(std::time::Duration::from_millis(200));
        let second = sampler.sample();
        assert!((0.0..=1.0).contains(&second.cpu_usage));
        assert!(second.download_bytes_per_second >= 0.0 && second.upload_bytes_per_second >= 0.0);
        if let Some(gpu) = second.gpu_usage {
            assert!((0.0..=1.0).contains(&gpu));
        }
    }
}
