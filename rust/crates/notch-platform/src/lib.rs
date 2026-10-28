//! Notch's Windows integrations, ported from `src/Notch.Platform`: windows, displays and
//! fullscreen detection, media sessions, HUD sources, audio, ConPTY, named pipes, the clipboard,
//! hotkeys, thumbnails and system stats.
//!
//! The Windows calls compile only on Windows; the plain types around them compile everywhere, so
//! their tests also run on other systems.

#[cfg(windows)]
pub mod conpty;
pub mod display;
#[cfg(windows)]
pub mod pipes;
#[cfg(windows)]
pub mod registry;
