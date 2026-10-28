//! The picture Explorer shows for a file or folder: a thumbnail where the file has one, its icon
//! otherwise. Ported from `src/Notch.Platform/Shell/FileThumbnails.cs`.

use windows::Win32::Foundation::SIZE;
use windows::Win32::Graphics::Gdi::{
    BITMAP, BITMAPINFO, BITMAPINFOHEADER, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC, DeleteObject, GetDIBits,
    GetObjectW, HBITMAP, HGDIOBJ,
};
use windows::Win32::UI::Shell::{IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF_RESIZETOFIT};
use windows::core::HSTRING;

/// A picture of a file: 32-bit pixels with premultiplied alpha, blue first, top row first.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileThumbnail {
    pub width: u32,
    pub height: u32,
    pub pixels: Vec<u8>,
}

/// `None` when the path leads nowhere or the shell has no picture for it. `size` is the longest
/// side wanted, in pixels. Call on a thread that has COM set up; a thumbnail that is not cached
/// yet can take a moment.
pub fn thumbnail(path: &str, size: i32) -> Option<FileThumbnail> {
    // SAFETY: COM calls on an interface the shell returned; the bitmap is deleted below.
    unsafe {
        let factory: IShellItemImageFactory = SHCreateItemFromParsingName(&HSTRING::from(path), None).ok()?;
        let bitmap = factory.GetImage(SIZE { cx: size, cy: size }, SIIGBF_RESIZETOFIT).ok()?;
        let result = read(bitmap);
        let _ = DeleteObject(HGDIOBJ(bitmap.0));
        result
    }
}

fn read(bitmap: HBITMAP) -> Option<FileThumbnail> {
    let mut shape = BITMAP::default();
    // SAFETY: `shape` is a BITMAP of the size given.
    let filled = unsafe { GetObjectW(HGDIOBJ(bitmap.0), size_of::<BITMAP>() as i32, Some((&raw mut shape).cast())) };
    if filled == 0 || shape.bmWidth <= 0 || shape.bmHeight <= 0 {
        return None;
    }

    // A negative height asks for the rows top first.
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: shape.bmWidth,
            biHeight: -shape.bmHeight,
            biPlanes: 1,
            biBitCount: 32,
            ..Default::default()
        },
        ..Default::default()
    };

    let (width, height) = (shape.bmWidth as u32, shape.bmHeight as u32);
    let mut pixels = vec![0u8; width as usize * height as usize * 4];
    // SAFETY: `pixels` holds height rows of width 32-bit pixels, as `info` describes.
    unsafe {
        let dc = CreateCompatibleDC(None);
        let lines = GetDIBits(dc, bitmap, 0, height, Some(pixels.as_mut_ptr().cast()), &mut info, DIB_RGB_COLORS);
        let _ = DeleteDC(dc);
        if lines == 0 {
            return None;
        }
    }

    // Some thumbnails come without an alpha channel, which reads as fully transparent.
    if pixels.chunks_exact(4).all(|pixel| pixel[3] == 0) {
        for pixel in pixels.chunks_exact_mut(4) {
            pixel[3] = 255;
        }
    }

    Some(FileThumbnail { width, height, pixels })
}
