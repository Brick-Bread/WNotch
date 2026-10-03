//! The shelf: the commands the Shelf tab calls, the saved list, and what the platform adds
//! (pictures of files, dragging a tile out to other apps).

use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use notch_core::shelf::{self, FileShelf, ShelfStore};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, State, WebviewWindow};

use crate::state::Backend;

#[cfg(unix)]
#[path = "shelf/unix.rs"]
mod platform;
#[cfg(windows)]
#[path = "shelf/win.rs"]
mod platform;

/// A moved file can still be on its way when the drop returns; look again after this long.
const MOVE_SETTLE: Duration = Duration::from_millis(1500);

/// A tile as the UI draws it.
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ShelfItem {
    path: String,
    name: String,
    /// Whether to show the folder glyph until the picture arrives.
    is_folder: bool,
}

/// What the receiver did with a file dragged out of the shelf.
#[derive(Serialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(rename_all = "lowercase")]
pub enum DragEffect {
    None,
    Copy,
    Move,
    Link,
}

/// The shelf and its file on disk. Registered once with Tauri's state.
pub struct ShelfService {
    app: AppHandle,
    shelf: FileShelf,
    store: ShelfStore,
}

impl ShelfService {
    /// Loads the saved shelf and starts a look at what has gone from disk since.
    pub fn setup(app: &AppHandle) {
        let store = ShelfStore::new(ShelfStore::default_path());
        let shelf = FileShelf::new(store.load());
        app.manage(Arc::new(Self { app: app.clone(), shelf, store }));
        app.state::<Arc<Self>>().inner().clone().prune_in_background();
    }

    fn items(&self) -> Vec<ShelfItem> {
        self.shelf
            .snapshot()
            .into_iter()
            .map(|path| ShelfItem {
                name: shelf::display_name(&path).to_owned(),
                is_folder: shelf::looks_like_folder(&path),
                path,
            })
            .collect()
    }

    /// Saves and announces the list after a change.
    fn changed(&self) {
        if let Err(e) = self.store.save(&self.shelf.snapshot()) {
            log::warn!("could not save the shelf: {e}");
        }
        if let Err(e) = self.app.emit("shelf", self.items()) {
            log::warn!("could not emit shelf: {e}");
        }
    }

    /// Takes files that are gone off the shelf, off the calling thread: a path on a network share
    /// that is away can take seconds to answer.
    fn prune_in_background(self: Arc<Self>) {
        std::thread::spawn(move || {
            if self.shelf.prune(|path| Path::new(path).exists()) {
                self.changed();
            }
        });
    }
}

/// The shelf, newest first. A look at what has gone from disk follows and, when something has,
/// the `shelf` event.
#[tauri::command]
pub fn shelf_items(service: State<'_, Arc<ShelfService>>) -> Vec<ShelfItem> {
    service.inner().clone().prune_in_background();
    service.items()
}

/// Puts dropped paths on the shelf. `announce` is set when the notch is closed, so the drop is
/// confirmed in the pill.
#[tauri::command]
pub fn shelf_add(
    service: State<'_, Arc<ShelfService>>,
    backend: State<'_, Backend>,
    paths: Vec<String>,
    announce: bool,
) {
    let count = paths.len();
    if service.shelf.add(&paths) {
        service.changed();
    }
    if announce && count > 0 {
        backend.hub.publish(shelf::added(count));
    }
}

/// Takes one path off the shelf. The file itself stays where it is.
#[tauri::command]
pub fn shelf_remove(service: State<'_, Arc<ShelfService>>, path: String) {
    if service.shelf.remove(&path) {
        service.changed();
    }
}

/// Takes everything off the shelf.
#[tauri::command]
pub fn shelf_clear(service: State<'_, Arc<ShelfService>>) {
    if service.shelf.clear() {
        service.changed();
    }
}

/// Opens a shelved file with the app the system has for it, or a shelved folder in the file manager.
#[tauri::command]
pub fn shelf_open(service: State<'_, Arc<ShelfService>>, path: String) {
    if let Err(e) = opener::open(&path) {
        // Most likely the file has gone since it was shelved.
        log::info!("could not open {path}: {e}");
        service.inner().clone().prune_in_background();
    }
}

/// Shows a shelved file selected in the file manager.
#[tauri::command]
pub fn shelf_reveal(service: State<'_, Arc<ShelfService>>, path: String) {
    if let Err(e) = platform::reveal(&path) {
        log::info!("could not show {path} in its folder: {e}");
        service.inner().clone().prune_in_background();
    }
}

/// The picture the file manager shows for a file, as a data URI, or `None` when there is none
/// (the tile keeps its glyph). `size` is the longest side wanted, in pixels.
#[tauri::command]
pub async fn shelf_thumbnail(path: String, size: u32) -> Option<String> {
    let picture = tauri::async_runtime::spawn_blocking(move || platform::thumbnail(&path, size.clamp(16, 512)))
        .await
        .ok()??;
    Some(format!("data:{};base64,{}", picture.mime, STANDARD.encode(picture.bytes)))
}

/// Starts the system's own drag of a shelved file, so any app or folder can take it. Returns when
/// the file has been dropped somewhere or the drag was called off. Dropping on a folder moves or
/// copies as the file manager does (Ctrl copies); what moved leaves the shelf by itself.
#[tauri::command]
pub async fn shelf_start_drag(
    service: State<'_, Arc<ShelfService>>,
    window: WebviewWindow,
    path: String,
) -> Result<DragEffect, String> {
    let (done, finished) = std::sync::mpsc::channel();
    let started = {
        let window = window.clone();
        window.clone().run_on_main_thread(move || {
            let report = done.clone();
            let outcome = platform::start_drag(&window, &path, move |effect| {
                let _ = report.send(Ok(effect));
            });
            if let Err(e) = outcome {
                let _ = done.send(Err(e));
            }
        })
    };
    started.map_err(|e| e.to_string())?;

    let effect = tauri::async_runtime::spawn_blocking(move || finished.recv())
        .await
        .map_err(|e| e.to_string())?
        .map_err(|e| e.to_string())??;

    let service = service.inner().clone();
    service.clone().prune_in_background();
    std::thread::spawn(move || {
        std::thread::sleep(MOVE_SETTLE);
        service.prune_in_background();
    });
    Ok(effect)
}

/// A picture of a file: encoded bytes and their type.
pub struct Picture {
    pub mime: &'static str,
    pub bytes: Vec<u8>,
}
