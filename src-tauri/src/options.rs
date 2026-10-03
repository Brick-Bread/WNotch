//! The command-line switches, which are for development and for scripts: they change how this
//! run looks and starts without touching the saved settings.
//!
//! Switches other modules own (`--demo`, `--plugin=`, `--screenshots=`, `--hook` ...) are
//! ignored here, as is anything unknown.

use notch_core::settings::{NotchPosition, NotchStyle, NotchTheme};
use serde::Serialize;

/// What this run was asked to do. Sent to the UI with the state, which acts on `open`, `tab`,
/// `settings` and `pin_open`; the window follows `display`, `style` and `position` by itself.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StartupOptions {
    /// `--open=claude|codex|shell`: start a terminal session of that button straight away.
    pub open: Option<String>,
    /// `--tab=home|terminal|...`: show that tab. Names the UI does not know are ignored.
    pub tab: Option<String>,
    /// `--pin-open`: keep the notch expanded whatever the pointer does.
    pub pin_open: bool,
    /// `--settings`: open on the settings panel.
    pub settings: bool,
    /// `--style=notch|island`, for this run only.
    pub style: Option<NotchStyle>,
    /// `--position=topcenter|taskbarleft`, for this run only.
    pub position: Option<NotchPosition>,
    /// `--display=2`: the display the settings list as "Display 2", as a zero-based index.
    pub display: Option<i32>,
    /// `--theme=dark|light|system`, for this run only.
    pub theme: Option<NotchTheme>,
}

impl StartupOptions {
    /// Reads the switches, without the program name. Switch names and values are not case-sensitive.
    pub fn parse<I, S>(args: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let mut options = Self::default();
        for argument in args {
            let argument = argument.as_ref();
            let (name, value) = match argument.split_once('=') {
                Some((name, value)) => (name, Some(value)),
                None => (argument, None),
            };
            match (name.to_ascii_lowercase().as_str(), value) {
                ("--pin-open", None) => options.pin_open = true,
                ("--settings", None) => options.settings = true,
                ("--open", Some(v)) if !v.is_empty() => options.open = Some(v.to_owned()),
                ("--tab", Some(v)) if !v.is_empty() => options.tab = Some(v.to_owned()),
                ("--style", Some(v)) => options.style = from_name(v).or(options.style),
                ("--position", Some(v)) => options.position = from_name(v).or(options.position),
                ("--theme", Some(v)) => options.theme = from_name(v).or(options.theme),
                ("--display", Some(v)) => {
                    // The settings count displays from 1; the setting itself from 0.
                    options.display = v.parse::<i32>().ok().filter(|n| *n >= 1).map(|n| n - 1).or(options.display);
                }
                _ => {}
            }
        }
        options
    }
}

/// Reads an enum from its camelCase name, ignoring case, the way the C# `Enum.TryParse` does.
fn from_name<T: serde::de::DeserializeOwned>(name: &str) -> Option<T> {
    let wanted = name.to_ascii_lowercase();
    let candidates = ["notch", "island", "topCenter", "taskbarLeft", "dark", "light", "system"];
    let exact = candidates.iter().find(|c| c.to_ascii_lowercase() == wanted)?;
    serde_json::from_value(serde_json::Value::String((*exact).to_owned())).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_switches() {
        let options = StartupOptions::parse([
            "--open=Claude", "--tab=terminal", "--pin-open", "--settings", "--style=ISLAND",
            "--position=taskbarleft", "--display=2", "--theme=light",
        ]);
        assert_eq!(options.open.as_deref(), Some("Claude"));
        assert_eq!(options.tab.as_deref(), Some("terminal"));
        assert!(options.pin_open && options.settings);
        assert_eq!(options.style, Some(NotchStyle::Island));
        assert_eq!(options.position, Some(NotchPosition::TaskbarLeft));
        assert_eq!(options.display, Some(1));
        assert_eq!(options.theme, Some(NotchTheme::Light));
    }

    #[test]
    fn switches_are_not_case_sensitive() {
        let options = StartupOptions::parse(["--PIN-OPEN", "--Position=TopCenter"]);
        assert!(options.pin_open);
        assert_eq!(options.position, Some(NotchPosition::TopCenter));
    }

    #[test]
    fn unknown_and_unreadable_switches_are_ignored() {
        let options = StartupOptions::parse([
            "--demo", "--plugin=C:\\dev\\plugin", "--screenshots=out", "--style=square", "--display=0",
            "--display=x", "--open=", "--pin-open=yes", "stray",
        ]);
        assert_eq!(options, StartupOptions::default());
    }
}
