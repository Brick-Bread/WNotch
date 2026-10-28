//! Finding, checking and verifying Notch updates. Ported from `src/Notch.Core/Updates`.

pub mod feed;
pub mod signing;
pub mod updater;

pub use feed::{ReleaseInfo, Version};
