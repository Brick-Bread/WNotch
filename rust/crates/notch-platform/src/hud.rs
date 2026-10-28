//! The watchers that turn system events (volume, brightness, power, Bluetooth) into HUDs. Ported
//! from `src/Notch.Platform/Hud`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use notch_core::activities::Activity;
use notch_core::hud::PowerHudTracker;
use notch_core::hud::activities as hud;
use windows::Devices::Bluetooth::{BluetoothDevice, BluetoothLEDevice};
use windows::Devices::Enumeration::{DeviceInformation, DeviceInformationKind, DeviceInformationUpdate, DeviceWatcher};
use windows::Foundation::{EventHandler, IReference, TypedEventHandler};
use windows::System::Power::{BatteryStatus, PowerManager, PowerSupplyStatus};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, CoCreateInstance, CoSetProxyBlanket, EOAC_NONE, RPC_C_AUTHN_LEVEL_CALL,
    RPC_C_IMP_LEVEL_IMPERSONATE,
};
use windows::Win32::System::Rpc::{RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE};
use windows::Win32::System::Variant::{VARIANT, VT_UI1};
use windows::Win32::System::Wmi::{
    IEnumWbemClassObject, IWbemClassObject, IWbemLocator, WBEM_FLAG_FORWARD_ONLY, WBEM_FLAG_RETURN_IMMEDIATELY,
    WbemLocator,
};
use windows::core::{BSTR, HSTRING, IInspectable, IUnknown, Interface, Result};
use windows_collections::IIterable;

use crate::audio::{VolumeEvent, VolumeWatcher, init_com};

/// Where the HUDs go. Called on the watchers' threads.
pub type Publish = Arc<dyn Fn(Activity) + Send + Sync>;

/// Owns every watcher the machine supports; dropping it stops them.
pub struct SystemHudSources {
    _volume: Option<VolumeWatcher>,
    _brightness: Option<BrightnessWatcher>,
    _power: Option<PowerWatcher>,
    _bluetooth: Option<BluetoothWatcher>,
}

impl SystemHudSources {
    /// Starts every watcher the machine supports. Not every machine has every device: desktops
    /// lack a battery and a dimmable panel, some have no Bluetooth radio or audio output. What is
    /// missing is skipped. Call from a background thread, not the UI thread.
    pub fn start(publish: Publish) -> Self {
        init_com();
        let volume = {
            let publish = publish.clone();
            VolumeWatcher::new(move |event| {
                publish(match event {
                    VolumeEvent::Volume { level, muted } => hud::volume(f64::from(level), muted),
                    VolumeEvent::OutputChanged { name } => hud::audio_output(&name),
                });
            })
            .ok()
        };
        Self {
            _volume: volume,
            _brightness: BrightnessWatcher::start(publish.clone()).ok(),
            _power: PowerWatcher::start(publish.clone()).ok(),
            _bluetooth: BluetoothWatcher::start(publish).ok(),
        }
    }
}

/// Shows a HUD when the charger is connected or removed, and at low-battery marks.
pub struct PowerWatcher {
    tokens: [i64; 3],
}

impl PowerWatcher {
    pub fn start(publish: Publish) -> Result<Self> {
        let tracker = Arc::new(Mutex::new(PowerHudTracker::new()));
        let read = {
            let tracker = Arc::clone(&tracker);
            move || {
                let hud = (|| -> Result<Option<Activity>> {
                    let has_battery = PowerManager::BatteryStatus()? != BatteryStatus::NotPresent;
                    let plugged_in = PowerManager::PowerSupplyStatus()? != PowerSupplyStatus::NotPresent;
                    let percent = PowerManager::RemainingChargePercent()?;
                    Ok(tracker.lock().unwrap_or_else(|p| p.into_inner()).update(has_battery, plugged_in, percent))
                })();
                if let Ok(Some(hud)) = hud {
                    publish(hud);
                }
            }
        };

        let handler = {
            let read = read.clone();
            EventHandler::<IInspectable>::new(move |_, _| {
                read();
                Ok(())
            })
        };
        let tokens = [
            PowerManager::PowerSupplyStatusChanged(&handler)?,
            PowerManager::BatteryStatusChanged(&handler)?,
            PowerManager::RemainingChargePercentChanged(&handler)?,
        ];
        read();
        Ok(Self { tokens })
    }
}

