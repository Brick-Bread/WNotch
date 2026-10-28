//! The default output device: how loud it is, its volume, and when it changes. Ported from
//! `src/Notch.Platform/Media/AudioLevelMeter.cs` and `src/Notch.Platform/Hud/VolumeWatcher.cs`,
//! which use NAudio; this talks to the same Core Audio interfaces directly.
//!
//! Everything here runs in the multithreaded COM apartment; each thread that uses it joins it first.

use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
use windows::Win32::Foundation::PROPERTYKEY;
use windows::Win32::Media::Audio::Endpoints::{
    IAudioEndpointVolume, IAudioEndpointVolumeCallback, IAudioEndpointVolumeCallback_Impl, IAudioMeterInformation,
};
use windows::Win32::Media::Audio::{
    AUDIO_VOLUME_NOTIFICATION_DATA, DEVICE_STATE, EDataFlow, ERole, IMMDevice, IMMDeviceEnumerator,
    IMMNotificationClient, IMMNotificationClient_Impl, MMDeviceEnumerator, eMultimedia, eRender,
};
use windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc;
use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree, STGM_READ,
};
use windows::core::{PCWSTR, Result, implement};

/// Joins the multithreaded COM apartment on this thread. Harmless when already in it.
pub fn init_com() {
    // SAFETY: no pointers; S_FALSE and RPC_E_CHANGED_MODE both leave COM usable for this code.
    let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
}

fn enumerator() -> Result<IMMDeviceEnumerator> {
    // SAFETY: creating the system's device enumerator.
    unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
}

fn default_output(enumerator: &IMMDeviceEnumerator) -> Result<IMMDevice> {
    // SAFETY: plain COM call.
    unsafe { enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia) }
}

fn friendly_name(device: &IMMDevice) -> Option<String> {
    // SAFETY: the string PropVariantToStringAlloc returns is freed with CoTaskMemFree.
    unsafe {
        let store = device.OpenPropertyStore(STGM_READ).ok()?;
        let key: *const PROPERTYKEY = &PKEY_Device_FriendlyName;
        let value = store.GetValue(key).ok()?;
        let text = PropVariantToStringAlloc(&value).ok()?;
        let name = text.to_string().ok();
        CoTaskMemFree(Some(text.0.cast()));
        name
    }
}

/// Reads how loud the default output is right now (the peak meter the volume mixer shows).
/// Cheap enough to call every frame; it does not capture any audio.
pub struct LevelMeter {
    enumerator: Option<IMMDeviceEnumerator>,
    meter: Option<IAudioMeterInformation>,
    refresh_at: Instant,
}

impl LevelMeter {
    // The default device can change at any time; re-resolving it occasionally is simpler than watching for it.
    const DEVICE_REFRESH: Duration = Duration::from_secs(5);

    pub fn new() -> Self {
        init_com();
        Self { enumerator: enumerator().ok(), meter: None, refresh_at: Instant::now() }
    }

    /// Peak level since the last call, 0..1. Zero when there is no output device.
    pub fn read(&mut self) -> f32 {
        if self.meter.is_none() || Instant::now() >= self.refresh_at {
            self.refresh_at = Instant::now() + Self::DEVICE_REFRESH;
            self.meter = self.enumerator.as_ref().and_then(|e| default_output(e).ok()).and_then(|device| {
                // SAFETY: activating a meter on a device the enumerator returned.
                unsafe { device.Activate::<IAudioMeterInformation>(CLSCTX_ALL, None) }.ok()
            });
        }

        // SAFETY: plain COM call.
        match self.meter.as_ref().map(|meter| unsafe { meter.GetPeakValue() }) {
            Some(Ok(level)) => level,
            Some(Err(_)) => {
                // The device disappeared mid-read; try again on the next refresh.
                self.meter = None;
                0.0
            }
            None => 0.0,
        }
    }
}

impl Default for LevelMeter {
    fn default() -> Self {
        Self::new()
    }
}

/// What the default output reports.
#[derive(Debug, Clone, PartialEq)]
pub enum VolumeEvent {
    /// The volume changed: 0..1, and whether it is muted.
    Volume { level: f32, muted: bool },
    /// A different output became the default; its name.
    OutputChanged { name: String },
}

type Report = Arc<dyn Fn(VolumeEvent) + Send + Sync>;

/// COM interfaces held by more than one thread. Every thread that touches them is in the
/// multithreaded apartment, where Core Audio objects may be used from any thread.
struct Attached {
    volume: IAudioEndpointVolume,
    callback: IAudioEndpointVolumeCallback,
}

// SAFETY: see `Attached`.
unsafe impl Send for Attached {}

struct Shared {
    report: Report,
    attached: Mutex<Option<Attached>>,
    stopped: Mutex<bool>,
}

