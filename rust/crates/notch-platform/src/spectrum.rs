//! Listens to what the default output is playing (WASAPI loopback) and reports how loud it is, in
//! total and in [`BAND_COUNT`] bands of pitch. Only levels leave this module; the samples are
//! analysed in place and discarded. Ported from `src/Notch.Platform/Media/SpectrumAnalyzer.cs`.
//!
//! The analysis is plain arithmetic and compiles everywhere; the capture is Windows only.

use std::f64::consts::PI;

/// The number of values in [`AudioFrame::bands`].
pub const BAND_COUNT: usize = 16;

const WINDOW_SIZE: usize = 2048;
const LOWEST_HZ: f64 = 40.0;
const HIGHEST_HZ: f64 = 16000.0;

/// How loud what the PC is playing is right now, in total and by pitch. Levels only: a plugin
/// never receives the sound itself.
#[derive(Debug, Clone, PartialEq)]
pub struct AudioFrame {
    /// Overall loudness, 0..1.
    pub level: f64,
    /// Loudness of the low end (below about 250 Hz), 0..1.
    pub bass: f64,
    /// Loudness of the middle, 0..1.
    pub mid: f64,
    /// Loudness of the high end (above about 4 kHz), 0..1.
    pub treble: f64,
    /// Loudness from the lowest pitch to the highest, 0..1, each band covering the same musical
    /// interval, already scaled to a useful range.
    pub bands: [f32; BAND_COUNT],
}

impl AudioFrame {
    /// What a frame looks like when nothing is playing.
    pub const SILENT: Self = Self { level: 0.0, bass: 0.0, mid: 0.0, treble: 0.0, bands: [0.0; BAND_COUNT] };
}

/// Turns mono samples into [`AudioFrame`]s.
pub struct Analyzer {
    ring: Vec<f32>,
    written: usize,
    re: Vec<f64>,
    im: Vec<f64>,
    window: Vec<f64>,
    bin_ranges: [(usize, usize); BAND_COUNT],
    tilt: [f64; BAND_COUNT],
}

impl Analyzer {
    pub fn new(sample_rate: u32) -> Self {
        let window =
            (0..WINDOW_SIZE).map(|i| 0.5 - 0.5 * (2.0 * PI * i as f64 / (WINDOW_SIZE - 1) as f64).cos()).collect();

        let bin_hz = f64::from(sample_rate) / WINDOW_SIZE as f64;
        let top = HIGHEST_HZ.min(f64::from(sample_rate) / 2.0 * 0.95);
        let ratio = (top / LOWEST_HZ).powf(1.0 / BAND_COUNT as f64);
        let mut bin_ranges = [(0, 0); BAND_COUNT];
        let mut tilt = [0.0; BAND_COUNT];
        let mut previous = 0;
        for i in 0..BAND_COUNT {
            let low = LOWEST_HZ * ratio.powi(i as i32);
            let high = low * ratio;
            let from = previous.max((low / bin_hz).round_ties_even() as usize);
            let to = from.max(((high / bin_hz).round_ties_even() as usize).saturating_sub(1));
            bin_ranges[i] = (from, to);
            previous = to + 1;

            // Music has far more energy low down; lifting the highs by 3 dB an octave evens the picture out.
            tilt[i] = 3.0 * ((low * high).sqrt() / 1000.0).log2();
        }

        Self {
            ring: vec![0.0; WINDOW_SIZE],
            written: 0,
            re: vec![0.0; WINDOW_SIZE],
            im: vec![0.0; WINDOW_SIZE],
            window,
            bin_ranges,
            tilt,
        }
    }

    pub fn push(&mut self, sample: f32) {
        self.ring[self.written % WINDOW_SIZE] = sample;
        self.written += 1;
    }

    /// Whether a whole window of sound has arrived.
    pub fn is_ready(&self) -> bool {
        self.written >= WINDOW_SIZE
    }

    /// The frame for the newest window of samples.
    pub fn frame(&mut self) -> AudioFrame {
        let mut energy = 0.0;
        for i in 0..WINDOW_SIZE {
            let sample = f64::from(self.ring[(self.written + i) % WINDOW_SIZE]);
            energy += sample * sample;
            self.re[i] = sample * self.window[i];
            self.im[i] = 0.0;
        }

        fft(&mut self.re, &mut self.im);

        // A full-scale sine comes out as 1: the Hann window halves it, and half of the spectrum is mirrored.
        let scale = 4.0 / WINDOW_SIZE as f64;
        let mut bands = [0f32; BAND_COUNT];
        for (b, band) in bands.iter_mut().enumerate() {
            let (from, to) = self.bin_ranges[b];
            let mut peak = 0f64;
            for k in from..=to.min(WINDOW_SIZE / 2 - 1) {
                peak = peak.max((self.re[k] * self.re[k] + self.im[k] * self.im[k]).sqrt() * scale);
            }
            let db = 20.0 * (peak + 1e-9).log10() + self.tilt[b];
            *band = ((db + 62.0) / 52.0).clamp(0.0, 1.0) as f32;
        }

        let rms_db = 20.0 * ((energy / WINDOW_SIZE as f64).sqrt() + 1e-9).log10();
        let level = ((rms_db + 48.0) / 42.0).clamp(0.0, 1.0);
        let mean =
            |from: usize, to: usize| bands[from..to].iter().map(|&v| f64::from(v)).sum::<f64>() / (to - from) as f64;

        AudioFrame { level, bass: mean(0, 4), mid: mean(4, 11), treble: mean(11, BAND_COUNT), bands }
    }
}

