//! Windows: the Explorer picture of a file (`IShellItemImageFactory`) and an OLE drag of a file
//! out of the window, which lets the receiver choose between copying, moving and linking the way
//! it does for a drag from Explorer itself.

use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::process::CommandExt;
use std::process::Command;

use windows::Win32::Foundation::{
    COLORREF, DRAGDROP_S_CANCEL, DRAGDROP_S_DROP, DRAGDROP_S_USEDEFAULTCURSORS, POINT, S_OK, SIZE,
};
use windows::Win32::Graphics::Gdi::{
    BITMAP, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC, DeleteObject, GetDIBits,
    GetObjectW, HBITMAP, HGDIOBJ,
};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize,
    IBindCtx, IDataObject,
};
use windows::Win32::System::Ole::{
    DROPEFFECT, DROPEFFECT_COPY, DROPEFFECT_LINK, DROPEFFECT_MOVE, DoDragDrop, IDropSource, IDropSource_Impl,
    OleInitialize,
};
use windows::Win32::System::SystemServices::{MK_LBUTTON, MODIFIERKEYS_FLAGS};
use windows::Win32::UI::Shell::Common::ITEMIDLIST;
use windows::Win32::UI::Shell::{
    BHID_DataObject, CLSID_DragDropHelper, IDragSourceHelper, IShellItemImageFactory, SHCreateItemFromParsingName,
    SHCreateShellItemArrayFromIDLists, SHDRAGIMAGE, SHParseDisplayName, SIIGBF,
};
use windows::core::{BOOL, HRESULT, PCWSTR, implement};

use super::{DragEffect, Picture};

/// The size of the picture that follows the pointer while a tile is dragged.
const DRAG_IMAGE_SIZE: u32 = 96;

fn wide(text: &str) -> Vec<u16> {
    std::ffi::OsStr::new(text).encode_wide().chain(Some(0)).collect()
}

/// COM for the current thread, for as long as it is held.
struct Com(bool);

impl Com {
    fn enter() -> Self {
        // SAFETY: plain COM initialisation; undone in `drop` only when it succeeded.
        Self(unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }.is_ok())
    }
}

impl Drop for Com {
    fn drop(&mut self) {
        if self.0 {
            // SAFETY: pairs the successful `CoInitializeEx` in `enter`.
            unsafe { CoUninitialize() };
        }
    }
}

/// What Explorer shows for the path: a thumbnail where the file has one, its icon otherwise.
/// Premultiplied BGRA pixels in a GDI bitmap that the caller deletes.
fn shell_image(path: &str, size: u32) -> Option<HBITMAP> {
    let path = wide(path);
    let side = i32::try_from(size).ok()?;
    // SAFETY: `path` is a NUL-terminated UTF-16 string that outlives the call.
    unsafe {
        let item: IShellItemImageFactory = SHCreateItemFromParsingName(PCWSTR(path.as_ptr()), None::<&IBindCtx>).ok()?;
        item.GetImage(SIZE { cx: side, cy: side }, SIIGBF(0)).ok()
    }
}

/// The bitmap's pixels as straight RGBA, top row first.
fn read_rgba(bitmap: HBITMAP) -> Option<(u32, u32, Vec<u8>)> {
    // SAFETY: the structures are sized as the calls expect and `pixels` is as big as `GetDIBits` writes.
    unsafe {
        let mut shape = BITMAP::default();
        let read = GetObjectW(
            HGDIOBJ(bitmap.0),
            i32::try_from(size_of::<BITMAP>()).ok()?,
            Some(std::ptr::from_mut(&mut shape).cast::<c_void>()),
        );
        if read == 0 || shape.bmWidth <= 0 || shape.bmHeight <= 0 {
            return None;
        }

        let (width, height) = (u32::try_from(shape.bmWidth).ok()?, u32::try_from(shape.bmHeight).ok()?);
        let mut info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: u32::try_from(size_of::<BITMAPINFOHEADER>()).ok()?,
                biWidth: shape.bmWidth,
                // A negative height asks for the rows top first.
                biHeight: -shape.bmHeight,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..BITMAPINFOHEADER::default()
            },
            ..BITMAPINFO::default()
        };
        let mut pixels = vec![0_u8; usize::try_from(width).ok()? * usize::try_from(height).ok()? * 4];
        let dc = CreateCompatibleDC(None);
        let rows = GetDIBits(dc, bitmap, 0, height, Some(pixels.as_mut_ptr().cast::<c_void>()), &mut info, DIB_RGB_COLORS);
        let _ = DeleteDC(dc);
        if rows == 0 {
            return None;
        }

        Some((width, height, to_straight_rgba(pixels)))
    }
}

