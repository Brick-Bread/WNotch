//! Linux sources: the amdgpu `gpu_busy_percent` files or `nvidia-smi` for the GPU, and
//! `/sys/class/power_supply` for the battery.

use std::fs;
use std::io::ErrorKind;
use std::path::Path;
use std::process::Command;

/// Reads the busiest GPU's load. With neither an AMD card exposing `gpu_busy_percent` nor `nvidia-smi`,
/// there is nothing to show and the card is hidden.
pub struct Gpu {
    /// False once `nvidia-smi` turned out to be missing or broken.
    nvidia: bool,
    present: bool,
}

impl Gpu {
    pub fn new() -> Self {
        Self { nvidia: true, present: amd_usage().is_some() || nvidia_usage().is_some() }
    }

    pub fn is_present(&self) -> bool {
        self.present
    }

    pub fn usage(&mut self) -> Option<f64> {
        if let Some(usage) = amd_usage() {
            return Some(usage);
        }
        if !self.nvidia {
            return None;
        }
        let usage = nvidia_usage();
        self.nvidia = usage.is_some();
        usage
    }
}

/// `/sys/class/drm/card*/device/gpu_busy_percent`, the highest of all cards, as 0..1.
fn amd_usage() -> Option<f64> {
    let cards = fs::read_dir("/sys/class/drm").ok()?;
    cards
        .flatten()
        .filter(|card| {
            let name = card.file_name();
            let name = name.to_string_lossy();
            name.starts_with("card") && name[4..].bytes().all(|b| b.is_ascii_digit())
        })
        .filter_map(|card| fs::read_to_string(card.path().join("device/gpu_busy_percent")).ok())
        .filter_map(|text| text.trim().parse::<f64>().ok())
        .map(|percent| (percent / 100.0).clamp(0.0, 1.0))
        .reduce(f64::max)
}

fn nvidia_usage() -> Option<f64> {
    let output = match Command::new("nvidia-smi").args(["--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"]).output() {
        Ok(output) if output.status.success() => output,
        Ok(_) => return None,
        Err(e) => {
            if e.kind() != ErrorKind::NotFound {
                log::debug!("nvidia-smi failed: {e}");
            }
            return None;
        }
    };
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| line.trim().parse::<f64>().ok())
        .map(|percent| (percent / 100.0).clamp(0.0, 1.0))
        .reduce(f64::max)
}

/// The charge of the first battery, or `None` on a machine without one.
pub fn battery_percent() -> Option<u8> {
    let supplies = fs::read_dir("/sys/class/power_supply").ok()?;
    supplies.flatten().find_map(|supply| {
        let path = supply.path();
        let kind = fs::read_to_string(path.join("type")).ok()?;
        (kind.trim() == "Battery" && is_system_battery(&path)).then_some(())?;
        fs::read_to_string(path.join("capacity")).ok()?.trim().parse::<u8>().ok().map(|p| p.min(100))
    })
}

/// Peripherals (a wireless mouse) also show up as batteries, with scope "Device".
fn is_system_battery(path: &Path) -> bool {
    fs::read_to_string(path.join("scope")).map_or(true, |scope| scope.trim() != "Device")
}
