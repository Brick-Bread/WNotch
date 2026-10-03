//! The Windows watchers: Core Audio for volume and the default output, WMI for the built-in
//! panel's brightness, the power status for the charger, WinRT for Bluetooth, and the keyboard
//! state for Caps Lock.

use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use notch_core::hud::{self, BluetoothHudTracker};
use windows::core::{implement, w, Interface, BSTR, HSTRING, PCWSTR};
use windows::Devices::Bluetooth::{BluetoothDevice, BluetoothLEDevice};
use windows::Devices::Enumeration::{
    DeviceInformation, DeviceInformationKind, DeviceInformationUpdate, DeviceWatcher,
};
use windows::Foundation::{IPropertyValue, TypedEventHandler};
use windows::Win32::Foundation::PROPERTYKEY;
use windows::Win32::Media::Audio::Endpoints::{
    IAudioEndpointVolume, IAudioEndpointVolumeCallback, IAudioEndpointVolumeCallback_Impl,
};
use windows::Win32::Media::Audio::{
    eMultimedia, eRender, EDataFlow, ERole, IMMDevice, IMMDeviceEnumerator, IMMNotificationClient,
    IMMNotificationClient_Impl, MMDeviceEnumerator, AUDIO_VOLUME_NOTIFICATION_DATA, DEVICE_STATE,
};
use windows_collections::IIterable;
use windows::Win32::System::Com::StructuredStorage::PropVariantClear;
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoSetProxyBlanket, CLSCTX_ALL, CLSCTX_INPROC_SERVER,
    COINIT_MULTITHREADED, EOAC_NONE, RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, STGM_READ,
};
use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
use windows::Win32::System::Rpc::{RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE};
use windows::Win32::System::Variant::{VariantClear, VARIANT, VT_I4, VT_UI1, VT_UI2};
use windows::Win32::System::Wmi::{
    IWbemClassObject, IWbemLocator, WbemLocator, WBEM_FLAG_FORWARD_ONLY, WBEM_FLAG_RETURN_IMMEDIATELY,
    WBEM_INFINITE,
};
use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
use windows::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, GetKeyState, VK_CAPITAL};

use super::{spawn, watch_caps_lock, watch_power, PowerReading};
use crate::activity_hub::ActivityHub;

/// Caps Lock is polled this often. Short enough not to miss a key tap, cheap enough not to matter.
const CAPS_LOCK_INTERVAL: Duration = Duration::from_millis(25);

pub fn start(hub: &Arc<ActivityHub>) {
    let volume_hub = hub.clone();
    spawn("volume", move || volume(&volume_hub));
    let brightness_hub = hub.clone();
    spawn("brightness", move || brightness(&brightness_hub));
    let power_hub = hub.clone();
    spawn("power", move || watch_power(&power_hub, read_power));
    let bluetooth_hub = hub.clone();
    spawn("bluetooth", move || bluetooth(&bluetooth_hub));
    let caps_hub = hub.clone();
    spawn("caps-lock", move || watch_caps_lock(&caps_hub, CAPS_LOCK_INTERVAL, caps_lock_reader()));
}

// Volume -------------------------------------------------------------------

/// Publishes the volume of whatever endpoint is attached. Runs on the audio worker thread, so it
/// must not call back into the audio APIs.
#[implement(IAudioEndpointVolumeCallback)]
struct VolumeCallback {
    hub: Arc<ActivityHub>,
}

impl IAudioEndpointVolumeCallback_Impl for VolumeCallback_Impl {
    fn OnNotify(&self, notify: *mut AUDIO_VOLUME_NOTIFICATION_DATA) -> windows::core::Result<()> {
        // SAFETY: Core Audio passes a valid notification for the duration of the call.
        if let Some(data) = unsafe { notify.as_ref() } {
            self.hub.publish(hud::volume(f64::from(data.fMasterVolume), data.bMuted.as_bool()));
        }
        Ok(())
    }
}

