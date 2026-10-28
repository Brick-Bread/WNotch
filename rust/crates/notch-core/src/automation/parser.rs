//! Turns what outside callers send into a [`NotchCommand`], or refuses it. Ported from
//! `src/Notch.Core/Automation/CommandParser.cs`.
//!
//! Every entry point (link, command line, webhook) goes through [`build`], so they all accept
//! exactly the same things. Anything unknown, oversized or malformed is refused, never guessed at.
//! The error messages are shown to the user; `tests/fixtures/commands` holds the C# answers.

use std::collections::HashMap;
use std::time::Duration;

use super::command::{NotchCommand, TimerAction, Toast};
use crate::activities::GlowColor;
use crate::widgets::duration;

pub const SCHEME: &str = "notch";

/// The longest `notch://` link or command line that is read at all, in UTF-16 units as C# counts.
pub const MAX_INPUT_LENGTH: usize = 2048;
pub const MAX_TITLE_LENGTH: usize = 120;
pub const MAX_DETAIL_LENGTH: usize = 300;
pub const MAX_GLYPH_LENGTH: usize = 8;
pub const MAX_PAGE_ID_LENGTH: usize = 64;
pub const MAX_TIMER: Duration = Duration::from_secs(24 * 60 * 60);
pub const MAX_TOAST_LIFETIME: Duration = Duration::from_secs(30);

pub const TABS: [&str; 6] = ["home", "terminal", "stats", "shelf", "plugins", "clipboard"];

/// Named values, matched ignoring case.
#[derive(Debug, Default, Clone)]
pub struct Values(HashMap<String, String>);

impl Values {
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets a value, replacing one of the same name.
    pub fn set(&mut self, name: &str, value: impl Into<String>) {
        self.0.insert(name.to_lowercase(), value.into());
    }

    /// Sets a value unless one of the same name is there.
    pub fn add(&mut self, name: &str, value: impl Into<String>) {
        self.0.entry(name.to_lowercase()).or_insert_with(|| value.into());
    }

    pub fn contains(&self, name: &str) -> bool {
        self.0.contains_key(&name.to_lowercase())
    }

    /// The value, trimmed, or empty when there is none.
    fn get(&self, name: &str) -> &str {
        self.0.get(&name.to_lowercase()).map_or("", |v| v.trim())
    }
}

/// The length C# gives a string: UTF-16 units.
pub(crate) fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

/// Reads `notch://verb?name=value&...`.
pub fn parse_url(url: &str) -> Result<NotchCommand, String> {
    if url.trim().is_empty() || utf16_len(url) > MAX_INPUT_LENGTH {
        return Err("That link is empty or too long.".into());
    }

    let link = Link::parse(url.trim()).filter(|link| link.scheme.eq_ignore_ascii_case(SCHEME));
    let Some(link) = link else {
        return Err("That is not a notch:// link.".into());
    };

    // notch://install?id=x puts the verb in the host; notch:install?id=x and notch:///install in the path.
    let verb = if link.host.is_empty() { link.path.trim_matches('/') } else { link.host };
    build(verb, &parse_query(link.query))
}

/// Reads the command line tool's arguments: `timer 5m`, `toast "Build done" --detail ok`,
/// `install acme.tool`. The first positional argument after the verb fills the verb's main parameter.
pub fn parse_arguments(arguments: &[String]) -> Result<NotchCommand, String> {
    let Some(first) = arguments.first() else {
        return Err("Say what to do, e.g. timer 5m.".into());
    };

    if arguments.iter().map(|a| utf16_len(a)).sum::<usize>() > MAX_INPUT_LENGTH {
        return Err("That is too long.".into());
    }

    let verb = first.to_lowercase();
    let main = main_parameter(&verb);
    let mut values = Values::new();
    let mut i = 1;
    while i < arguments.len() {
        let argument = &arguments[i];
        if let Some(name) = argument.strip_prefix("--") {
            if let Some((name, value)) = name.split_once('=') {
                values.set(name, value);
            } else if i + 1 < arguments.len() && !arguments[i + 1].starts_with("--") {
                i += 1;
                values.set(name, arguments[i].clone());
            } else {
                values.set(name, "true");
            }
        } else if let Some(main) = main.filter(|main| !values.contains(main)) {
            values.set(main, argument.clone());
        } else if verb == "open" && !values.contains("page") {
            // open page chomp.desk/chomp
            values.set("page", argument.clone());
        }
        i += 1;
    }

    build(&verb, &values)
}

