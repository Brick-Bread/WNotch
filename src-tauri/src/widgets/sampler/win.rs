//! Windows sources: the "GPU Engine" performance counters (what Task Manager shows) and the
//! system power status.

use std::collections::HashMap;
use std::ptr::{null, null_mut};

use windows_sys::Win32::System::Performance::{
    PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterArrayW, PdhOpenQueryW,
    PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_DOUBLE, PDH_HCOUNTER, PDH_HQUERY, PDH_MORE_DATA,
};
use windows_sys::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};

/// `PDH_CSTATUS_VALID_DATA` and `PDH_CSTATUS_NEW_DATA`: the counter produced a value.
const VALID_STATUSES: [u32; 2] = [0, 1];
/// `BatteryFlag` bit: no system battery.
const NO_BATTERY: u8 = 128;

/// Reads the busiest GPU engine type (3D, video decode, ...) summed over all processes.
pub struct Gpu {
    query: PDH_HQUERY,
    counter: PDH_HCOUNTER,
}

// A PDH query may be used from any thread as long as it is not used from two at once, which
// the sampler's owner guarantees by keeping it behind a mutex.
unsafe impl Send for Gpu {}

impl Gpu {
    /// Opens the counter. Without GPU counters (very old drivers, or counters disabled) the load reads as n/a.
    pub fn new() -> Self {
        let mut gpu = Gpu { query: null_mut(), counter: null_mut() };
        let path: Vec<u16> = "\\GPU Engine(*)\\Utilization Percentage\0".encode_utf16().collect();
        // SAFETY: the pointers handed to PDH are valid for the calls and the handles are only used here.
        unsafe {
            let mut query: PDH_HQUERY = null_mut();
            if PdhOpenQueryW(null(), 0, &mut query) != 0 {
                return gpu;
            }
            let mut counter: PDH_HCOUNTER = null_mut();
            if PdhAddEnglishCounterW(query, path.as_ptr(), 0, &mut counter) != 0 {
                PdhCloseQuery(query);
                log::info!("no GPU performance counters on this machine; GPU load will read n/a");
                return gpu;
            }
            // Rates need two readings: this one is the baseline.
            PdhCollectQueryData(query);
            gpu.query = query;
            gpu.counter = counter;
        }
        gpu
    }

    /// Every Windows machine shows the card; without counters it reads "n/a".
    pub fn is_present(&self) -> bool {
        true
    }

    /// The busiest engine type's load, 0..1, or `None` when the counters are not there.
    pub fn usage(&mut self) -> Option<f64> {
        if self.query.is_null() {
            return None;
        }
        // SAFETY: see `new`; the buffer is sized by PDH's own answer and aligned for the item type.
        unsafe {
            if PdhCollectQueryData(self.query) != 0 {
                return Some(0.0);
            }
            let (mut bytes, mut count) = (0u32, 0u32);
            if PdhGetFormattedCounterArrayW(self.counter, PDH_FMT_DOUBLE, &mut bytes, &mut count, null_mut()) != PDH_MORE_DATA {
                return Some(0.0);
            }
            let mut buffer = vec![0u64; (bytes as usize).div_ceil(8)];
            let items = buffer.as_mut_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>();
            if PdhGetFormattedCounterArrayW(self.counter, PDH_FMT_DOUBLE, &mut bytes, &mut count, items) != 0 {
                return Some(0.0);
            }

            // Instance names look like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D".
            let mut by_engine: HashMap<String, f64> = HashMap::new();
            for item in std::slice::from_raw_parts(items, count as usize) {
                if !VALID_STATUSES.contains(&item.FmtValue.CStatus) {
                    continue;
                }
                let name = wide_to_string(item.szName);
                let engine = name.rfind("engtype_").map_or("", |at| &name[at..]).to_owned();
                *by_engine.entry(engine).or_default() += item.FmtValue.Anonymous.doubleValue;
            }
            Some((by_engine.values().copied().fold(0.0, f64::max) / 100.0).clamp(0.0, 1.0))
        }
    }
}

impl Drop for Gpu {
    fn drop(&mut self) {
        if !self.query.is_null() {
            // SAFETY: the query was opened by `new` and is closed exactly once.
            unsafe { PdhCloseQuery(self.query) };
        }
    }
}

/// # Safety
/// `text` must be null or point to a NUL-terminated UTF-16 string.
unsafe fn wide_to_string(text: *const u16) -> String {
    if text.is_null() {
        return String::new();
    }
    let mut length = 0;
    while *text.add(length) != 0 {
        length += 1;
    }
    String::from_utf16_lossy(std::slice::from_raw_parts(text, length))
}

/// The charge, or `None` on a machine without a battery.
pub fn battery_percent() -> Option<u8> {
    let mut status = SYSTEM_POWER_STATUS {
        ACLineStatus: 0,
        BatteryFlag: 0,
        BatteryLifePercent: 0,
        SystemStatusFlag: 0,
        BatteryLifeTime: 0,
        BatteryFullLifeTime: 0,
    };
    // SAFETY: `status` is a valid out structure.
    let ok = unsafe { GetSystemPowerStatus(&mut status) } != 0;
    (ok && status.BatteryFlag & NO_BATTERY == 0 && status.BatteryLifePercent <= 100).then_some(status.BatteryLifePercent)
}