/// Tells the volume thread that the default output changed. Re-attaching has to happen on that
/// thread: the audio worker that calls this must not call back into the audio APIs.
#[implement(IMMNotificationClient)]
struct DeviceCallback {
    changed: Mutex<Sender<()>>,
}

impl IMMNotificationClient_Impl for DeviceCallback_Impl {
    fn OnDeviceStateChanged(&self, _: &PCWSTR, _: DEVICE_STATE) -> windows::core::Result<()> {
        Ok(())
    }

    fn OnDeviceAdded(&self, _: &PCWSTR) -> windows::core::Result<()> {
        Ok(())
    }

    fn OnDeviceRemoved(&self, _: &PCWSTR) -> windows::core::Result<()> {
        Ok(())
    }

    fn OnDefaultDeviceChanged(&self, flow: EDataFlow, role: ERole, _: &PCWSTR) -> windows::core::Result<()> {
        if flow == eRender && role == eMultimedia {
            if let Ok(changed) = self.changed.lock() {
                // The thread is gone only when the app is quitting.
                let _ = changed.send(());
            }
        }
        Ok(())
    }

    fn OnPropertyValueChanged(&self, _: &PCWSTR, _: &PROPERTYKEY) -> windows::core::Result<()> {
        Ok(())
    }
}

/// The default output with the volume callback registered on it.
struct Attached {
    endpoint: IAudioEndpointVolume,
    callback: IAudioEndpointVolumeCallback,
}

impl Drop for Attached {
    fn drop(&mut self) {
        // SAFETY: the callback was registered on this endpoint in `attach`.
        // The device may already be gone, in which case there is nothing to unregister.
        let _ = unsafe { self.endpoint.UnregisterControlChangeNotify(&self.callback) };
    }
}

fn volume(hub: &Arc<ActivityHub>) {
    if let Err(e) = watch_volume(hub) {
        log::info!("volume HUD unavailable: {e}");
    }
}

fn watch_volume(hub: &Arc<ActivityHub>) -> windows::core::Result<()> {
    // SAFETY: plain COM calls on a thread that initialised COM and keeps the objects alive.
    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;
        let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
        let (sender, changes) = mpsc::channel();
        let client: IMMNotificationClient = DeviceCallback { changed: Mutex::new(sender) }.into();
        enumerator.RegisterEndpointNotificationCallback(&client)?;

        let mut attached = attach(&enumerator, hub, false);
        while changes.recv().is_ok() {
            // A burst of notifications (one per role) re-attaches once.
            while changes.try_recv().is_ok() {}
            drop(attached.take());
            attached = attach(&enumerator, hub, true);
        }
        let _ = enumerator.UnregisterEndpointNotificationCallback(&client);
    }
    Ok(())
}

/// Attaches to the current default output; `announce` also shows which output it is.
/// `None` when there is no output (or it vanished between the notification and the query).
///
/// # Safety
/// COM must be initialised on the calling thread.
unsafe fn attach(enumerator: &IMMDeviceEnumerator, hub: &Arc<ActivityHub>, announce: bool) -> Option<Attached> {
    let device = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia).ok()?;
    let endpoint: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None).ok()?;
    let callback: IAudioEndpointVolumeCallback = VolumeCallback { hub: hub.clone() }.into();
    endpoint.RegisterControlChangeNotify(&callback).ok()?;
    if announce {
        if let Some(name) = friendly_name(&device) {
            hub.publish(hud::audio_output(&name));
        }
    }
    Some(Attached { endpoint, callback })
}

/// The name Windows shows for the device, such as "Speakers (Realtek Audio)".
///
/// # Safety
/// COM must be initialised on the calling thread.
unsafe fn friendly_name(device: &IMMDevice) -> Option<String> {
    let store = device.OpenPropertyStore(STGM_READ).ok()?;
    let mut value = store.GetValue(&PKEY_Device_FriendlyName).ok()?;
    let name = value.Anonymous.Anonymous.Anonymous.pwszVal;
    let name = (!name.is_null()).then(|| name.to_string().ok()).flatten();
    let _ = PropVariantClear(&mut value);
    name
}