/// In-place radix-2 FFT; the length must be a power of two.
fn fft(re: &mut [f64], im: &mut [f64]) {
    let n = re.len();
    let mut j = 0;
    for i in 1..n {
        let mut bit = n >> 1;
        while j & bit != 0 {
            j ^= bit;
            bit >>= 1;
        }
        j ^= bit;
        if i < j {
            re.swap(i, j);
            im.swap(i, j);
        }
    }

    let mut length = 2;
    while length <= n {
        let angle = -2.0 * PI / length as f64;
        let (wr, wi) = (angle.cos(), angle.sin());
        for start in (0..n).step_by(length) {
            let (mut cr, mut ci) = (1.0, 0.0);
            for k in 0..length / 2 {
                let a = start + k;
                let b = a + length / 2;
                let tr = re[b] * cr - im[b] * ci;
                let ti = re[b] * ci + im[b] * cr;
                re[b] = re[a] - tr;
                im[b] = im[a] - ti;
                re[a] += tr;
                im[a] += ti;
                (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
            }
        }
        length <<= 1;
    }
}

#[cfg(windows)]
pub use capture::SpectrumAnalyzer;

#[cfg(windows)]
mod capture {
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::thread::{self, JoinHandle};
    use std::time::{Duration, Instant};

    use windows::Win32::Media::Audio::{
        AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator,
        MMDeviceEnumerator, WAVEFORMATEX, WAVEFORMATEXTENSIBLE, eConsole, eRender,
    };
    use windows::Win32::Media::KernelStreaming::WAVE_FORMAT_EXTENSIBLE;
    use windows::Win32::Media::Multimedia::{KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, WAVE_FORMAT_IEEE_FLOAT};
    use windows::Win32::System::Com::{CLSCTX_ALL, CoCreateInstance, CoTaskMemFree};

    use super::{Analyzer, AudioFrame};
    use crate::audio::init_com;

    // A new picture at most this often; the glow redraws at 25 frames a second.
    const INTERVAL: Duration = Duration::from_millis(25);

    /// Listens while it exists: create it to start, drop it to stop. `publish` is called on the
    /// capture thread.
    pub struct SpectrumAnalyzer {
        stop: Arc<AtomicBool>,
        thread: Option<JoinHandle<()>>,
    }

    impl SpectrumAnalyzer {
        pub fn start(publish: impl FnMut(AudioFrame) + Send + 'static) -> Self {
            let stop = Arc::new(AtomicBool::new(false));
            let flag = Arc::clone(&stop);
            let thread = thread::Builder::new().name("spectrum".into()).spawn(move || run(&flag, publish)).ok();
            Self { stop, thread }
        }
    }

    impl Drop for SpectrumAnalyzer {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Relaxed);
            if let Some(thread) = self.thread.take() {
                let _ = thread.join();
            }
        }
    }

    fn run(stop: &AtomicBool, mut publish: impl FnMut(AudioFrame)) {
        init_com();
        while !stop.load(Ordering::Relaxed) {
            let retry = match capture(stop, &mut publish) {
                // The default device changed or went away; follow it.
                Ok(()) => Duration::from_secs(1),
                // No output device right now (unplugged, being switched): look again in a moment.
                Err(_) => Duration::from_secs(3),
            };
            let until = Instant::now() + retry;
            while !stop.load(Ordering::Relaxed) && Instant::now() < until {
                thread::sleep(Duration::from_millis(50));
            }
        }
    }

    /// How the device's samples are laid out.
    struct Format {
        channels: usize,
        bits: u16,
        float: bool,
    }

    impl Format {
        /// # Safety
        /// `format` must point to a valid WAVEFORMATEX, extensible when its tag says so.
        unsafe fn read(format: *const WAVEFORMATEX) -> Self {
            // SAFETY: see above.
            let base = unsafe { *format };
            let float = match u32::from(base.wFormatTag) {
                WAVE_FORMAT_IEEE_FLOAT => true,
                WAVE_FORMAT_EXTENSIBLE => {
                    // SAFETY: the tag says the extensible layout follows.
                    let extensible = unsafe { std::ptr::read_unaligned(format.cast::<WAVEFORMATEXTENSIBLE>()) };
                    // The struct is packed; copy the field out before comparing it.
                    let sub_format = extensible.SubFormat;
                    sub_format == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT || base.wBitsPerSample == 32
                }
                _ => false,
            };
            Self { channels: usize::from(base.nChannels.max(1)), bits: base.wBitsPerSample, float }
        }

        fn sample(&self, bytes: &[u8]) -> f32 {
            match (self.float, self.bits) {
                (true, _) => f32::from_le_bytes(bytes[..4].try_into().unwrap_or_default()),
                (false, 16) => f32::from(i16::from_le_bytes([bytes[0], bytes[1]])) / 32768.0,
                (false, 24) => (i32::from_le_bytes([0, bytes[0], bytes[1], bytes[2]]) >> 8) as f32 / 8_388_608.0,
                (false, 32) => i32::from_le_bytes(bytes[..4].try_into().unwrap_or_default()) as f32 / 2_147_483_648.0,
                _ => 0.0,
            }
        }
    }

    /// Captures until told to stop (`Ok`) or until the device goes away (`Ok` as well, after it
    /// has started); `Err` when capture could not start.
    fn capture(stop: &AtomicBool, publish: &mut impl FnMut(AudioFrame)) -> windows::core::Result<()> {
        // SAFETY: COM calls on interfaces the system returned; the mix format is freed after use.
        unsafe {
            let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
            let device = enumerator.GetDefaultAudioEndpoint(eRender, eConsole)?;
            let client: IAudioClient = device.Activate(CLSCTX_ALL, None)?;
            let mix = client.GetMixFormat()?;
            let format = Format::read(mix);
            let sample_rate = (*mix).nSamplesPerSec;
            let initialised =
                client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200_000, 0, mix, None);
            CoTaskMemFree(Some(mix.cast()));
            initialised?;

            let reader: IAudioCaptureClient = client.GetService()?;
            client.Start()?;

            let mut analyzer = Analyzer::new(sample_rate);
            let frame_bytes = format.channels * usize::from(format.bits / 8);
            let mut last_frame = Instant::now() - INTERVAL;

            while !stop.load(Ordering::Relaxed) {
                thread::sleep(Duration::from_millis(10));
                loop {
                    let Ok(size) = reader.GetNextPacketSize() else {
                        let _ = client.Stop();
                        return Ok(());
                    };
                    if size == 0 {
                        break;
                    }

                    let mut data = std::ptr::null_mut();
                    let mut frames = 0u32;
                    let mut flags = 0u32;
                    if reader.GetBuffer(&mut data, &mut frames, &mut flags, None, None).is_err() {
                        let _ = client.Stop();
                        return Ok(());
                    }

                    let bytes = std::slice::from_raw_parts(data, frames as usize * frame_bytes);
                    for frame in bytes.chunks_exact(frame_bytes) {
                        let sum: f32 =
                            frame.chunks_exact(frame_bytes / format.channels).map(|s| format.sample(s)).sum();
                        analyzer.push(sum / format.channels as f32);
                    }
                    let _ = reader.ReleaseBuffer(frames);
                }

                if analyzer.is_ready() && last_frame.elapsed() >= INTERVAL {
                    last_frame = Instant::now();
                    publish(analyzer.frame());
                }
            }

            let _ = client.Stop();
            Ok(())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame_of(frequency: f64, amplitude: f64) -> AudioFrame {
        let sample_rate = 48_000;
        let mut analyzer = Analyzer::new(sample_rate);
        for i in 0..WINDOW_SIZE * 2 {
            analyzer.push((amplitude * (2.0 * PI * frequency * i as f64 / f64::from(sample_rate)).sin()) as f32);
        }
        assert!(analyzer.is_ready());
        analyzer.frame()
    }

    fn loudest_band(frame: &AudioFrame) -> usize {
        (0..BAND_COUNT).max_by(|&a, &b| frame.bands[a].total_cmp(&frame.bands[b])).unwrap()
    }

    #[test]
    fn silence_is_silent() {
        let mut analyzer = Analyzer::new(48_000);
        for _ in 0..WINDOW_SIZE {
            analyzer.push(0.0);
        }
        assert_eq!(analyzer.frame(), AudioFrame::SILENT);
    }

    #[test]
    fn a_low_tone_lights_the_bass_and_a_high_one_the_treble() {
        let low = frame_of(80.0, 0.5);
        let high = frame_of(8000.0, 0.5);
        assert!(loudest_band(&low) < 4, "{:?}", low.bands);
        assert!(loudest_band(&high) >= 11, "{:?}", high.bands);
        assert!(low.bass > low.treble);
        assert!(high.treble > high.bass);
    }

    #[test]
    fn louder_sound_reads_louder() {
        let quiet = frame_of(1000.0, 0.01);
        let loud = frame_of(1000.0, 0.8);
        assert!(loud.level > quiet.level);
        assert!(loud.level > 0.9);
    }

    #[test]
    fn bands_cover_the_spectrum_without_overlap() {
        let analyzer = Analyzer::new(44_100);
        for pair in analyzer.bin_ranges.windows(2) {
            assert!(pair[0].1 < pair[1].0, "{:?}", analyzer.bin_ranges);
        }
    }
}