impl Drop for PowerWatcher {
    fn drop(&mut self) {
        let [supply, battery, percent] = self.tokens;
        let _ = PowerManager::RemovePowerSupplyStatusChanged(supply);
        let _ = PowerManager::RemoveBatteryStatusChanged(battery);
        let _ = PowerManager::RemoveRemainingChargePercentChanged(percent);
    }
}

const IS_CONNECTED_PROPERTY: &str = "System.Devices.Aep.IsConnected";

struct Device {
    name: String,
    is_connected: bool,
}

/// Shows a HUD when a paired Bluetooth device connects or disconnects.
pub struct BluetoothWatcher {
    watchers: Vec<DeviceWatcher>,
}

impl BluetoothWatcher {
    pub fn start(publish: Publish) -> Result<Self> {
        let devices: Arc<Mutex<HashMap<String, Device>>> = Arc::default();
        // Classic (headsets, speakers) and Low Energy (mice, keyboards) are enumerated separately.
        let selectors = [
            BluetoothDevice::GetDeviceSelectorFromPairingState(true)?,
            BluetoothLEDevice::GetDeviceSelectorFromPairingState(true)?,
        ];
        let mut watchers = Vec::new();
        for selector in selectors {
            let watcher = create_watcher(&selector, &devices, &publish)?;
            watcher.Start()?;
            watchers.push(watcher);
        }
        Ok(Self { watchers })
    }
}

impl Drop for BluetoothWatcher {
    fn drop(&mut self) {
        for watcher in &self.watchers {
            // Stop fails if the watcher never finished starting.
            let _ = watcher.Stop();
        }
    }
}

fn is_connected(properties: &windows_collections::IMapView<HSTRING, IInspectable>) -> Option<bool> {
    let value = properties.Lookup(&HSTRING::from(IS_CONNECTED_PROPERTY)).ok()?;
    value.cast::<IReference<bool>>().ok()?.Value().ok()
}

fn create_watcher(
    selector: &HSTRING,
    devices: &Arc<Mutex<HashMap<String, Device>>>,
    publish: &Publish,
) -> Result<DeviceWatcher> {
    let properties: IIterable<HSTRING> = vec![HSTRING::from(IS_CONNECTED_PROPERTY)].into();
    let watcher = DeviceInformation::CreateWatcherWithKindAqsFilterAndAdditionalProperties(
        selector,
        &properties,
        DeviceInformationKind::AssociationEndpoint,
    )?;

    // Added carries each device's state at startup, so it only records the baseline.
    let added = Arc::clone(devices);
    watcher.Added(&TypedEventHandler::<DeviceWatcher, DeviceInformation>::new(move |_, info| {
        if let Some(info) = info.as_ref() {
            let connected = info.Properties().ok().and_then(|p| is_connected(&p)).unwrap_or(false);
            let device = Device { name: info.Name()?.to_string(), is_connected: connected };
            added.lock().unwrap_or_else(|p| p.into_inner()).insert(info.Id()?.to_string(), device);
        }
        Ok(())
    }))?;

    let updated = Arc::clone(devices);
    let publish = publish.clone();
    watcher.Updated(&TypedEventHandler::<DeviceWatcher, DeviceInformationUpdate>::new(move |_, update| {
        let Some(update) = update.as_ref() else { return Ok(()) };
        let Some(connected) = update.Properties().ok().and_then(|p| is_connected(&p)) else { return Ok(()) };
        let name = {
            let mut devices = updated.lock().unwrap_or_else(|p| p.into_inner());
            let Some(device) = devices.get_mut(&update.Id()?.to_string()) else { return Ok(()) };
            if device.is_connected == connected {
                return Ok(());
            }
            device.is_connected = connected;
            device.name.clone()
        };
        if !name.trim().is_empty() {
            publish(hud::bluetooth(&name, connected));
        }
        Ok(())
    }))?;

    let removed = Arc::clone(devices);
    watcher.Removed(&TypedEventHandler::<DeviceWatcher, DeviceInformationUpdate>::new(move |_, update| {
        if let Some(update) = update.as_ref() {
            removed.lock().unwrap_or_else(|p| p.into_inner()).remove(&update.Id()?.to_string());
        }
        Ok(())
    }))?;

    Ok(watcher)
}