/// Premultiplied BGRA to straight RGBA. Some thumbnails come without an alpha channel, which
/// reads as fully transparent: those are made opaque.
fn to_straight_rgba(mut pixels: Vec<u8>) -> Vec<u8> {
    let alpha_missing = pixels.chunks_exact(4).all(|pixel| pixel[3] == 0);
    for pixel in pixels.chunks_exact_mut(4) {
        if alpha_missing {
            pixel[3] = 255;
        }
        let alpha = u32::from(pixel[3]);
        let straight = |channel: u8| {
            if alpha == 0 || alpha == 255 {
                channel
            } else {
                u8::try_from((u32::from(channel) * 255 / alpha).min(255)).unwrap_or(255)
            }
        };
        let (blue, green, red) = (straight(pixel[0]), straight(pixel[1]), straight(pixel[2]));
        pixel[0] = red;
        pixel[1] = green;
        pixel[2] = blue;
    }
    pixels
}

fn encode_png(width: u32, height: u32, rgba: &[u8]) -> Option<Vec<u8>> {
    let mut bytes = Vec::new();
    let mut encoder = png::Encoder::new(&mut bytes, width, height);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    encoder.write_header().ok()?.write_image_data(rgba).ok()?;
    Some(bytes)
}

/// The Explorer picture of a file or folder as a PNG, or `None` when the path leads nowhere or
/// the shell has no picture for it. A thumbnail that is not cached yet can take a moment.
pub fn thumbnail(path: &str, size: u32) -> Option<Picture> {
    let _com = Com::enter();
    let bitmap = shell_image(path, size)?;
    let pixels = read_rgba(bitmap);
    // SAFETY: the bitmap came from `GetImage` and is not used again.
    let _ = unsafe { DeleteObject(HGDIOBJ(bitmap.0)) };
    let (width, height, rgba) = pixels?;
    Some(Picture { mime: "image/png", bytes: encode_png(width, height, &rgba)? })
}

/// Opens Explorer with the file selected.
pub fn reveal(path: &str) -> std::io::Result<()> {
    // Explorer parses its own command line: the path stays in one piece only inside quotes.
    Command::new("explorer.exe").raw_arg(format!("/select,\"{path}\"")).spawn().map(drop)
}

/// The drop source of a drag: it ends when the button goes up or Esc is pressed.
#[implement(IDropSource)]
struct DropSource;

impl IDropSource_Impl for DropSource_Impl {
    fn QueryContinueDrag(&self, escape_pressed: BOOL, key_state: MODIFIERKEYS_FLAGS) -> HRESULT {
        if escape_pressed.as_bool() {
            DRAGDROP_S_CANCEL
        } else if key_state.0 & MK_LBUTTON.0 == 0 {
            DRAGDROP_S_DROP
        } else {
            S_OK
        }
    }

    fn GiveFeedback(&self, _effect: DROPEFFECT) -> HRESULT {
        DRAGDROP_S_USEDEFAULTCURSORS
    }
}

/// The shell's own data object for the file, which carries what an Explorer drag carries (the
/// file list, the shell ID list, ...), so every kind of target can take it.
fn data_object(path: &str) -> Result<IDataObject, String> {
    let name = wide(path);
    let mut id_list: *mut ITEMIDLIST = std::ptr::null_mut();
    // SAFETY: `name` is NUL-terminated; the ID list is freed once the item array holds its own copy.
    unsafe {
        SHParseDisplayName(PCWSTR(name.as_ptr()), None::<&IBindCtx>, &mut id_list, 0, None).map_err(|e| e.to_string())?;
        let items = SHCreateShellItemArrayFromIDLists(&[id_list.cast_const()]);
        CoTaskMemFree(Some(id_list.cast_const().cast::<c_void>()));
        items
            .and_then(|items| items.BindToHandler::<_, IDataObject>(None::<&IBindCtx>, &BHID_DataObject))
            .map_err(|e| e.to_string())
    }
}

