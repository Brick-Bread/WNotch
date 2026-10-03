//! Starting Notch when the user signs in: the current user's Run key on Windows, an autostart
//! entry in the XDG config folder on Linux. Neither needs administrator rights.

use std::io;

/// The `.desktop` file that starts `executable` at sign-in. Exec quoting follows the Desktop
/// Entry specification: a quoted argument with `"`, `` ` ``, `$` and `\` backslash-escaped, and
/// `%` doubled.
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
pub fn desktop_entry(executable: &str) -> String {
    let mut quoted = String::from("\"");
    for c in executable.chars() {
        match c {
            '"' | '`' | '$' | '\\' => {
                quoted.push('\\');
                quoted.push(c);
            }
            '%' => quoted.push_str("%%"),
            _ => quoted.push(c),
        }
    }
    quoted.push('"');
    format!("[Desktop Entry]\nType=Application\nName=Notch\nComment=A notch for your desktop\nExec={quoted}\nX-GNOME-Autostart-enabled=true\n")
}

/// Whether Notch starts at sign-in.
pub fn is_enabled() -> bool {
    platform::is_enabled()
}

/// Turns starting at sign-in on or off. Enabling points it at the running executable.
pub fn set_enabled(enabled: bool) -> io::Result<()> {
    platform::set_enabled(enabled)
}

#[cfg(windows)]
mod platform {
    use std::io;

    use winreg::enums::{HKEY_CURRENT_USER, KEY_READ};
    use winreg::RegKey;

    const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
    const VALUE_NAME: &str = "Notch";

    pub fn is_enabled() -> bool {
        RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey_with_flags(RUN_KEY, KEY_READ)
            .and_then(|key| key.get_value::<String, _>(VALUE_NAME))
            .is_ok_and(|command| !command.is_empty())
    }

    pub fn set_enabled(enabled: bool) -> io::Result<()> {
        let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(RUN_KEY)?;
        if enabled {
            let executable = std::env::current_exe()?;
            key.set_value(VALUE_NAME, &format!("\"{}\"", executable.display()))
        } else {
            match key.delete_value(VALUE_NAME) {
                Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
                other => other,
            }
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use std::io;
    use std::path::PathBuf;

    fn entry_path() -> Option<PathBuf> {
        dirs::config_dir().map(|dir| dir.join("autostart").join("notch.desktop"))
    }

    pub fn is_enabled() -> bool {
        entry_path().is_some_and(|path| path.is_file())
    }

    pub fn set_enabled(enabled: bool) -> io::Result<()> {
        let path = entry_path().ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "no config folder"))?;
        if !enabled {
            return match std::fs::remove_file(&path) {
                Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
                other => other,
            };
        }
        let executable = std::env::current_exe()?;
        if let Some(folder) = path.parent() {
            std::fs::create_dir_all(folder)?;
        }
        std::fs::write(path, super::desktop_entry(&executable.to_string_lossy()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_entry_runs_the_executable() {
        let entry = desktop_entry("/opt/notch/notch");
        assert!(entry.starts_with("[Desktop Entry]\n"));
        assert!(entry.contains("\nExec=\"/opt/notch/notch\"\n"));
        assert!(entry.contains("\nType=Application\n"));
    }

    #[test]
    fn exec_quoting_escapes_what_the_specification_names() {
        let entry = desktop_entry(r#"/home/a b/$x/"q"/100%"#);
        assert!(entry.contains(r#"Exec="/home/a b/\$x/\"q\"/100%%""#));
    }
}
