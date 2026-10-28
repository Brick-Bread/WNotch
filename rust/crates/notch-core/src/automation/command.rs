//! Ported from `src/Notch.Core/Automation/NotchCommand.cs`.

use std::time::Duration;

use crate::activities::GlowColor;

/// Something an outside caller may ask the notch to do: a `notch://` link, the command line tool
/// and the webhook all end up as one of these. Only the commands listed here exist, so what a link
/// can do is limited to what is safe to do on a stranger's say-so.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NotchCommand {
    /// Asks to install a plugin from the registry. The user still has to confirm.
    InstallPlugin {
        plugin_id: String,
    },
    /// Switches an installed plugin on or off.
    SetPluginEnabled {
        plugin_id: String,
        enabled: bool,
    },
    StartTimer {
        duration: Duration,
    },
    TimerAction(TimerAction),
    /// Opens the notch on a tab: `home`, `terminal`, `stats`, `shelf`, `plugins` or `clipboard`.
    OpenTab {
        tab: String,
    },
    /// Opens the notch on a plugin's page, named `plugin-id/page-id`. Ignored when that page is not there.
    OpenPage {
        page: String,
    },
    /// A short notice in the pill.
    Toast(Toast),
    OpenPalette,
    OpenSettings,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TimerAction {
    Stop,
    Pause,
    Resume,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Toast {
    pub title: String,
    pub detail: Option<String>,
    pub glyph: Option<String>,
    pub color: Option<GlowColor>,
    pub lifetime: Option<Duration>,
}
