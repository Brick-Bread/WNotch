//! How loud the default output is right now (the peak meter the volume mixer shows). It does
//! not capture any audio.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

use notch_core::media::LevelSmoother;

/// A glow looks the same at 25 fps, and every update crosses into the UI.
const FRAME: Duration = Duration::from_millis(40);

/// How often to look again while nothing is playing.
const IDLE: Duration = Duration::from_millis(500);

/// Reads the output's peak level, 0..1.
pub trait PeakSource {
    fn peak(&mut self) -> f32;
}

/// Starts the thread that reports the smoothed output level through `emit` (about 25 times a
/// second, and only while `playing`). Without a loudness source on this system nothing is ever
/// reported, and the glow stays steady.
pub fn spawn(playing: Arc<AtomicBool>, emit: impl Fn(f64) + Send + 'static) {
    thread::spawn(move || {
        let Some(mut source) = platform::open() else {
            log::info!("no output loudness meter on this system; the audio glow stays steady");
            return;
        };

        let mut smoother = LevelSmoother::default();
        let mut last = Instant::now();
        let mut reported = f64::NAN;
        loop {
            if !playing.load(Ordering::Relaxed) {
                thread::sleep(IDLE);
                last = Instant::now();
                continue;
            }

            let now = Instant::now();
            let level = smoother.update(f64::from(source.peak()), now.duration_since(last).as_secs_f64());
            last = now;

            // Two decimals are finer than the eye can tell apart, and skip a quiet stream of events.
            let rounded = (level * 100.0).round() / 100.0;
            if rounded != reported {
                reported = rounded;
                emit(rounded);
            }
            thread::sleep(FRAME);
        }
    });
}

#[cfg(windows)]
mod platform {
    use std::time::{Duration, Instant};

    use windows::Win32::Media::Audio::Endpoints::IAudioMeterInformation;
    use windows::Win32::Media::Audio::{eMultimedia, eRender, IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED};

    use super::PeakSource;

    /// The default device can change at any time; resolving it again occasionally is simpler
    /// than watching for it.
    const DEVICE_REFRESH: Duration = Duration::from_secs(5);

    struct Wasapi {
        enumerator: IMMDeviceEnumerator,
        meter: Option<IAudioMeterInformation>,
        refresh_at: Instant,
    }

    pub fn open() -> Option<Box<dyn PeakSource>> {
        // SAFETY: plain COM initialisation of the calling thread, which owns the objects below.
        let enumerator = unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            CoCreateInstance::<_, IMMDeviceEnumerator>(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?
        };
        Some(Box::new(Wasapi { enumerator, meter: None, refresh_at: Instant::now() }))
    }

    impl Wasapi {
        fn default_meter(&self) -> Option<IAudioMeterInformation> {
            // SAFETY: the enumerator is alive; Activate hands back a new reference we own.
            unsafe {
                let device = self.enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia).ok()?;
                device.Activate::<IAudioMeterInformation>(CLSCTX_ALL, None).ok()
            }
        }
    }

    impl PeakSource for Wasapi {
        /// Zero when there is no output device.
        fn peak(&mut self) -> f32 {
            if self.meter.is_none() || Instant::now() >= self.refresh_at {
                self.meter = self.default_meter();
                self.refresh_at = Instant::now() + DEVICE_REFRESH;
            }

            // SAFETY: the meter is a live COM object created on this thread.
            match self.meter.as_ref().map(|meter| unsafe { meter.GetPeakValue() }) {
                Some(Ok(peak)) => peak,
                Some(Err(_)) => {
                    // The device disappeared mid-read; try again on the next refresh.
                    self.meter = None;
                    0.0
                }
                None => 0.0,
            }
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::PeakSource;

    /// PulseAudio and PipeWire expose no cheap whole-output peak without capturing audio.
    pub fn open() -> Option<Box<dyn PeakSource>> {
        None
    }
}