// Brightness ---------------------------------------------------------------

fn brightness(hub: &ActivityHub) {
    if let Err(e) = watch_brightness(hub) {
        // Desktops and external monitors have no WMI-controllable panel.
        log::info!("brightness HUD unavailable: {e}");
    }
}

/// Listens for `WmiMonitorBrightnessEvent`, which only the built-in panel raises.
fn watch_brightness(hub: &ActivityHub) -> windows::core::Result<()> {
    // SAFETY: plain COM calls on a thread that initialised COM and keeps the objects alive.
    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;
        let locator: IWbemLocator = CoCreateInstance(&WbemLocator, None, CLSCTX_INPROC_SERVER)?;
        let services = locator.ConnectServer(
            &BSTR::from("ROOT\\WMI"),
            &BSTR::new(),
            &BSTR::new(),
            &BSTR::new(),
            0,
            &BSTR::new(),
            None,
        )?;
        CoSetProxyBlanket(
            &services,
            RPC_C_AUTHN_WINNT,
            RPC_C_AUTHZ_NONE,
            PCWSTR::null(),
            RPC_C_AUTHN_LEVEL_CALL,
            RPC_C_IMP_LEVEL_IMPERSONATE,
            None,
            EOAC_NONE,
        )?;
        let events = services.ExecNotificationQuery(
            &BSTR::from("WQL"),
            &BSTR::from("SELECT * FROM WmiMonitorBrightnessEvent"),
            WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
            None,
        )?;

        loop {
            let mut event: [Option<IWbemClassObject>; 1] = [None];
            let mut returned = 0;
            events.Next(WBEM_INFINITE, &mut event, &mut returned).ok()?;
            if let Some(percent) = event[0].as_ref().and_then(|e| read_brightness(e)) {
                hub.publish(hud::brightness(f64::from(percent) / 100.0));
            }
        }
    }
}

/// The `Brightness` property (0..100) of a brightness event.
///
/// # Safety
/// COM must be initialised on the calling thread.
unsafe fn read_brightness(event: &IWbemClassObject) -> Option<u32> {
    let mut value = VARIANT::default();
    event.Get(w!("Brightness"), 0, &mut value, None, None).ok()?;
    let inner = &value.Anonymous.Anonymous;
    let percent = match inner.vt {
        VT_UI1 => Some(u32::from(inner.Anonymous.bVal)),
        VT_UI2 => Some(u32::from(inner.Anonymous.uiVal)),
        VT_I4 => u32::try_from(inner.Anonymous.lVal).ok(),
        _ => None,
    };
    let _ = VariantClear(&mut value);
    percent
}

// Power --------------------------------------------------------------------

fn read_power() -> Option<PowerReading> {
    let mut status = SYSTEM_POWER_STATUS::default();
    // SAFETY: `status` is a valid out parameter.
    unsafe { GetSystemPowerStatus(&mut status) }.ok()?;
    const NO_BATTERY: u8 = 128;
    const UNKNOWN: u8 = 255;
    Some(PowerReading {
        has_battery: status.BatteryFlag != UNKNOWN && status.BatteryFlag & NO_BATTERY == 0,
        plugged_in: status.ACLineStatus == 1,
        percent: if status.BatteryLifePercent == UNKNOWN { return None } else { i32::from(status.BatteryLifePercent) },
    })
}

// Bluetooth ----------------------------------------------------------------

const IS_CONNECTED: &str = "System.Devices.Aep.IsConnected";

fn bluetooth(hub: &Arc<ActivityHub>) {
    if let Err(e) = watch_bluetooth(hub) {
        log::info!("Bluetooth HUD unavailable: {e}");
    }
}

