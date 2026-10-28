//! What was copied. Ported from `src/Notch.Core/Clipboard`.

pub mod history;

pub use history::{
    ClipboardHistory, ClipboardItem, ClipboardKind, DEFAULT_CAPACITY, MAX_FILES, MAX_IMAGE_BYTES, MAX_TEXT_LENGTH,
    pins, privacy,
};

#[cfg(test)]
mod tests;