/// Gives the drag the file's picture to carry along. Without one the drag still works.
fn attach_drag_image(path: &str, data: &IDataObject) {
    let Some(bitmap) = shell_image(path, DRAG_IMAGE_SIZE) else { return };
    // SAFETY: the shell takes over the bitmap when this succeeds; otherwise it is deleted here.
    unsafe {
        let shape = {
            let mut shape = BITMAP::default();
            let read = GetObjectW(
                HGDIOBJ(bitmap.0),
                i32::try_from(size_of::<BITMAP>()).unwrap_or(0),
                Some(std::ptr::from_mut(&mut shape).cast::<c_void>()),
            );
            (read != 0).then_some(shape)
        };
        let attached = shape.is_some_and(|shape| {
            let image = SHDRAGIMAGE {
                sizeDragImage: SIZE { cx: shape.bmWidth, cy: shape.bmHeight },
                ptOffset: POINT { x: shape.bmWidth / 2, y: shape.bmHeight / 2 },
                hbmpDragImage: bitmap,
                crColorKey: COLORREF(0xFFFF_FFFF),
            };
            CoCreateInstance::<_, IDragSourceHelper>(&CLSID_DragDropHelper, None, CLSCTX_INPROC_SERVER)
                .and_then(|helper| helper.InitializeFromBitmap(&image, data))
                .is_ok()
        });
        if !attached {
            let _ = DeleteObject(HGDIOBJ(bitmap.0));
        }
    }
}

/// Drags `path` out of the window with the system's drag and drop, which keeps running (and the
/// window's messages with it) until the file is dropped or the drag is cancelled. Must be called
/// on the thread that owns the window, with the left button down. `done` is told what happened.
pub fn start_drag(
    _window: &tauri::WebviewWindow,
    path: &str,
    done: impl FnOnce(DragEffect) + Send + 'static,
) -> Result<(), String> {
    // SAFETY: OLE is set up for this thread; repeated calls only count.
    unsafe { OleInitialize(None) }.map_err(|e| e.to_string())?;

    let data = data_object(path)?;
    attach_drag_image(path, &data);

    let source: IDropSource = DropSource.into();
    let mut effect = DROPEFFECT(0);
    // SAFETY: both objects are alive for the whole call, which is a modal loop.
    let result = unsafe { DoDragDrop(&data, &source, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, &mut effect) };

    done(if result == DRAGDROP_S_DROP { effect_of(effect) } else { DragEffect::None });
    Ok(())
}

fn effect_of(effect: DROPEFFECT) -> DragEffect {
    if effect.contains(DROPEFFECT_MOVE) {
        DragEffect::Move
    } else if effect.contains(DROPEFFECT_COPY) {
        DragEffect::Copy
    } else if effect.contains(DROPEFFECT_LINK) {
        DragEffect::Link
    } else {
        DragEffect::None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_picture_without_alpha_is_made_opaque() {
        assert_eq!(to_straight_rgba(vec![10, 20, 30, 0]), [30, 20, 10, 255]);
    }

    #[test]
    fn premultiplied_pixels_are_made_straight() {
        // Half transparent mid-grey stored as 50% of its value.
        assert_eq!(to_straight_rgba(vec![50, 50, 50, 128, 0, 0, 0, 0]), [99, 99, 99, 128, 0, 0, 0, 0]);
    }

    #[test]
    fn the_effect_reports_the_strongest_action() {
        assert_eq!(effect_of(DROPEFFECT_MOVE), DragEffect::Move);
        assert_eq!(effect_of(DROPEFFECT_COPY), DragEffect::Copy);
        assert_eq!(effect_of(DROPEFFECT(0)), DragEffect::None);
    }
}
