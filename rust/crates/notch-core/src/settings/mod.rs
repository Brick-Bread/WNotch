//! The user's settings and where Notch keeps its files. Ported from `src/Notch.Core/Settings`.
//!
//! `settings.json` is shared with the C# app: an installed copy upgrades in place, so this reads
//! what the C# app wrote and writes what it reads. The same key names, the same defaults, and the
//! same reading rules ([`crate::json`]); `tests/fixtures/settings` holds the C# answers. Keys this
//! version does not know are kept and written back, so a newer version's settings survive.

mod folders;

use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde_json::{Map, Value};

pub use folders::DataFolders;

use crate::activities::GlowColor;
use crate::activities::glow::output as glow_output;
use crate::json::{self, Read, json_enum};
use crate::widgets::{HomeWidget, PomodoroDurations, TimerPreset, layout};

json_enum! {
    /// The shape the pill takes against the edge of the screen.
    pub enum NotchStyle {
        /// Grows out of the screen edge like a camera notch: flat against it, rounded away from it.
        Notch = 0,
        /// A pill rounded all the way around that floats a little clear of the edge.
        Island = 1,
    }
    default Notch;
}

json_enum! {
    /// Where on the display the pill sits.
    pub enum NotchPosition {
        /// Middle of the top edge; the panel opens downwards.
        TopCenter = 0,
        /// In the taskbar's far left corner; the panel opens upwards.
        TaskbarLeft = 1,
    }
    default TopCenter;
}

json_enum! {
    pub enum NotchTheme {
        /// Black island with light text.
        Dark = 0,
        /// Light island with dark text.
        Light = 1,
        /// Whichever of the two Windows is set to use for apps.
        System = 2,
    }
    default Dark;
}

impl NotchTheme {
    /// Whether the theme comes out light, given what Windows is set to.
    pub fn is_light(self, system_is_light: bool) -> bool {
        self == Self::Light || (self == Self::System && system_is_light)
    }
}

/// The accent colour setting that means "no accent": anything that is not a preset name does.
pub const NO_ACCENT: &str = "None";

/// As many preset buttons as the timer card has room for.
pub const MAX_TIMER_PRESETS: usize = 6;

const MAX_RECENT_FOLDERS: usize = 8;

/// The port the webhook listens on unless the user picks another.
pub const DEFAULT_WEBHOOK_PORT: i32 = 47890;

fn default_timer_presets() -> Vec<TimerPreset> {
    [5, 15, 30, 60].into_iter().map(|minutes| TimerPreset::new("", minutes * 60)).collect()
}

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|&s| s.to_owned()).collect()
}

fn write_bool(value: &bool) -> Value {
    Value::Bool(*value)
}

fn write_int(value: &i32) -> Value {
    Value::from(*value)
}

fn write_optional_int(value: &Option<i32>) -> Value {
    value.map_or(Value::Null, Value::from)
}

fn write_string(value: &str) -> Value {
    Value::String(value.to_owned())
}

fn write_optional_string(value: &Option<String>) -> Value {
    json::to_optional_string(value.as_deref())
}

fn write_strings(value: &[String]) -> Value {
    json::to_string_list(value)
}

fn read_presets(value: &Value) -> Read<Vec<TimerPreset>> {
    json::list(value, TimerPreset::read)
}

fn write_presets(value: &[TimerPreset]) -> Value {
    Value::Array(value.iter().map(TimerPreset::to_json).collect())
}

fn read_widgets(value: &Value) -> Read<Vec<HomeWidget>> {
    json::list(value, HomeWidget::read)
}

fn write_widgets(value: &[HomeWidget]) -> Value {
    Value::Array(value.iter().map(HomeWidget::to_json).collect())
}

