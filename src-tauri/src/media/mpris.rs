//! Reads and controls the media players on the session bus through MPRIS
//! (`org.mpris.MediaPlayer2.*`): the same players a desktop's media applet shows.
//!
//! The players are read by polling, which is robust against the many quirks of MPRIS
//! implementations (position is never signalled, players come and go); a command is followed
//! by an immediate read so the card answers a click at once.

use std::collections::HashMap;
use std::io::Read;
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::thread;
use std::time::{Duration, SystemTime};

use notch_core::media::{MediaAction, MediaSnapshot};
use zbus::blocking::fdo::DBusProxy;
use zbus::blocking::{Connection, Proxy};
use zbus::zvariant::{ObjectPath, OwnedValue, Value};

use super::{Player, Sink, MAX_ART_BYTES};

const BUS_PREFIX: &str = "org.mpris.MediaPlayer2.";
const OBJECT_PATH: &str = "/org/mpris/MediaPlayer2";
const PLAYER: &str = "org.mpris.MediaPlayer2.Player";

/// How often the players are read when nobody sends a command.
const POLL: Duration = Duration::from_millis(750);

/// How long to wait for artwork served over the network.
const ART_TIMEOUT: Duration = Duration::from_secs(3);

enum Message {
    Control(MediaAction),
    Seek(Duration),
}

struct Mpris {
    messages: Sender<Message>,
}

impl Player for Mpris {
    fn control(&self, action: MediaAction) {
        let _ = self.messages.send(Message::Control(action));
    }

    fn seek(&self, position: Duration) {
        let _ = self.messages.send(Message::Seek(position));
    }
}

/// Connects to the session bus and starts watching the players; `None` without a bus.
pub fn start(sink: Sink) -> Option<Box<dyn Player>> {
    let connection = match Connection::session() {
        Ok(connection) => connection,
        Err(e) => {
            log::info!("no session bus, so no media players: {e}");
            return None;
        }
    };

    let (messages, inbox) = mpsc::channel();
    thread::Builder::new()
        .name("media-mpris".into())
        .spawn(move || {
            let mut worker = Worker { connection, selected: None, last: None, art: None, sink };
            loop {
                match inbox.recv_timeout(POLL) {
                    Ok(Message::Control(action)) => worker.control(action),
                    Ok(Message::Seek(position)) => worker.seek(position),
                    Err(RecvTimeoutError::Timeout) => {}
                    Err(RecvTimeoutError::Disconnected) => return,
                }
                worker.refresh();
            }
        })
        .ok()?;
    Some(Box::new(Mpris { messages }))
}

/// The properties of one player that matter here.
struct Reading {
    bus_name: String,
    properties: HashMap<String, OwnedValue>,
}

impl Reading {
    fn status(&self) -> &str {
        match self.properties.get("PlaybackStatus").map(|v| &**v) {
            Some(Value::Str(status)) => status.as_str(),
            _ => "Stopped",
        }
    }

    fn flag(&self, name: &str) -> bool {
        matches!(self.properties.get(name).map(|v| &**v), Some(Value::Bool(true)))
    }

    fn metadata(&self) -> HashMap<String, OwnedValue> {
        self.properties
            .get("Metadata")
            .and_then(|value| HashMap::<String, OwnedValue>::try_from(value.try_clone().ok()?).ok())
            .unwrap_or_default()
    }
}

struct Worker {
    connection: Connection,
    /// The bus name of the player the card follows.
    selected: Option<String>,
    last: Option<MediaSnapshot>,
    /// The artwork last fetched, by URL.
    art: Option<(String, Option<Vec<u8>>)>,
    sink: Sink,
}

