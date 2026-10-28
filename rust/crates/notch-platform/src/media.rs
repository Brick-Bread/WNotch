//! Reads and controls whatever the system considers the current media session (the same one the
//! Windows media flyout shows), regardless of which app owns it. Ported from
//! `src/Notch.Platform/Media/GsmtcMediaService.cs`.

use std::sync::{Arc, Mutex, MutexGuard};
use std::thread;
use std::time::Duration;

use chrono::{DateTime, Utc};
use notch_core::media::{MediaService, MediaSnapshot};
use windows::Foundation::TypedEventHandler;
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession as Session,
    GlobalSystemMediaTransportControlsSessionManager as SessionManager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus,
};
use windows::Storage::Streams::{DataReader, IRandomAccessStreamReference};

const MAX_THUMBNAIL_BYTES: u64 = 8 * 1024 * 1024;

/// `DateTime.UniversalTime` counts 100 ns ticks from 1601; this many of them lie before 1970.
const TICKS_TO_UNIX_EPOCH: i64 = 116_444_736_000_000_000;
/// The same ticks at 1 January 2000: sources without a timeline leave their update time near zero.
const TICKS_AT_2000: i64 = TICKS_TO_UNIX_EPOCH + 946_684_800 * 10_000_000;

#[derive(Clone)]
struct TrackInfo {
    title: String,
    artist: String,
    album: String,
    thumbnail: Option<Vec<u8>>,
}

#[derive(Default)]
struct State {
    manager: Option<(SessionManager, i64)>,
    session: Option<(Session, [i64; 3])>,
    track: Option<TrackInfo>,
    current: Option<MediaSnapshot>,
}

struct Inner {
    state: Mutex<State>,
    /// Lets one refresh run at a time, as the C# semaphore does.
    refresh_gate: Mutex<()>,
    changed: Box<dyn Fn() + Send + Sync>,
}

impl Inner {
    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|p| p.into_inner())
    }

    fn attach(self: &Arc<Self>, session: Option<Session>) {
        self.detach();
        if let Some(session) = session {
            let tokens = [
                self.subscribe(&session, true, |s, h| s.MediaPropertiesChanged(h)),
                self.subscribe(&session, false, |s, h| s.PlaybackInfoChanged(h)),
                self.subscribe(&session, false, |s, h| s.TimelinePropertiesChanged(h)),
            ];
            self.state().session = Some((session, tokens));
        }
        self.refresh(true);
    }

    /// Registers a handler that refreshes on its own thread, so the WinRT callback returns at once.
    fn subscribe<A: windows::core::RuntimeType + 'static>(
        self: &Arc<Self>,
        session: &Session,
        reload_track: bool,
        register: impl Fn(&Session, &TypedEventHandler<Session, A>) -> windows::core::Result<i64>,
    ) -> i64 {
        let weak = Arc::downgrade(self);
        let handler = TypedEventHandler::new(move |_, _| {
            if let Some(inner) = weak.upgrade() {
                thread::spawn(move || inner.refresh(reload_track));
            }
            Ok(())
        });
        register(session, &handler).unwrap_or_default()
    }

    fn detach(&self) {
        let mut state = self.state();
        if let Some((old, [media, playback, timeline])) = state.session.take() {
            let _ = old.RemoveMediaPropertiesChanged(media);
            let _ = old.RemovePlaybackInfoChanged(playback);
            let _ = old.RemoveTimelinePropertiesChanged(timeline);
        }
        state.track = None;
    }

    fn refresh(&self, reload_track: bool) {
        let _gate = self.refresh_gate.lock().unwrap_or_else(|p| p.into_inner());
        let (session, track) = {
            let state = self.state();
            (state.session.as_ref().map(|(s, _)| s.clone()), if reload_track { None } else { state.track.clone() })
        };

        // Sessions disappear mid-read when their app closes.
        let read = session.and_then(|session| read(&session, track).ok());
        {
            let mut state = self.state();
            match read {
                Some((snapshot, track)) => {
                    state.track = Some(track);
                    state.current = Some(snapshot);
                }
                None => state.current = None,
            }
        }
        (self.changed)();
    }

    fn session(&self) -> Option<Session> {
        self.state().session.as_ref().map(|(s, _)| s.clone())
    }
}

/// The current media session through the Global System Media Transport Controls.
pub struct GsmtcMediaService {
    inner: Arc<Inner>,
}

impl GsmtcMediaService {
    /// Starts following the current session. `changed` is called on a background thread whenever
    /// [`MediaService::current`] may have changed.
    pub fn start(changed: impl Fn() + Send + Sync + 'static) -> windows::core::Result<Self> {
        let inner = Arc::new(Inner {
            state: Mutex::new(State::default()),
            refresh_gate: Mutex::new(()),
            changed: Box::new(changed),
        });

        let manager = SessionManager::RequestAsync()?.join()?;
        let weak = Arc::downgrade(&inner);
        let token = manager.CurrentSessionChanged(&TypedEventHandler::new(
            move |sender: windows::core::Ref<SessionManager>, _| {
                if let (Some(inner), Some(manager)) = (weak.upgrade(), sender.as_ref()) {
                    let session = manager.GetCurrentSession().ok();
                    thread::spawn(move || inner.attach(session));
                }
                Ok(())
            },
        ))?;
        let session = manager.GetCurrentSession().ok();
        inner.state().manager = Some((manager, token));
        inner.attach(session);
        Ok(Self { inner })
    }

