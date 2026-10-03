//! Linux (and other Unix): pictures from the freedesktop thumbnail cache, the file manager over
//! D-Bus or `xdg-open`, and dragging a file out through GTK with the `drag` crate.

use std::fs;
use std::path::Path;
use std::process::{Command, Stdio};

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use notch_core::shelf::file_uri;

use super::{DragEffect, Picture};

/// Image files this big are not sent over as their own picture.
const MAX_OWN_PICTURE: u64 = 8 * 1024 * 1024;

/// Folders of the freedesktop thumbnail cache, biggest first.
const THUMBNAIL_SIZES: [&str; 3] = ["x-large", "large", "normal"];

/// The cached thumbnail the desktop made for the file (named after the MD5 of its URI), else the
/// file itself when it is a small picture. `None` leaves the tile on its glyph.
pub fn thumbnail(path: &str, _size: u32) -> Option<Picture> {
    cached_thumbnail(path).or_else(|| own_picture(path))
}

#[cfg(target_os = "linux")]
fn cached_thumbnail(path: &str) -> Option<Picture> {
    use md5::{Digest, Md5};

    let name: String = Md5::digest(file_uri(path).as_bytes()).iter().map(|byte| format!("{byte:02x}")).collect();
    let cache = dirs::cache_dir()?.join("thumbnails");
    THUMBNAIL_SIZES
        .iter()
        .find_map(|size| fs::read(cache.join(size).join(format!("{name}.png"))).ok())
        .map(|bytes| Picture { mime: "image/png", bytes })
}

#[cfg(not(target_os = "linux"))]
fn cached_thumbnail(_path: &str) -> Option<Picture> {
    let _ = THUMBNAIL_SIZES;
    None
}

fn own_picture(path: &str) -> Option<Picture> {
    let mime = match Path::new(path).extension()?.to_str()?.to_ascii_lowercase().as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        _ => return None,
    };
    if fs::metadata(path).ok()?.len() > MAX_OWN_PICTURE {
        return None;
    }
    Some(Picture { mime, bytes: fs::read(path).ok()? })
}

/// Shows the file selected in the file manager, or its folder when none speaks the FileManager1
/// D-Bus interface.
pub fn reveal(path: &str) -> std::io::Result<()> {
    let shown = Command::new("dbus-send")
        .args([
            "--session",
            "--print-reply",
            "--dest=org.freedesktop.FileManager1",
            "--type=method_call",
            "/org/freedesktop/FileManager1",
            "org.freedesktop.FileManager1.ShowItems",
        ])
        .arg(format!("array:string:{}", file_uri(path)))
        .arg("string:")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .is_ok_and(|status| status.success());
    if shown {
        return Ok(());
    }

    let folder = Path::new(path).parent().ok_or_else(|| std::io::Error::other("no folder to show"))?;
    opener::open(folder).map_err(std::io::Error::other)
}

/// A one pixel transparent PNG (base64) for the drag icon, which `drag` insists on.
const BLANK_PNG: &str =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

/// Drags `path` out of the window with GTK's drag and drop. `done` is told once the drag ended.
/// GTK offers one action per drag: files are copied, which every receiver accepts.
pub fn start_drag(
    window: &tauri::WebviewWindow,
    path: &str,
    done: impl FnOnce(DragEffect) + Send + 'static,
) -> Result<(), String> {
    use std::sync::Mutex;

    let finished = Mutex::new(Some(done));
    let on_end = move |result: drag::DragResult, _cursor: drag::CursorPosition| {
        let effect = match result {
            drag::DragResult::Dropped => DragEffect::Copy,
            drag::DragResult::Cancel => DragEffect::None,
        };
        if let Some(done) = finished.lock().unwrap_or_else(|e| e.into_inner()).take() {
            done(effect);
        }
    };
    let item = drag::DragItem::Files(vec![path.into()]);
    let icon = drag::Image::Raw(STANDARD.decode(BLANK_PNG).map_err(|e| e.to_string())?);

    #[cfg(target_os = "linux")]
    let handle = window.gtk_window().map_err(|e| e.to_string())?;
    #[cfg(not(target_os = "linux"))]
    let handle = window;

    drag::start_drag(&handle, item, icon, on_end, drag::Options::default()).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_picture_file_stands_for_itself() {
        let folder = std::env::temp_dir().join(format!("notch-shelf-unix-{}", std::process::id()));
        fs::create_dir_all(&folder).expect("folder");
        let image = folder.join("a.PNG");
        fs::write(&image, STANDARD.decode(BLANK_PNG).expect("png")).expect("write");
        let text = folder.join("a.txt");
        fs::write(&text, "x").expect("write");

        let picture = own_picture(image.to_str().expect("utf-8")).expect("picture");
        assert_eq!(picture.mime, "image/png");
        assert!(own_picture(text.to_str().expect("utf-8")).is_none());

        let _ = fs::remove_dir_all(&folder);
    }
}
