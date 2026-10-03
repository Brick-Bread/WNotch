//! Album art: shrinks what a player supplies to a size the UI needs and picks the colour the
//! glow takes from it.

use std::io::Cursor;

use image::codecs::jpeg::JpegEncoder;
use notch_core::glow::{AccentColor, GlowColor};

/// The longest side of the artwork sent to the UI (the Home card shows it at 112 px).
const MAX_SIDE: u32 = 256;

/// The side of the picture the accent colour is averaged over.
const ACCENT_SIDE: u32 = 64;

const JPEG_QUALITY: u8 = 85;

/// Artwork ready for the UI.
pub struct Art {
    /// JPEG bytes, at most [`MAX_SIDE`] on the longest side.
    pub jpeg: Vec<u8>,
    /// The glow colour taken from the picture; `None` for greyscale artwork.
    pub accent: Option<GlowColor>,
}

/// Decodes `bytes` (PNG, JPEG, GIF or WebP); `None` when they are not an image.
pub fn process(bytes: &[u8]) -> Option<Art> {
    let decoded = image::load_from_memory(bytes).ok()?;

    let mut jpeg = Vec::new();
    let shown = decoded.thumbnail(MAX_SIDE, MAX_SIDE).to_rgb8();
    JpegEncoder::new_with_quality(Cursor::new(&mut jpeg), JPEG_QUALITY)
        .encode_image(&shown)
        .ok()?;

    let mut bgra = decoded.thumbnail(ACCENT_SIDE, ACCENT_SIDE).to_rgba8().into_raw();
    for pixel in bgra.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
    Some(Art { jpeg, accent: AccentColor::from_pixels(&bgra) })
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::{ImageFormat, Rgb, RgbImage};

    fn encoded(width: u32, height: u32, color: [u8; 3]) -> Vec<u8> {
        let mut bytes = Vec::new();
        RgbImage::from_pixel(width, height, Rgb(color))
            .write_to(&mut Cursor::new(&mut bytes), ImageFormat::Png)
            .expect("encodes");
        bytes
    }

    #[test]
    fn large_art_is_shrunk_and_coloured() {
        let art = process(&encoded(600, 300, [255, 0, 0])).expect("an image");
        let shown = image::load_from_memory(&art.jpeg).expect("a jpeg");
        assert_eq!((shown.width(), shown.height()), (256, 128));
        assert_eq!(art.accent.map(|c| (c.r, c.g > 40 || c.b > 40)), Some((255, false)));
    }

    #[test]
    fn grey_art_has_no_accent() {
        assert!(process(&encoded(8, 8, [128, 128, 128])).is_some_and(|art| art.accent.is_none()));
    }

    #[test]
    fn non_images_are_rejected() {
        assert!(process(b"not an image").is_none());
    }
}