/// Builds a command from a verb and its named values.
pub fn build(verb: &str, values: &Values) -> Result<NotchCommand, String> {
    match verb.trim().to_lowercase().as_str() {
        "install" => {
            let id = values.get("id");
            if !is_plugin_id(id) {
                return Err("install needs a plugin id.".into());
            }
            Ok(NotchCommand::InstallPlugin { plugin_id: id.to_owned() })
        }
        "plugin" => {
            let id = values.get("id");
            if !is_plugin_id(id) {
                return Err("plugin needs a plugin id.".into());
            }
            let Some(enabled) = parse_bool(values.get("enable")) else {
                return Err("plugin needs enable=true or enable=false.".into());
            };
            Ok(NotchCommand::SetPluginEnabled { plugin_id: id.to_owned(), enabled })
        }
        "timer" => build_timer(values),
        "open" => {
            let tab = values.get("tab").to_lowercase();
            let page = values.get("page");
            if !page.is_empty() || tab == "page" || tab.contains('/') {
                let page = if page.is_empty() { values.get("tab") } else { page };
                if !is_page_key(page) {
                    return Err("open page needs a plugin's page as plugin-id/page-id, e.g. acme.tool/main.".into());
                }
                return Ok(NotchCommand::OpenPage { page: page.to_owned() });
            }

            if !TABS.contains(&tab.as_str()) {
                return Err(format!("open needs a tab: {}, or page plugin-id/page-id.", TABS.join(", ")));
            }
            Ok(NotchCommand::OpenTab { tab })
        }
        "toast" => build_toast(values),
        "palette" => Ok(NotchCommand::OpenPalette),
        "settings" => Ok(NotchCommand::OpenSettings),
        _ => Err("Unknown command.".into()),
    }
}

/// The parameter a bare argument after the verb stands for.
fn main_parameter(verb: &str) -> Option<&'static str> {
    match verb {
        "install" | "plugin" => Some("id"),
        "timer" => Some("d"),
        "open" => Some("tab"),
        "toast" => Some("title"),
        _ => None,
    }
}

fn first_of<'a>(values: &'a Values, names: &[&str]) -> &'a str {
    names.iter().map(|name| values.get(name)).find(|value| !value.is_empty()).unwrap_or("")
}

fn build_timer(values: &Values) -> Result<NotchCommand, String> {
    let text = first_of(values, &["d", "duration", "action"]);
    let action = match text.to_lowercase().as_str() {
        "stop" | "reset" | "cancel" => Some(TimerAction::Stop),
        "pause" => Some(TimerAction::Pause),
        "resume" => Some(TimerAction::Resume),
        _ => None,
    };
    if let Some(action) = action {
        return Ok(NotchCommand::TimerAction(action));
    }

    let Some(duration) = duration::parse(text).filter(|d| !d.is_zero()) else {
        return Err("timer needs a duration like 5m, 90s or 1h20m, or stop, pause or resume.".into());
    };
    if duration > MAX_TIMER {
        return Err("A timer can be at most 24 hours.".into());
    }
    Ok(NotchCommand::StartTimer { duration })
}

fn build_toast(values: &Values) -> Result<NotchCommand, String> {
    let title = first_of(values, &["title", "text"]);
    if title.is_empty() || utf16_len(title) > MAX_TITLE_LENGTH {
        return Err(format!("toast needs a title of 1 to {MAX_TITLE_LENGTH} characters."));
    }

    let detail = values.get("detail");
    if utf16_len(detail) > MAX_DETAIL_LENGTH {
        return Err(format!("The detail can be at most {MAX_DETAIL_LENGTH} characters."));
    }

    let glyph = values.get("glyph");
    if utf16_len(glyph) > MAX_GLYPH_LENGTH {
        return Err("The glyph is one symbol.".into());
    }

    let color = match values.get("color") {
        "" => None,
        name => Some(parse_color(name).ok_or("The colour is a name like Green, or #rrggbb.")?),
    };

    let lifetime = match values.get("lifetime") {
        "" => None,
        text => {
            let parsed = duration::parse(text).filter(|d| !d.is_zero()).ok_or("The lifetime is a duration like 5s.")?;
            Some(parsed.min(MAX_TOAST_LIFETIME))
        }
    };

    let some = |text: &str| (!text.is_empty()).then(|| text.to_owned());
    Ok(NotchCommand::Toast(Toast {
        title: title.to_owned(),
        detail: some(detail),
        glyph: some(glyph),
        color,
        lifetime,
    }))
}