/// Declares the settings: each field with its key in the file, its default, and how it is read
/// and written. The order is the order the C# app writes them in.
macro_rules! settings {
    ( $( $(#[$meta:meta])* $field:ident : $ty:ty = $key:literal, $default:expr, $read:expr, $write:expr; )+ ) => {
        #[derive(Debug, Clone, PartialEq)]
        pub struct AppSettings {
            $( $(#[$meta])* pub $field: $ty, )+
            /// Keys this version does not know, kept so they are written back.
            pub unknown: Map<String, Value>,
        }

        impl Default for AppSettings {
            fn default() -> Self {
                Self { $( $field: $default, )+ unknown: Map::new() }
            }
        }

        impl AppSettings {
            /// The keys in the file, in the order they are written.
            pub const KEYS: &'static [&'static str] = &[ $( $key, )+ ];

            fn read_key(&mut self, key: &str, value: &Value) -> Read<bool> {
                match key {
                    $( $key => self.$field = ($read)(value)?, )+
                    _ => return Ok(false),
                }
                Ok(true)
            }

            fn write_keys(&self, map: &mut Map<String, Value>) {
                $( map.insert($key.to_owned(), ($write)(&self.$field)); )+
            }
        }
    };
}

settings! {
    /// Which display hosts the notch, as an index into the system's display list. `None` means the primary display.
    display_index: Option<i32> = "DisplayIndex", None, json::optional_int, write_optional_int;
    style: NotchStyle = "Style", NotchStyle::Notch, NotchStyle::read, |v: &NotchStyle| v.to_json();
    position: NotchPosition = "Position", NotchPosition::TopCenter, NotchPosition::read, |v: &NotchPosition| v.to_json();
    expand_on_hover: bool = "ExpandOnHover", true, json::bool, write_bool;
    shelf_opens_on_drag: bool = "ShelfOpensOnDrag", true, json::bool, write_bool;
    /// Keys that open and close the notch from any app. Empty, or anything that is not a hotkey, means none.
    open_hotkey: String = "OpenHotkey", "Alt+Shift+N".to_owned(), json::string, write_string;
    hide_in_fullscreen: bool = "HideInFullscreen", true, json::bool, write_bool;
    theme: NotchTheme = "Theme", NotchTheme::Dark, NotchTheme::read, |v: &NotchTheme| v.to_json();
    /// A theme offered by a plugin, as "plugin-id/theme-id", used instead of `theme` while its plugin runs.
    plugin_theme: Option<String> = "PluginTheme", None, json::optional_string, write_optional_string;
    /// Name of the glow colour preset that tints buttons, bars and the selected tab. Anything that is
    /// not a preset, such as [`NO_ACCENT`], leaves them uncoloured.
    accent_color: String = "AccentColor", "Blue".to_owned(), json::string, write_string;
    glow_effects: bool = "GlowEffects", true, json::bool, write_bool;
    /// Glow brightness in percent of the standard; clamped where it is used.
    glow_intensity: i32 = "GlowIntensity", glow_output::DEFAULT_PERCENT, json::int, write_int;
    notify_of_updates: bool = "NotifyOfUpdates", true, json::bool, write_bool;
    /// The release last announced, so each is announced once.
    last_notified_update_tag: Option<String> = "LastNotifiedUpdateTag", None, json::optional_string, write_optional_string;
    show_media: bool = "ShowMedia", true, json::bool, write_bool;
    show_volume: bool = "ShowVolume", true, json::bool, write_bool;
    show_brightness: bool = "ShowBrightness", true, json::bool, write_bool;
    show_power: bool = "ShowPower", true, json::bool, write_bool;
    show_bluetooth: bool = "ShowBluetooth", true, json::bool, write_bool;
    show_caps_lock: bool = "ShowCapsLock", true, json::bool, write_bool;
    pomodoro_focus_minutes: i32 = "PomodoroFocusMinutes", 25, json::int, write_int;
    pomodoro_short_break_minutes: i32 = "PomodoroShortBreakMinutes", 5, json::int, write_int;
    pomodoro_long_break_minutes: i32 = "PomodoroLongBreakMinutes", 15, json::int, write_int;
    /// The one-click timers on the timer card, in the order shown.
    timer_presets: Vec<TimerPreset> = "TimerPresets", default_timer_presets(), read_presets, write_presets;
    /// The widgets on the Home tab, in order.
    home_widgets: Vec<HomeWidget> = "HomeWidgets", layout::default_layout(), read_widgets, write_widgets;
    /// iCalendar (.ics) links shown on the Home tab's calendar card.
    calendar_feeds: Vec<String> = "CalendarFeeds", Vec::new(), json::string_list, write_strings;
    /// Ids of the plugins the user has switched on. Installed plugins do not run until listed here.
    enabled_plugins: Vec<String> = "EnabledPlugins", Vec::new(), json::string_list, write_strings;
    /// The buttons that start terminal sessions, one per line, such as `Work Claude = CLAUDE_CONFIG_DIR=C:\work claude`.
    terminal_presets: Vec<String> = "TerminalPresets", strings(&crate::terminal::DEFAULT_PRESETS), json::string_list, write_strings;
    /// Folders terminal sessions were started in, most recent first.
    recent_folders: Vec<String> = "RecentFolders", Vec::new(), json::string_list, write_strings;
    /// Accept requests on the local webhook. Off until the user turns it on.
    webhook_enabled: bool = "WebhookEnabled", false, json::bool, write_bool;
    /// The port the webhook listens on, on this computer only.
    webhook_port: i32 = "WebhookPort", DEFAULT_WEBHOOK_PORT, json::int, write_int;
    /// The secret every webhook request must carry. Made when the webhook is first switched on.
    webhook_token: Option<String> = "WebhookToken", None, json::optional_string, write_optional_string;
    /// Keys that open the command palette, read like `open_hotkey`. Empty for none.
    palette_hotkey: String = "PaletteHotkey", "Alt+Shift+P".to_owned(), json::string, write_string;
    /// Show agents that were not started from Notch's terminal too.
    detect_agents: bool = "DetectAgents", true, json::bool, write_bool;
    /// Ids of agents the user does not want shown.
    hidden_agents: Vec<String> = "HiddenAgents", Vec::new(), json::string_list, write_strings;
    /// Show the sessions open in the Claude and ChatGPT desktop apps too.
    watch_app_sessions: bool = "WatchAppSessions", true, json::bool, write_bool;
    /// Whether Notch has put its hooks into the agents' own settings files.
    global_agent_hooks: bool = "GlobalAgentHooks", false, json::bool, write_bool;
    /// Keep a history of what was copied, in the Clipboard tab. Off until the user turns it on.
    clipboard_history: bool = "ClipboardHistory", false, json::bool, write_bool;
    /// Show Windows notifications in the pill. Off until the user turns it on.
    mirror_notifications: bool = "MirrorNotifications", false, json::bool, write_bool;
    /// Names of the apps whose notifications are shown. Empty means all of them.
    mirrored_apps: Vec<String> = "MirroredApps", Vec::new(), json::string_list, write_strings;
}

impl AppSettings {
    /// Reads settings as the C# app does: anything unusable gives the defaults.
    pub fn from_json(text: &str) -> Self {
        let Ok(value) = serde_json::from_str::<Value>(text) else { return Self::default() };
        match value {
            Value::Object(map) => Self::read(&map).unwrap_or_default(),
            _ => Self::default(),
        }
    }

    fn read(map: &Map<String, Value>) -> Read<Self> {
        let mut settings = Self::default();
        for (key, value) in map {
            if !settings.read_key(key, value)? {
                settings.unknown.insert(key.clone(), value.clone());
            }
        }
        Ok(settings)
    }

    pub fn to_json_value(&self) -> Value {
        let mut map = Map::new();
        self.write_keys(&mut map);
        for (key, value) in &self.unknown {
            map.insert(key.clone(), value.clone());
        }
        Value::Object(map)
    }

    /// The settings as the file holds them: indented JSON.
    pub fn to_json(&self) -> String {
        serde_json::to_string_pretty(&self.to_json_value()).expect("settings are always valid JSON")
    }

    /// Pomodoro lengths from the settings, with anything out of range replaced by the classic value.
    pub fn pomodoro_durations(&self) -> PomodoroDurations {
        let classic = PomodoroDurations::CLASSIC;
        let minutes = |value: i32, fallback: Duration| {
            if (1..=600).contains(&value) { Duration::from_secs(value as u64 * 60) } else { fallback }
        };
        PomodoroDurations {
            focus: minutes(self.pomodoro_focus_minutes, classic.focus),
            short_break: minutes(self.pomodoro_short_break_minutes, classic.short_break),
            long_break: minutes(self.pomodoro_long_break_minutes, classic.long_break),
            ..classic
        }
    }

    /// The timer presets that can be used: sensible lengths, tidy names, no more than fit.
    pub fn timers(&self) -> Vec<TimerPreset> {
        self.timer_presets
            .iter()
            .filter(|preset| preset.is_valid())
            .take(MAX_TIMER_PRESETS)
            .map(|preset| TimerPreset::new(crate::widgets::timer_preset::clip_name(&preset.name), preset.seconds))
            .collect()
    }

    /// The activity ids the user has switched off.
    pub fn suppressed_activity_ids(&self) -> Vec<&'static str> {
        use crate::hud;
        let mut ids = Vec::new();
        if !self.show_media {
            ids.push(crate::media::ACTIVITY_ID);
        }
        if !self.show_volume {
            ids.extend([hud::VOLUME_ID, hud::AUDIO_OUTPUT_ID]);
        }
        if !self.show_brightness {
            ids.push(hud::BRIGHTNESS_ID);
        }
        if !self.show_power {
            ids.push(hud::POWER_ID);
        }
        if !self.show_bluetooth {
            ids.push(hud::BLUETOOTH_ID);
        }
        if !self.show_caps_lock {
            ids.push(hud::CAPS_LOCK_ID);
        }
        ids
    }

    /// Puts `folder` first in the recent folders, once, keeping at most eight.
    pub fn remember_folder(&mut self, folder: &str) {
        let key = folder.to_lowercase();
        self.recent_folders.retain(|f| f.to_lowercase() != key);
        self.recent_folders.insert(0, folder.to_owned());
        self.recent_folders.truncate(MAX_RECENT_FOLDERS);
    }

    /// The accent colour preset, or `None` when the setting names none.
    pub fn accent(&self) -> Option<GlowColor> {
        GlowColor::from_name(&self.accent_color)
    }
}

/// Loads and saves [`AppSettings`] as JSON. A missing or corrupt file gives the defaults.
#[derive(Debug, Clone)]
pub struct SettingsStore {
    path: PathBuf,
}

impl SettingsStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> AppSettings {
        fs::read_to_string(&self.path).map(|text| AppSettings::from_json(&text)).unwrap_or_default()
    }

    /// Writes the settings through a temporary file, so a crash cannot leave half a file. Failing
    /// to save is reported but never fatal: settings are a convenience.
    pub fn save(&self, settings: &AppSettings) -> io::Result<()> {
        if let Some(folder) = self.path.parent() {
            fs::create_dir_all(folder)?;
        }
        let temporary = tmp_path(&self.path);
        fs::write(&temporary, settings.to_json())?;
        fs::rename(&temporary, &self.path)
    }
}

/// `<path>.tmp`, where a file is written before it replaces `path`.
pub(crate) fn tmp_path(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(".tmp");
    PathBuf::from(name)
}

#[cfg(test)]
mod tests;
