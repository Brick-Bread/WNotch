//! The buttons of a launcher or commands widget. Ported from `WidgetButtons` in
//! `src/Notch.Core/Widgets/HomeWidgetData.cs`.

/// A button on a launcher or commands widget.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WidgetButton {
    pub label: String,
    /// What a launcher opens (a program, file, folder or link), or the command a commands widget runs.
    pub target: String,
}

impl WidgetButton {
    pub fn new(label: impl Into<String>, target: impl Into<String>) -> Self {
        Self { label: label.into(), target: target.into() }
    }
}

pub const MAX_BUTTONS: usize = 8;

/// Reads the buttons, one per line as `Name = target`.
///
/// Lines without a name are given one from the target: a file's or folder's name, or a link's host.
/// Empty lines and lines starting with # are skipped.
pub fn parse(text: Option<&str>) -> Vec<WidgetButton> {
    let mut buttons = Vec::new();
    for raw in text.unwrap_or("").split('\n') {
        let line = raw.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with('=') {
            continue;
        }

        // A target such as https://x/?a=b has an = of its own; only one before it names the button.
        let equals = line.find('=');
        let scheme = line.find("://");
        let (label, target) = match equals {
            Some(equals) if equals > 0 && scheme.is_none_or(|s| equals < s) => {
                (line[..equals].trim(), line[equals + 1..].trim())
            }
            _ => ("", line),
        };

        if target.is_empty() {
            continue;
        }

        let label = if label.is_empty() { name_for(target) } else { label.to_owned() };
        buttons.push(WidgetButton::new(label, target));
        if buttons.len() == MAX_BUTTONS {
            break;
        }
    }

    buttons
}

fn name_for(target: &str) -> String {
    let unquoted = target.trim_matches('"');
    if let Some(name) = link_name(unquoted) {
        return name;
    }

    let trimmed = unquoted.trim_end_matches(['\\', '/']);
    let name = file_name_without_extension(trimmed);
    if name.is_empty() { trimmed.to_owned() } else { name.to_owned() }
}

/// For text C#'s `Uri.TryCreate` reads as an absolute link that is not a file: its host, or its
/// scheme when it has none. Only what names a button is handled: a scheme of two or more
/// characters (one letter is a drive), an authority after `//`, and mailto's address.
fn link_name(text: &str) -> Option<String> {
    let (scheme, rest) = text.split_once(':')?;
    let valid = scheme.len() >= 2
        && scheme.starts_with(|c: char| c.is_ascii_alphabetic())
        && scheme.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.'));
    if !valid || scheme.eq_ignore_ascii_case("file") {
        return None;
    }

    let scheme = scheme.to_ascii_lowercase();
    let host = if let Some(authority) = rest.strip_prefix("//") {
        let authority = authority.split(['/', '?', '#', '\\']).next().unwrap_or("");
        let host = authority.rsplit_once('@').map_or(authority, |(_, host)| host);
        let host = match host.rsplit_once(':') {
            Some((name, port)) if !host.ends_with(']') && port.bytes().all(|b| b.is_ascii_digit()) => name,
            _ => host,
        };
        host.to_lowercase()
    } else if scheme == "mailto" {
        rest.split('?').next().and_then(|a| a.rsplit_once('@')).map(|(_, host)| host.to_lowercase()).unwrap_or_default()
    } else {
        String::new()
    };

    Some(if host.is_empty() { scheme } else { host })
}

/// `Path.GetFileNameWithoutExtension` as it works on Windows, wherever this runs.
fn file_name_without_extension(path: &str) -> &str {
    let name = match path.rfind(['\\', '/']) {
        Some(separator) => &path[separator + 1..],
        None if path.len() >= 2 && path.as_bytes()[1] == b':' && path.as_bytes()[0].is_ascii_alphabetic() => &path[2..],
        None => path,
    };
    name.rfind('.').map_or(name, |dot| &name[..dot])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn widget_buttons_read_names_targets_and_links() {
        let buttons = parse(Some(
            "Notes = C:\\Tools\\notes.exe\r\n\r\n# a comment\nhttps://example.com/?q=a=b\nC:\\Users\\me\\Projects\\\nBuild = dotnet build -c Release\n = \n",
        ));

        assert_eq!(
            buttons,
            [
                WidgetButton::new("Notes", "C:\\Tools\\notes.exe"),
                WidgetButton::new("example.com", "https://example.com/?q=a=b"),
                WidgetButton::new("Projects", "C:\\Users\\me\\Projects\\"),
                WidgetButton::new("Build", "dotnet build -c Release"),
            ]
        );
    }

    #[test]
    fn names_come_from_files_hosts_and_schemes() {
        let name = |target| parse(Some(target))[0].label.clone();
        assert_eq!(name("\"C:\\Program Files\\App\\app.exe\""), "app");
        assert_eq!(name("https://user@Example.com:8080/x"), "example.com");
        assert_eq!(name("mailto:me@example.org"), "example.org");
        assert_eq!(name("ms-settings:display"), "ms-settings");
        assert_eq!(name("notepad.exe"), "notepad");
        assert_eq!(name("C:\\"), "C:");
        assert_eq!(parse(Some(&"a\n".repeat(20))).len(), MAX_BUTTONS);
        assert!(parse(None).is_empty());
    }
}