/// A colour name from [`GlowColor::NAMED`] or `#rrggbb`.
pub fn parse_color(text: &str) -> Option<GlowColor> {
    let text = text.trim();
    if text.is_empty() {
        return None;
    }

    if let Some(hex) = text.strip_prefix('#').filter(|hex| hex.len() == 6 && hex.bytes().all(|b| b.is_ascii_hexdigit()))
    {
        let channel = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).ok();
        return Some(GlowColor::new(channel(0)?, channel(2)?, channel(4)?));
    }

    GlowColor::from_name(text)
}

/// The id rule plugin manifests use: lowercase letters and digits in groups split by dots or dashes.
pub fn is_plugin_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 64
        && id
            .split(['.', '-'])
            .all(|group| !group.is_empty() && group.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit()))
}

/// A plugin's id, a slash and one of its page ids: printable, without line breaks, of a sensible length.
pub fn is_page_key(key: &str) -> bool {
    let Some((plugin, page)) = key.split_once('/') else { return false };
    is_plugin_id(plugin)
        && (1..=MAX_PAGE_ID_LENGTH).contains(&utf16_len(page))
        && !page.chars().any(char::is_control)
        && page.trim() == page
}

fn parse_bool(text: &str) -> Option<bool> {
    match text.to_lowercase().as_str() {
        "true" | "1" | "yes" | "on" => Some(true),
        "false" | "0" | "no" | "off" => Some(false),
        _ => None,
    }
}

/// The parts of an absolute link this needs, split the way `System.Uri` splits them.
struct Link<'a> {
    scheme: &'a str,
    host: &'a str,
    path: &'a str,
    query: &'a str,
}

impl<'a> Link<'a> {
    fn parse(text: &'a str) -> Option<Self> {
        let (scheme, rest) = text.split_once(':')?;
        let mut chars = scheme.chars();
        if !chars.next()?.is_ascii_alphabetic() || !chars.all(|c| c.is_ascii_alphanumeric() || "+-.".contains(c)) {
            return None;
        }

        // The fragment is not part of the query.
        let rest = rest.split_once('#').map_or(rest, |(before, _)| before);
        let (rest, query) = rest.split_once('?').map_or((rest, ""), |(before, query)| (before, query));

        let (host, path) = match rest.strip_prefix("//") {
            Some(authority_and_path) => {
                let end = authority_and_path.find('/').unwrap_or(authority_and_path.len());
                let (authority, path) = authority_and_path.split_at(end);
                if !authority.chars().all(|c| c.is_ascii_alphanumeric() || "-._~%!$&'()*+,;=:@[]".contains(c)) {
                    return None;
                }
                // userinfo@host:port
                let host = authority.rsplit_once('@').map_or(authority, |(_, host)| host);
                let host = match host.rsplit_once(':') {
                    Some((name, port)) if port.bytes().all(|b| b.is_ascii_digit()) => name,
                    _ => host,
                };
                (host, path)
            }
            None => ("", rest),
        };

        Some(Self { scheme, host, path, query })
    }
}

fn parse_query(query: &str) -> Values {
    let mut values = Values::new();
    for pair in query.split('&').filter(|pair| !pair.is_empty()) {
        let (name, value) = match pair.split_once('=') {
            Some((name, value)) => (unescape(&name.replace('+', " ")), unescape(&value.replace('+', " "))),
            None => (unescape(&pair.replace('+', " ")), "true".to_owned()),
        };
        values.add(&name, value);
    }
    values
}

/// `Uri.UnescapeDataString`: `%xx` sequences decoded; text whose escapes do not make UTF-8 is left as it was.
fn unescape(text: &str) -> String {
    let bytes = text.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let (Some(high), Some(low)) = (hex_digit(bytes[i + 1]), hex_digit(bytes[i + 2])) {
                out.push(high << 4 | low);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8(out).unwrap_or_else(|_| text.to_owned())
}

fn hex_digit(byte: u8) -> Option<u8> {
    (byte as char).to_digit(16).map(|d| d as u8)
}

#[cfg(test)]
mod tests;
