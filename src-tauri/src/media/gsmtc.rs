//! Reads and controls whatever the system considers the current media session (the same one
//! the Windows media flyout shows), regardless of which app owns it.

use std::sync::mpsc::{self, Sender};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use notch_core::media::{MediaAction, MediaSnapshot};
use windows::Foundation::TypedEventHandler;
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession as Session,
    GlobalSystemMediaTransportControlsSessionManager as Manager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus,
};
use windows::Storage::Streams::{DataReader, IRandomAccessStreamReference};
use windows::Win32::System::Com::{CoInitializeEx, COINIT_MULTITHREADED};

use super::{Player, Sink, MAX_ART_BYTES};

/// Seconds between 1601-01-01 (the Windows epoch) and 1970-01-01.
const WINDOWS_EPOCH_OFFSET_SECS: u64 = 11_644_473_600;

/// Ticks (100 ns) per second.
const TICKS_PER_SECOND: i64 = 10_000_000;

enum Message {
    /// The system picked another current session.
    SessionChanged,
    /// The track changed: read everything, artwork included.
    TrackChanged,
    /// Playback state or timeline changed: reuse the track's artwork.
    PlaybackChanged,
    Control(MediaAction),
    Seek(Duration),
}

struct Gsmtc {
    messages: Sender<Message>,
}

impl Player for Gsmtc {
    fn control(&self, action: MediaAction) {
        let _ = self.messages.send(Message::Control(action));
    }

    fn seek(&self, position: Duration) {
        let _ = self.messages.send(Message::Seek(position));
    }
}

/// Starts watching the system's media sessions on a thread of its own.
pub fn start(sink: Sink) -> Option<Box<dyn Player>> {
    let (messages, inbox) = mpsc::channel();
    let own = messages.clone();
    thread::Builder::new()
        .name("media-gsmtc".into())
        .spawn(move || {
            // SAFETY: initialises COM for this thread, which stays alive for the process.
            let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
            let manager = match Manager::RequestAsync().and_then(|operation| operation.get()) {
                Ok(manager) => manager,
                Err(e) => {
                    log::warn!("media sessions are unavailable: {e}");
                    return;
                }
            };
            let sender = own.clone();
            let handler = TypedEventHandler::new(move |_, _| {
                let _ = sender.send(Message::SessionChanged);
                Ok(())
            });
            if let Err(e) = manager.CurrentSessionChanged(&handler) {
                log::warn!("could not watch media sessions: {e}");
            }

            let mut worker = Worker { manager, own, session: None, tokens: None, track: None, sink };
            worker.attach();
            for message in inbox {
                worker.handle(message);
            }
        })
        .ok()?;
    Some(Box::new(Gsmtc { messages }))
}

/// What a track carries; the artwork makes reading it slow, so playback events reuse it.
#[derive(Clone)]
struct Track {
    title: String,
    artist: String,
    album: String,
    thumbnail: Option<Vec<u8>>,
}

/// The event registrations on one session.
struct Tokens {
    properties: i64,
    playback: i64,
    timeline: i64,
}

struct Worker {
    manager: Manager,
    own: Sender<Message>,
    session: Option<Session>,
    tokens: Option<Tokens>,
    track: Option<Track>,
    sink: Sink,
}

impl Worker {
    fn handle(&mut self, message: Message) {
        match message {
            Message::SessionChanged => self.attach(),
            Message::TrackChanged => self.refresh(true),
            Message::PlaybackChanged => self.refresh(false),
            Message::Control(action) => self.control(action),
            Message::Seek(position) => self.seek(position),
        }
    }

    fn attach(&mut self) {
        self.detach();
        self.session = self.manager.GetCurrentSession().ok();
        if let Some(session) = &self.session {
            self.tokens = self.watch(session);
        }
        self.refresh(true);
    }

    fn watch(&self, session: &Session) -> Option<Tokens> {
        let sender = self.own.clone();
        let properties = session
            .MediaPropertiesChanged(&TypedEventHandler::new(move |_, _| {
                let _ = sender.send(Message::TrackChanged);
                Ok(())
            }))
            .ok()?;
        let sender = self.own.clone();
        let playback = session
            .PlaybackInfoChanged(&TypedEventHandler::new(move |_, _| {
                let _ = sender.send(Message::PlaybackChanged);
                Ok(())
            }))
            .ok()?;
        let sender = self.own.clone();
        let timeline = session
            .TimelinePropertiesChanged(&TypedEventHandler::new(move |_, _| {
                let _ = sender.send(Message::PlaybackChanged);
                Ok(())
            }))
            .ok()?;
        Some(Tokens { properties, playback, timeline })
    }

    fn detach(&mut self) {
        if let (Some(session), Some(tokens)) = (self.session.take(), self.tokens.take()) {
            let _ = session.RemoveMediaPropertiesChanged(tokens.properties);
            let _ = session.RemovePlaybackInfoChanged(tokens.playback);
            let _ = session.RemoveTimelinePropertiesChanged(tokens.timeline);
        }
        self.session = None;
        self.track = None;
    }

