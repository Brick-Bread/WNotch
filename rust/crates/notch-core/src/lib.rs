//! Notch's logic that does not touch Windows: settings, commands, activities, agents, widgets,
//! the shelf, clipboard history and updates. Ported from `src/Notch.Core`, minus the plugin
//! system, which stays in .NET in the plugin host.

pub mod activities;
pub mod automation;
pub mod hud;
pub mod json;
pub mod media;
pub mod settings;
pub mod terminal;
pub mod widgets;

#[cfg(test)]
mod fixtures;