/// Shows a HUD when the built-in panel's brightness changes. External monitors do not raise this
/// event, and most desktops have no panel WMI can see: then starting fails and the caller skips it.
pub struct BrightnessWatcher {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl BrightnessWatcher {
    pub fn start(publish: Publish) -> Result<Self> {
        init_com();
        let events = subscribe_brightness()?;
        // The enumerator is handed to the reading thread, in the same multithreaded apartment.
        let events = SendEnumerator(events);
        let stop = Arc::new(AtomicBool::new(false));
        let flag = Arc::clone(&stop);
        let thread = thread::Builder::new()
            .name("brightness".into())
            .spawn(move || {
                init_com();
                let events = events;
                while !flag.load(Ordering::Relaxed) {
                    let mut objects: [Option<IWbemClassObject>; 1] = [None];
                    let mut returned = 0u32;
                    // SAFETY: waits up to a second for one event object.
                    let _ = unsafe { events.0.Next(1000, &mut objects, &mut returned) };
                    if let Some(object) = objects[0].take()
                        && let Some(percent) = brightness(&object)
                    {
                        publish(hud::brightness(f64::from(percent) / 100.0));
                    }
                }
            })
            .map_err(|_| windows::core::Error::empty())?;
        Ok(Self { stop, thread: Some(thread) })
    }
}

impl Drop for BrightnessWatcher {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

struct SendEnumerator(IEnumWbemClassObject);

// SAFETY: WMI objects created in the multithreaded apartment may be used from its other threads.
unsafe impl Send for SendEnumerator {}

fn subscribe_brightness() -> Result<IEnumWbemClassObject> {
    // SAFETY: COM calls on interfaces the system returned, with proxy security set as WMI needs.
    unsafe {
        let locator: IWbemLocator = CoCreateInstance(&WbemLocator, None, CLSCTX_INPROC_SERVER)?;
        let services = locator.ConnectServer(
            &BSTR::from(r"root\wmi"),
            &BSTR::new(),
            &BSTR::new(),
            &BSTR::new(),
            0,
            &BSTR::new(),
            None,
        )?;
        set_blanket(&services.cast()?)?;
        let events = services.ExecNotificationQuery(
            &BSTR::from("WQL"),
            &BSTR::from("SELECT * FROM WmiMonitorBrightnessEvent"),
            WBEM_FLAG_RETURN_IMMEDIATELY | WBEM_FLAG_FORWARD_ONLY,
            None,
        )?;
        set_blanket(&events.cast()?)?;
        Ok(events)
    }
}

/// # Safety
/// `proxy` must be a live COM proxy.
unsafe fn set_blanket(proxy: &IUnknown) -> Result<()> {
    // SAFETY: see above.
    unsafe {
        CoSetProxyBlanket(
            proxy,
            RPC_C_AUTHN_WINNT,
            RPC_C_AUTHZ_NONE,
            None,
            RPC_C_AUTHN_LEVEL_CALL,
            RPC_C_IMP_LEVEL_IMPERSONATE,
            None,
            EOAC_NONE,
        )
    }
}

fn brightness(object: &IWbemClassObject) -> Option<u8> {
    let mut value = VARIANT::default();
    // SAFETY: reads one property into a VARIANT this function owns.
    unsafe {
        object.Get(&HSTRING::from("Brightness"), 0, &mut value, None, None).ok()?;
        let inner = &value.Anonymous.Anonymous;
        (inner.vt == VT_UI1).then(|| inner.Anonymous.bVal)
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;

    #[test]
    fn every_source_starts_and_stops_or_is_skipped() {
        // Build machines have few of these devices; what they lack must be skipped quietly.
        let (done, finished) = std::sync::mpsc::channel();
        thread::spawn(move || {
            let sources = SystemHudSources::start(Arc::new(|_| {}));
            drop(sources);
            let _ = done.send(());
        });
        assert!(finished.recv_timeout(Duration::from_secs(30)).is_ok(), "starting the HUD sources hung");
    }
}
