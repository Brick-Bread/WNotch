//! What is playing: the system's media session, its card on the Home tab and its activity in
//! the pill, plus the loudness that drives the audio glow.
//!
//! Commands: `get_media`, `media_control({ action })`, `media_seek({ seconds })`.
//! Events: `media` (the state for the Home card, `null` when no app has a session) and
//! `media-level` (the smoothed output loudness, 0..1, while playing, when the system has a meter).

mod art;
#[cfg(windows)]
mod gsmtc;
mod meter;
#[cfg(target_os = "linux")]
mod mpris;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use notch_core::media::{media_activity, MediaAction, MediaSnapshot, MediaState, ACTIVITY_ID};
use serde::Serialize;
use tauri::{AppHandle, Emitter, State};

use crate::activity_hub::ActivityHub;

/// The largest artwork a player may hand over.
const MAX_ART_BYTES: usize = 8 * 1024 * 1024;

/// Receives the current snapshot from a platform backend whenever it changes.
type Sink = Arc<dyn Fn(Option<MediaSnapshot>) + Send + Sync>;

/// Sends commands to the system's current media session.
trait Player: Send + Sync {
    fn control(&self, action: MediaAction);
    fn seek(&self, position: Duration);
}

/// Artwork already processed for a given source image.
struct CachedArt {
    source: Vec<u8>,
    art: Option<art::Art>,
}

#[derive(Default)]
struct Current {
    snapshot: Option<MediaSnapshot>,
    art: Option<CachedArt>,
}

/// Owns the platform backend and keeps the UI and the pill up to date with it.
pub struct MediaService {
    current: Mutex<Current>,
    player: Mutex<Option<Box<dyn Player>>>,
}

#[derive(Serialize, Clone, Copy)]
struct Level {
    level: f64,
}

impl MediaService {
    /// Connects to the system's media sessions and starts reporting. Without a media session
    /// service on this system the card and the activity simply never show.
    pub fn start(app: AppHandle, hub: Arc<ActivityHub>) -> Arc<Self> {
        let service = Arc::new(Self { current: Mutex::new(Current::default()), player: Mutex::new(None) });
        let playing = Arc::new(AtomicBool::new(false));

        let level_app = app.clone();
        meter::spawn(playing.clone(), move |level| {
            if let Err(e) = level_app.emit("media-level", Level { level }) {
                log::warn!("could not emit media-level: {e}");
            }
        });

        let sink: Sink = {
            let service = service.clone();
            Arc::new(move |snapshot| service.update(&app, &hub, &playing, snapshot))
        };
        match start_platform(sink) {
            Some(player) => *service.player() = Some(player),
            None => log::info!("no media session service on this system; media is off"),
        }
        service
    }

    fn lock(&self) -> MutexGuard<'_, Current> {
        self.current.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn player(&self) -> MutexGuard<'_, Option<Box<dyn Player>>> {
        self.player.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn update(&self, app: &AppHandle, hub: &ActivityHub, playing: &AtomicBool, snapshot: Option<MediaSnapshot>) {
        let (state, accent, snapshot) = {
            let mut current = self.lock();
            let snapshot = snapshot.map(|media| self.with_processed_art(&mut current, media));
            let accent = current.art.as_ref().and_then(|cached| cached.art.as_ref()).and_then(|art| art.accent);
            current.snapshot = snapshot.clone();
            (snapshot.as_ref().map(MediaState::from), accent, snapshot)
        };

        playing.store(snapshot.as_ref().is_some_and(|media| media.is_playing), Ordering::Relaxed);
        if let Err(e) = app.emit("media", &state) {
            log::warn!("could not emit media: {e}");
        }
        match media_activity(snapshot.as_ref(), accent) {
            Some(activity) => hub.publish(activity),
            None => hub.remove(ACTIVITY_ID),
        }
    }

    /// Swaps the source's artwork for the shrunken copy, decoding only when the track's image changed.
    fn with_processed_art(&self, current: &mut Current, mut media: MediaSnapshot) -> MediaSnapshot {
        let source = media.thumbnail.take().filter(|bytes| bytes.len() <= MAX_ART_BYTES);
        let Some(source) = source else {
            current.art = None;
            return media;
        };

        if current.art.as_ref().is_none_or(|cached| cached.source != source) {
            let art = art::process(&source);
            current.art = Some(CachedArt { source, art });
        }
        media.thumbnail = current
            .art
            .as_ref()
            .and_then(|cached| cached.art.as_ref())
            .map(|art| art.jpeg.clone());
        media
    }

    fn state(&self) -> Option<MediaState> {
        self.lock().snapshot.as_ref().map(MediaState::from)
    }

    fn control(&self, action: MediaAction) {
        if let Some(player) = self.player().as_ref() {
            player.control(action);
        }
    }

    fn seek(&self, seconds: f64) {
        let can_seek = self.lock().snapshot.as_ref().is_some_and(|media| media.can_seek && media.has_timeline());
        if !can_seek || !seconds.is_finite() {
            return;
        }
        if let Some(player) = self.player().as_ref() {
            player.seek(Duration::from_secs_f64(seconds.max(0.0)));
        }
    }
}

fn start_platform(sink: Sink) -> Option<Box<dyn Player>> {
    #[cfg(windows)]
    {
        gsmtc::start(sink)
    }
    #[cfg(target_os = "linux")]
    {
        mpris::start(sink)
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = sink;
        None
    }
}

/// The current track, for the Home card when the UI starts.
#[tauri::command]
pub fn get_media(media: State<'_, Arc<MediaService>>) -> Option<MediaState> {
    media.state()
}

/// Previous, play/pause or next on the current session.
#[tauri::command]
pub fn media_control(media: State<'_, Arc<MediaService>>, action: MediaAction) {
    media.control(action);
}

/// Jumps to this many seconds into the current track.
#[tauri::command]
pub fn media_seek(media: State<'_, Arc<MediaService>>, seconds: f64) {
    media.seek(seconds);
}
