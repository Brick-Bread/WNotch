//! The parts of .NET's `System.IO.Path` the terminal and agent code use, with Windows rules on
//! every host: `\` and `/` both separate folders, and `C:` starts a rooted path. The app only runs
//! on Windows, but these paths are plain text here and must read the same in tests on Linux.

pub fn is_separator(c: char) -> bool {
    c == '\\' || c == '/'
}

/// `Path.IsPathRooted`: starts with a separator or a drive letter and a colon.
pub fn is_rooted(path: &str) -> bool {
    let mut chars = path.chars();
    match (chars.next(), chars.next()) {
        (Some(first), _) if is_separator(first) => true,
        (Some(drive), Some(':')) => drive.is_ascii_alphabetic(),
        _ => false,
    }
}

/// `Path.Combine` of two parts.
pub fn combine(first: &str, second: &str) -> String {
    if second.is_empty() {
        return first.to_owned();
    }

    if first.is_empty() || is_rooted(second) {
        return second.to_owned();
    }

    if first.ends_with(is_separator) || second.starts_with(is_separator) {
        format!("{first}{second}")
    } else {
        format!("{first}\\{second}")
    }
}

/// `Path.GetFileName`: what follows the last separator, or the drive (`C:name`).
pub fn file_name(path: &str) -> &str {
    let root =
        if path.len() >= 2 && path.as_bytes()[1] == b':' && path.as_bytes()[0].is_ascii_alphabetic() { 2 } else { 0 };
    let after_separator = path.rfind(is_separator).map_or(0, |i| i + 1);
    &path[after_separator.max(root)..]
}

/// `Path.GetFileNameWithoutExtension`: the file name up to its last `.`.
pub fn file_name_without_extension(path: &str) -> &str {
    let name = file_name(path);
    name.rfind('.').map_or(name, |i| &name[..i])
}

/// `Path.GetExtension`: from the file name's last `.`, or empty when there is none or it ends the path.
pub fn extension(path: &str) -> &str {
    match path.rfind(|c: char| c == '.' || is_separator(c)) {
        Some(i) if path[i..].starts_with('.') && i + 1 < path.len() => &path[i..],
        _ => "",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn follows_windows_rules() {
        assert!(is_rooted(r"C:\x") && is_rooted("C:x") && is_rooted("/x") && is_rooted(r"\\server\share"));
        assert!(!is_rooted("codex") && !is_rooted(r"bin\codex") && !is_rooted("1:x"));
        assert_eq!(combine(r"C:\Tools", "codex.exe"), r"C:\Tools\codex.exe");
        assert_eq!(combine(r"C:\Tools\", "codex.exe"), r"C:\Tools\codex.exe");
        assert_eq!(combine("", "codex.exe"), "codex.exe");
        assert_eq!(combine(r"C:\Tools", r"D:\x"), r"D:\x");
        assert_eq!(file_name(r"C:\bin\claude.cmd"), "claude.cmd");
        assert_eq!(file_name("C:/a/b"), "b");
        assert_eq!(file_name("C:name"), "name");
        assert_eq!(file_name_without_extension(r"C:\bin\my-codex.exe"), "my-codex");
        assert_eq!(file_name_without_extension(r"C:\a.b\c"), "c");
        assert_eq!(extension(r"C:\npm\codex.cmd"), ".cmd");
        assert_eq!(extension(r"C:\a.b\c"), "");
        assert_eq!(extension("x."), "");
    }
}