impl Worker {
    fn proxy(&self, bus_name: &str, interface: &'static str) -> zbus::Result<Proxy<'static>> {
        Proxy::new(&self.connection, bus_name.to_owned(), OBJECT_PATH, interface)
    }

    fn players(&self) -> Vec<String> {
        let names = DBusProxy::new(&self.connection).ok().and_then(|bus| bus.list_names().ok()).unwrap_or_default();
        let mut players: Vec<String> = names.into_iter().map(|name| name.to_string()).collect();
        // playerctld mirrors another player; following it would show everything twice.
        players.retain(|name| name.starts_with(BUS_PREFIX) && !name.ends_with(".playerctld"));
        players.sort();
        players
    }

    fn read(&self, bus_name: &str) -> Option<Reading> {
        let properties = self
            .proxy(bus_name, "org.freedesktop.DBus.Properties")
            .and_then(|proxy| proxy.call::<_, _, HashMap<String, OwnedValue>>("GetAll", &(PLAYER,)))
            .ok()?;
        Some(Reading { bus_name: bus_name.to_owned(), properties })
    }

    /// The player to show: one that is playing, else the one shown before, else any with a track.
    fn choose(&mut self) -> Option<Reading> {
        let readings: Vec<Reading> = self.players().iter().filter_map(|name| self.read(name)).collect();
        let position = readings
            .iter()
            .position(|r| r.status() == "Playing")
            .or_else(|| readings.iter().position(|r| Some(&r.bus_name) == self.selected.as_ref() && r.status() != "Stopped"))
            .or_else(|| readings.iter().position(|r| r.status() != "Stopped"))?;
        let reading = readings.into_iter().nth(position)?;
        self.selected = Some(reading.bus_name.clone());
        Some(reading)
    }

    fn refresh(&mut self) {
        let snapshot = self.choose().map(|reading| self.snapshot(&reading));
        let changed = match (&snapshot, &self.last) {
            (Some(new), Some(old)) => new.differs_from(old, SystemTime::now()),
            (None, None) => false,
            _ => true,
        };
        if changed {
            self.last = snapshot.clone();
            (self.sink)(snapshot);
        }
    }

    fn snapshot(&mut self, reading: &Reading) -> MediaSnapshot {
        let metadata = reading.metadata();
        let text = |key: &str| match metadata.get(key).map(|v| &**v) {
            Some(Value::Str(text)) => text.to_string(),
            _ => String::new(),
        };
        let artist = match metadata.get("xesam:artist").map(|v| &**v) {
            Some(Value::Array(names)) => names
                .iter()
                .filter_map(|name| if let Value::Str(name) = name { Some(name.to_string()) } else { None })
                .collect::<Vec<_>>()
                .join(", "),
            Some(Value::Str(name)) => name.to_string(),
            _ => String::new(),
        };
        let micros = |value: Option<&OwnedValue>| match value.map(|v| &**v) {
            Some(Value::I64(n)) => u64::try_from(*n).unwrap_or(0),
            Some(Value::U64(n)) => *n,
            _ => 0,
        };

        let can_control = reading.flag("CanControl");
        MediaSnapshot {
            source_app_id: reading.bus_name.trim_start_matches(BUS_PREFIX).to_owned(),
            title: text("xesam:title"),
            artist,
            album: text("xesam:album"),
            is_playing: reading.status() == "Playing",
            position: Duration::from_micros(micros(reading.properties.get("Position"))),
            position_updated_at: SystemTime::now(),
            duration: Duration::from_micros(micros(metadata.get("mpris:length"))),
            playback_rate: match reading.properties.get("Rate").map(|v| &**v) {
                Some(Value::F64(rate)) if *rate > 0.0 => *rate,
                _ => 1.0,
            },
            thumbnail: self.artwork(&text("mpris:artUrl")),
            can_toggle_play_pause: can_control && (reading.flag("CanPlay") || reading.flag("CanPause")),
            can_go_next: can_control && reading.flag("CanGoNext"),
            can_go_previous: can_control && reading.flag("CanGoPrevious"),
            can_seek: reading.flag("CanSeek"),
        }
    }

    /// The artwork behind an `mpris:artUrl` (a file or a web address), fetched once per URL.
    fn artwork(&mut self, url: &str) -> Option<Vec<u8>> {
        if url.is_empty() {
            return None;
        }
        if let Some((cached, bytes)) = &self.art {
            if cached == url {
                return bytes.clone();
            }
        }
        let bytes = fetch_art(url);
        self.art = Some((url.to_owned(), bytes.clone()));
        bytes
    }

    fn control(&self, action: MediaAction) {
        let Some(bus_name) = &self.selected else { return };
        let method = match action {
            MediaAction::PlayPause => "PlayPause",
            MediaAction::Next => "Next",
            MediaAction::Previous => "Previous",
        };
        // The player can exit between the click and the call.
        if let Err(e) = self.proxy(bus_name, PLAYER).and_then(|proxy| proxy.call_method(method, &())) {
            log::debug!("media command failed: {e}");
        }
    }

    fn seek(&self, position: Duration) {
        let Some(bus_name) = &self.selected else { return };
        let micros = i64::try_from(position.as_micros()).unwrap_or(i64::MAX);
        let track = self.read(bus_name).and_then(|reading| match reading.metadata().get("mpris:trackid").map(|v| &**v) {
            Some(Value::ObjectPath(path)) => Some(path.to_owned()),
            Some(Value::Str(text)) => ObjectPath::try_from(text.as_str()).ok().map(|path| path.into_owned()),
            _ => None,
        });
        let result = self.proxy(bus_name, PLAYER).and_then(|proxy| match &track {
            Some(track) => proxy.call_method("SetPosition", &(track, micros)),
            None => {
                // Players that give no track id can only seek relative to where they are.
                let current = self.last.as_ref().map_or(0, |media| {
                    i64::try_from(media.position_at(SystemTime::now()).as_micros()).unwrap_or(0)
                });
                proxy.call_method("Seek", &(micros - current,))
            }
        });
        if let Err(e) = result {
            log::debug!("media seek failed: {e}");
        }
    }
}

fn fetch_art(url: &str) -> Option<Vec<u8>> {
    let limit = MAX_ART_BYTES as u64 + 1;
    let mut bytes = Vec::new();
    if let Some(path) = url.strip_prefix("file://") {
        std::fs::File::open(percent_decode(path)).ok()?.take(limit).read_to_end(&mut bytes).ok()?;
    } else if url.starts_with("http://") || url.starts_with("https://") {
        let response = ureq::AgentBuilder::new().timeout(ART_TIMEOUT).build().get(url).call().ok()?;
        response.into_reader().take(limit).read_to_end(&mut bytes).ok()?;
    } else {
        return None;
    }
    (!bytes.is_empty() && bytes.len() <= MAX_ART_BYTES).then_some(bytes)
}

/// Decodes `%XX` escapes in a file URL's path.
fn percent_decode(path: &str) -> String {
    let bytes = path.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        let escaped = (bytes[i] == b'%' && i + 2 < bytes.len())
            .then(|| std::str::from_utf8(&bytes[i + 1..i + 3]).ok().and_then(|hex| u8::from_str_radix(hex, 16).ok()))
            .flatten();
        match escaped {
            Some(byte) => {
                decoded.push(byte);
                i += 3;
            }
            None => {
                decoded.push(bytes[i]);
                i += 1;
            }
        }
    }
    String::from_utf8_lossy(&decoded).into_owned()
}

#[cfg(test)]
mod tests {
    use super::percent_decode;

    #[test]
    fn file_urls_are_decoded() {
        assert_eq!(percent_decode("/home/a%20b/c%C3%A9.jpg"), "/home/a b/cé.jpg");
        assert_eq!(percent_decode("/100%/x"), "/100%/x");
        assert_eq!(percent_decode("/end%2"), "/end%2");
    }
}