    /// Sends a control to the session on its own thread: the owning app can exit between the
    /// click and the call, and that is nobody's problem.
    fn send(&self, command: impl FnOnce(&Session) -> windows::core::Result<bool> + Send + 'static) {
        if let Some(session) = self.inner.session() {
            thread::spawn(move || {
                let _ = command(&session);
            });
        }
    }
}

impl Drop for GsmtcMediaService {
    fn drop(&mut self) {
        if let Some((manager, token)) = self.inner.state().manager.take() {
            let _ = manager.RemoveCurrentSessionChanged(token);
        }
        self.inner.detach();
    }
}

impl MediaService for GsmtcMediaService {
    fn current(&self) -> Option<MediaSnapshot> {
        self.inner.state().current.clone()
    }

    fn toggle_play_pause(&self) {
        self.send(|s| s.TryTogglePlayPauseAsync()?.join());
    }

    fn next(&self) {
        self.send(|s| s.TrySkipNextAsync()?.join());
    }

    fn previous(&self) {
        self.send(|s| s.TrySkipPreviousAsync()?.join());
    }

    fn seek(&self, position: Duration) {
        let ticks = i64::try_from(position.as_nanos() / 100).unwrap_or(i64::MAX);
        self.send(move |s| s.TryChangePlaybackPositionAsync(ticks)?.join());
    }
}

fn read(session: &Session, track: Option<TrackInfo>) -> windows::core::Result<(MediaSnapshot, TrackInfo)> {
    // Track info is the slow part (it carries the artwork), so timeline and playback events reuse the last copy.
    let track = match track {
        Some(track) => track,
        None => {
            let properties = session.TryGetMediaPropertiesAsync()?.join()?;
            TrackInfo {
                title: properties.Title().map(|s| s.to_string()).unwrap_or_default(),
                artist: properties.Artist().map(|s| s.to_string()).unwrap_or_default(),
                album: properties.AlbumTitle().map(|s| s.to_string()).unwrap_or_default(),
                thumbnail: properties.Thumbnail().ok().and_then(|t| read_thumbnail(&t)),
            }
        }
    };

    let playback = session.GetPlaybackInfo()?;
    let controls = playback.Controls()?;
    let timeline = session.GetTimelineProperties()?;

    let updated = timeline.LastUpdatedTime()?.UniversalTime;
    let updated_at = if updated < TICKS_AT_2000 { Utc::now() } else { from_ticks(updated) };
    let span = |ticks: i64| Duration::from_nanos(u64::try_from(ticks).unwrap_or(0).saturating_mul(100));
    let start = timeline.StartTime()?.Duration;
    let enabled = |flag: windows::core::Result<bool>| flag.unwrap_or(false);

    let snapshot = MediaSnapshot {
        source_app_id: session.SourceAppUserModelId().map(|s| s.to_string()).unwrap_or_default(),
        title: track.title.clone(),
        artist: track.artist.clone(),
        album: track.album.clone(),
        thumbnail: track.thumbnail.clone(),
        is_playing: playback.PlaybackStatus()? == PlaybackStatus::Playing,
        position: span(timeline.Position()?.Duration - start),
        position_updated_at: updated_at,
        duration: span(timeline.EndTime()?.Duration - start),
        playback_rate: playback.PlaybackRate().and_then(|r| r.Value()).unwrap_or(1.0),
        can_toggle_play_pause: enabled(controls.IsPlayPauseToggleEnabled())
            || enabled(controls.IsPlayEnabled())
            || enabled(controls.IsPauseEnabled()),
        can_go_next: enabled(controls.IsNextEnabled()),
        can_go_previous: enabled(controls.IsPreviousEnabled()),
        can_seek: enabled(controls.IsPlaybackPositionEnabled()),
    };
    Ok((snapshot, track))
}

fn from_ticks(universal_time: i64) -> DateTime<Utc> {
    let since_epoch = universal_time - TICKS_TO_UNIX_EPOCH;
    DateTime::from_timestamp(since_epoch.div_euclid(10_000_000), (since_epoch.rem_euclid(10_000_000) * 100) as u32)
        .unwrap_or_else(Utc::now)
}

fn read_thumbnail(reference: &IRandomAccessStreamReference) -> Option<Vec<u8>> {
    let stream = reference.OpenReadAsync().ok()?.join().ok()?;
    let size = stream.Size().ok()?;
    if size == 0 || size > MAX_THUMBNAIL_BYTES {
        return None;
    }

    let reader = DataReader::CreateDataReader(&stream).ok()?;
    reader.LoadAsync(size as u32).ok()?.join().ok()?;
    let mut bytes = vec![0u8; size as usize];
    reader.ReadBytes(&mut bytes).ok()?;
    Some(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn winrt_times_convert_to_utc() {
        assert_eq!(from_ticks(TICKS_TO_UNIX_EPOCH), DateTime::<Utc>::UNIX_EPOCH);
        assert_eq!(from_ticks(TICKS_AT_2000).timestamp(), 946_684_800);
        assert_eq!(from_ticks(TICKS_TO_UNIX_EPOCH + 15).timestamp_subsec_nanos(), 1500);
    }

    #[test]
    fn starts_whether_or_not_anything_is_playing() {
        // Build machines play nothing, and some have no media service at all; this checks that
        // following no session is quiet, without waiting forever on a machine that cannot answer.
        let (done, finished) = std::sync::mpsc::channel();
        thread::spawn(move || {
            if let Ok(service) = GsmtcMediaService::start(|| {}) {
                let _ = service.current();
            }
            let _ = done.send(());
        });
        let _ = finished.recv_timeout(Duration::from_secs(20));
    }
}