    fn refresh(&mut self, reload_track: bool) {
        let snapshot = self.session.clone().and_then(|session| match self.read(&session, reload_track) {
            Ok(snapshot) => Some(snapshot),
            Err(e) => {
                // Sessions disappear mid-read when their app closes.
                log::debug!("could not read the media session: {e}");
                None
            }
        });
        (self.sink)(snapshot);
    }

    fn read(&mut self, session: &Session, reload_track: bool) -> windows::core::Result<MediaSnapshot> {
        // Track info is the slow part (it carries the artwork), so timeline and playback
        // events reuse the last copy.
        let track = match self.track.clone().filter(|_| !reload_track) {
            Some(track) => track,
            None => {
                let properties = session.TryGetMediaPropertiesAsync()?.get()?;
                let track = Track {
                    title: properties.Title()?.to_string(),
                    artist: properties.Artist()?.to_string(),
                    album: properties.AlbumTitle()?.to_string(),
                    thumbnail: properties.Thumbnail().ok().and_then(|reference| read_thumbnail(&reference)),
                };
                self.track = Some(track.clone());
                track
            }
        };

        let playback = session.GetPlaybackInfo()?;
        let controls = playback.Controls()?;
        let timeline = session.GetTimelineProperties()?;

        let start = ticks(timeline.StartTime()?.Duration);
        let position = ticks(timeline.Position()?.Duration).saturating_sub(start);
        let duration = ticks(timeline.EndTime()?.Duration).saturating_sub(start);

        // Sources without a timeline leave LastUpdatedTime at its zero value.
        let updated_at = windows_time(timeline.LastUpdatedTime()?.UniversalTime)
            .filter(|time| *time > UNIX_EPOCH + Duration::from_secs(946_684_800))
            .unwrap_or_else(SystemTime::now);

        let flag = |value: windows::core::Result<bool>| value.unwrap_or(false);
        Ok(MediaSnapshot {
            source_app_id: session.SourceAppUserModelId()?.to_string(),
            title: track.title,
            artist: track.artist,
            album: track.album,
            thumbnail: track.thumbnail,
            is_playing: playback.PlaybackStatus()? == PlaybackStatus::Playing,
            position,
            position_updated_at: updated_at,
            duration,
            playback_rate: playback.PlaybackRate().ok().and_then(|rate| rate.Value().ok()).unwrap_or(1.0),
            can_toggle_play_pause: flag(controls.IsPlayPauseToggleEnabled())
                || flag(controls.IsPlayEnabled())
                || flag(controls.IsPauseEnabled()),
            can_go_next: flag(controls.IsNextEnabled()),
            can_go_previous: flag(controls.IsPreviousEnabled()),
            can_seek: flag(controls.IsPlaybackPositionEnabled()),
        })
    }

    fn control(&self, action: MediaAction) {
        let Some(session) = &self.session else { return };
        let result = match action {
            MediaAction::PlayPause => session.TryTogglePlayPauseAsync(),
            MediaAction::Next => session.TrySkipNextAsync(),
            MediaAction::Previous => session.TrySkipPreviousAsync(),
        };
        // The owning app can exit between the click and the call.
        if let Err(e) = result.and_then(|operation| operation.get()) {
            log::debug!("media command failed: {e}");
        }
    }

    fn seek(&self, position: Duration) {
        let Some(session) = &self.session else { return };
        let target = i64::try_from(position.as_nanos() / 100).unwrap_or(i64::MAX);
        if let Err(e) = session.TryChangePlaybackPositionAsync(target).and_then(|operation| operation.get()) {
            log::debug!("media seek failed: {e}");
        }
    }
}

/// A Windows `TimeSpan` (100 ns ticks) as a duration; negative values become zero.
fn ticks(value: i64) -> Duration {
    let value = value.max(0);
    Duration::new((value / TICKS_PER_SECOND) as u64, ((value % TICKS_PER_SECOND) * 100) as u32)
}

/// A Windows `DateTime` (100 ns ticks since 1601) as a system time.
fn windows_time(universal_time: i64) -> Option<SystemTime> {
    let since_1601 = ticks(universal_time);
    since_1601
        .checked_sub(Duration::from_secs(WINDOWS_EPOCH_OFFSET_SECS))
        .map(|since_epoch| UNIX_EPOCH + since_epoch)
}

fn read_thumbnail(reference: &IRandomAccessStreamReference) -> Option<Vec<u8>> {
    let stream = reference.OpenReadAsync().ok()?.get().ok()?;
    let size = stream.Size().ok()?;
    if size == 0 || size > MAX_ART_BYTES as u64 {
        return None;
    }

    let reader = DataReader::CreateDataReader(&stream).ok()?;
    reader.LoadAsync(u32::try_from(size).ok()?).ok()?.get().ok()?;
    let mut bytes = vec![0_u8; usize::try_from(size).ok()?];
    reader.ReadBytes(&mut bytes).ok()?;
    Some(bytes)
}