impl Shared {
    fn attach(&self, enumerator: &IMMDeviceEnumerator, announce: bool) {
        let mut attached = self.attached.lock().unwrap_or_else(|p| p.into_inner());
        if *self.stopped.lock().unwrap_or_else(|p| p.into_inner()) {
            return;
        }
        detach(&mut attached);

        // The device can go away between the notification and these calls; then there is nothing to attach to.
        let Ok(device) = default_output(enumerator) else { return };
        // SAFETY: activating and registering on a device the enumerator returned.
        let Ok(volume) = (unsafe { device.Activate::<IAudioEndpointVolume>(CLSCTX_ALL, None) }) else { return };
        let callback: IAudioEndpointVolumeCallback = VolumeCallback { report: self.report.clone() }.into();
        // SAFETY: as above.
        if unsafe { volume.RegisterControlChangeNotify(&callback) }.is_err() {
            return;
        }
        *attached = Some(Attached { volume, callback });

        if announce && let Some(name) = friendly_name(&device) {
            (self.report)(VolumeEvent::OutputChanged { name });
        }
    }
}

fn detach(attached: &mut Option<Attached>) {
    if let Some(Attached { volume, callback }) = attached.take() {
        // SAFETY: unregistering the callback registered in `attach`.
        unsafe {
            let _ = volume.UnregisterControlChangeNotify(&callback);
        }
    }
}

/// Reports when the default output's volume changes or a different output becomes the default.
/// `report` is called on Core Audio's threads.
pub struct VolumeWatcher {
    shared: Arc<Shared>,
    enumerator: IMMDeviceEnumerator,
    client: IMMNotificationClient,
}

impl VolumeWatcher {
    pub fn new(report: impl Fn(VolumeEvent) + Send + Sync + 'static) -> Result<Self> {
        init_com();
        let enumerator = enumerator()?;
        let shared =
            Arc::new(Shared { report: Arc::new(report), attached: Mutex::new(None), stopped: Mutex::new(false) });
        let client: IMMNotificationClient = DeviceCallback { shared: Arc::clone(&shared) }.into();
        // SAFETY: the client is unregistered on drop.
        unsafe { enumerator.RegisterEndpointNotificationCallback(&client)? };
        shared.attach(&enumerator, false);
        Ok(Self { shared, enumerator, client })
    }
}

impl Drop for VolumeWatcher {
    fn drop(&mut self) {
        *self.shared.stopped.lock().unwrap_or_else(|p| p.into_inner()) = true;
        detach(&mut self.shared.attached.lock().unwrap_or_else(|p| p.into_inner()));
        // SAFETY: unregistering the client registered in `new`.
        unsafe {
            let _ = self.enumerator.UnregisterEndpointNotificationCallback(&self.client);
        }
    }
}

#[implement(IAudioEndpointVolumeCallback)]
struct VolumeCallback {
    report: Report,
}

impl IAudioEndpointVolumeCallback_Impl for VolumeCallback_Impl {
    fn OnNotify(&self, data: *mut AUDIO_VOLUME_NOTIFICATION_DATA) -> Result<()> {
        // SAFETY: Core Audio passes a valid notification for the duration of the call.
        if let Some(data) = unsafe { data.as_ref() } {
            (self.report)(VolumeEvent::Volume { level: data.fMasterVolume, muted: data.bMuted.as_bool() });
        }
        Ok(())
    }
}

#[implement(IMMNotificationClient)]
struct DeviceCallback {
    shared: Arc<Shared>,
}

impl IMMNotificationClient_Impl for DeviceCallback_Impl {
    fn OnDefaultDeviceChanged(&self, flow: EDataFlow, role: ERole, _: &PCWSTR) -> Result<()> {
        if flow == eRender && role == eMultimedia {
            // Raised on the audio worker thread, which must not call back into the audio APIs.
            let shared = Arc::clone(&self.shared);
            thread::spawn(move || {
                init_com();
                if let Ok(enumerator) = enumerator() {
                    shared.attach(&enumerator, true);
                }
            });
        }
        Ok(())
    }

    fn OnDeviceStateChanged(&self, _: &PCWSTR, _: DEVICE_STATE) -> Result<()> {
        Ok(())
    }

    fn OnDeviceAdded(&self, _: &PCWSTR) -> Result<()> {
        Ok(())
    }

    fn OnDeviceRemoved(&self, _: &PCWSTR) -> Result<()> {
        Ok(())
    }

    fn OnPropertyValueChanged(&self, _: &PCWSTR, _: &PROPERTYKEY) -> Result<()> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Build machines often have no audio device; these check that its absence is handled quietly.

    #[test]
    fn the_meter_reads_a_level_or_zero() {
        let mut meter = LevelMeter::new();
        let level = meter.read();
        assert!((0.0..=1.0).contains(&level));
    }

    #[test]
    fn the_watcher_starts_and_stops_with_or_without_a_device() {
        let watcher = VolumeWatcher::new(|_| {}).expect("the device enumerator exists on every Windows");
        drop(watcher);
    }
}
