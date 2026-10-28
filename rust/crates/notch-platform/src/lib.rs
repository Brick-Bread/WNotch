//! Notch's Windows integrations, ported from `src/Notch.Platform`: windows, displays and
//! fullscreen detection, media sessions, HUD sources, audio, ConPTY, named pipes, the clipboard,
//! hotkeys, thumbnails and system stats.
//!
//! The Windows calls compile only on Windows; the plain types around them compile everywhere, so
//! their tests also run on other systems.

#[cfg(windows)]
pub mod audio;
#[cfg(windows)]
pub mod clipboard;
#[cfg(windows)]
pub mod command_pipe;
#[cfg(windows)]
pub mod conpty;
pub mod display;
#[cfg(windows)]
pub mod fullscreen;
#[cfg(windows)]
pub mod hook_server;
#[cfg(windows)]
pub mod hud;
#[cfg(windows)]
pub mod keyboard;
#[cfg(windows)]
pub mod media;
#[cfg(windows)]
pub mod notifications;
#[cfg(windows)]
pub mod pipes;
#[cfg(windows)]
pub mod process;
#[cfg(windows)]
pub mod process_scan;
#[cfg(windows)]
pub mod registry;
pub mod spectrum;
#[cfg(windows)]
pub mod stats;
#[cfg(windows)]
pub mod thumbnails;
pub mod webhook_server;
#[cfg(windows)]
pub mod window;