/// Watches paired devices. Classic (headsets, speakers) and Low Energy (mice, keyboards) devices
/// are enumerated separately. Added carries each device's state at startup, so it only records
/// the baseline.
fn watch_bluetooth(hub: &Arc<ActivityHub>) -> windows::core::Result<()> {
    let tracker = Arc::new(Mutex::new(BluetoothHudTracker::new()));
    let selectors = [
        BluetoothDevice::GetDeviceSelectorFromPairingState(true)?,
        BluetoothLEDevice::GetDeviceSelectorFromPairingState(true)?,
    ];

    let mut watchers = Vec::new();
    for selector in &selectors {
        let properties: IIterable<HSTRING> = vec![HSTRING::from(IS_CONNECTED)].into();
        let watcher = DeviceInformation::CreateWatcherWithKindAqsFilterAndAdditionalProperties(
            selector,
            &properties,
            DeviceInformationKind::AssociationEndpoint,
        )?;

        let added = tracker.clone();
        watcher.Added(&TypedEventHandler::new(move |_: windows::core::Ref<'_, DeviceWatcher>, info: windows::core::Ref<'_, DeviceInformation>| {
            if let Some(info) = info.as_ref() {
                let connected = info.Properties().ok().and_then(|p| read_connected(|key| p.Lookup(key).ok())).unwrap_or(false);
                if let (Ok(id), Ok(name)) = (info.Id(), info.Name()) {
                    lock(&added).added(&id.to_string(), &name.to_string(), connected);
                }
            }
            Ok(())
        }))?;

        let (updated, updates_hub) = (tracker.clone(), hub.clone());
        watcher.Updated(&TypedEventHandler::new(move |_: windows::core::Ref<'_, DeviceWatcher>, update: windows::core::Ref<'_, DeviceInformationUpdate>| {
            if let Some(update) = update.as_ref() {
                let connected = update.Properties().ok().and_then(|p| read_connected(|key| p.Lookup(key).ok()));
                if let (Some(connected), Ok(id)) = (connected, update.Id()) {
                    if let Some(activity) = lock(&updated).updated(&id.to_string(), connected) {
                        updates_hub.publish(activity);
                    }
                }
            }
            Ok(())
        }))?;

        let removed = tracker.clone();
        watcher.Removed(&TypedEventHandler::new(move |_: windows::core::Ref<'_, DeviceWatcher>, update: windows::core::Ref<'_, DeviceInformationUpdate>| {
            if let Some(Ok(id)) = update.as_ref().map(DeviceInformationUpdate::Id) {
                lock(&removed).removed(&id.to_string());
            }
            Ok(())
        }))?;

        watcher.Start()?;
        watchers.push(watcher);
    }

    // The watchers deliver on thread-pool threads for as long as they exist.
    loop {
        std::thread::park();
    }
}

fn lock(tracker: &Mutex<BluetoothHudTracker>) -> std::sync::MutexGuard<'_, BluetoothHudTracker> {
    tracker.lock().unwrap_or_else(|e| e.into_inner())
}

/// The `IsConnected` property out of a device's property map, if it carries one.
fn read_connected(lookup: impl Fn(&HSTRING) -> Option<windows::core::IInspectable>) -> Option<bool> {
    lookup(&HSTRING::from(IS_CONNECTED))?.cast::<IPropertyValue>().ok()?.GetBoolean().ok()
}

// Caps Lock ----------------------------------------------------------------

/// A reader of the Caps Lock state. `GetKeyState` only follows the keyboard on a thread that
/// handles messages, so this watches key presses instead (no keyboard hook, which would slow every
/// keystroke) and flips its own copy of the toggle on each press, starting from the real state.
fn caps_lock_reader() -> impl FnMut() -> Option<bool> {
    // SAFETY: these calls only read keyboard state.
    let mut on = unsafe { GetKeyState(i32::from(VK_CAPITAL.0)) } & 1 != 0;
    let mut was_down = false;
    move || {
        // SAFETY: as above.
        let down = unsafe { GetAsyncKeyState(i32::from(VK_CAPITAL.0)) } < 0;
        if down && !was_down {
            on = !on;
        }
        was_down = down;
        Some(on)
    }
}
