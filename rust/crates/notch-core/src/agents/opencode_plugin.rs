//! opencode has no hook settings like Claude Code's. It loads plugins from a folder instead, and
//! a plugin sees what the session is doing. Notch's plugin is one small file that tells Notch when
//! opencode starts working, asks for permission, or finishes. Ported from
//! `src/Notch.Core/Agents/OpenCodePlugin.cs`: the pure part of putting it in place and taking it
//! out again; the app does the file work.

use super::hook_editor::HookEdit;
use crate::terminal::windows_path;

/// The file's name inside opencode's `plugins` folder.
pub const FILE_NAME: &str = "notch.js";

/// A line of the plugin that says it is Notch's, and which version of the plugin it is.
pub const MARKER: &str = "notch-opencode-plugin";

/// The plugin the app ships. The C# app copies this file next to Notch.exe (`Assets\opencode`)
/// and reads it from there; the Rust app carries it inside the binary.
pub const SHIPPED: &str = include_str!("../../../../../src/Notch.App/Assets/opencode/notch.js");

/// opencode's config folder: `OPENCODE_CONFIG_DIR` when set, otherwise `.config\opencode` in the home folder.
pub fn config_directory(config_dir_variable: Option<&str>, home_directory: &str) -> String {
    match config_dir_variable.filter(|v| !v.trim().is_empty()) {
        Some(dir) => dir.to_owned(),
        None => windows_path::combine(&windows_path::combine(home_directory, ".config"), "opencode"),
    }
}

pub fn plugin_path(config_dir_variable: Option<&str>, home_directory: &str) -> String {
    let plugins = windows_path::combine(&config_directory(config_dir_variable, home_directory), "plugins");
    windows_path::combine(&plugins, FILE_NAME)
}

/// Whether a file is Notch's plugin (as opposed to some other plugin the user named notch.js).
pub fn is_notchs(text: Option<&str>) -> bool {
    text.is_some_and(|t| t.contains(MARKER))
}

/// Writes `plugin` over Notch's own older copy, or into a place where there is none. Never over a file that is not Notch's.
pub fn add(existing: Option<&str>, plugin: &str) -> HookEdit {
    let Some(existing) = existing else {
        return HookEdit::new(plugin, true);
    };

    if !is_notchs(Some(existing)) {
        return HookEdit::refused(
            existing,
            format!(
                "opencode already has a plugin called {FILE_NAME} that is not Notch's, so Notch left opencode alone. Rename it to let Notch add its own."
            ),
        );
    }

    HookEdit::new(plugin, normalize(existing) != normalize(plugin))
}

/// Takes Notch's plugin out. The result has [`HookEdit::changed`] set and empty
/// [`HookEdit::text`] when the file is Notch's and should be deleted.
pub fn remove(existing: Option<&str>) -> HookEdit {
    if is_notchs(existing) { HookEdit::new("", true) } else { HookEdit::new(existing.unwrap_or(""), false) }
}

fn normalize(text: &str) -> String {
    text.replace("\r\n", "\n").trim_end().to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    const PLUGIN: &str = "// notch-opencode-plugin 1\nexport default {}\n";

    // From OpenCodeTests.cs (OpenCodePluginFileTests).

    #[test]
    fn the_plugin_goes_in_opencodes_plugins_folder() {
        assert_eq!(plugin_path(None, r"C:\Users\me"), r"C:\Users\me\.config\opencode\plugins\notch.js");
        assert_eq!(plugin_path(Some(r"D:\oc"), r"C:\Users\me"), r"D:\oc\plugins\notch.js");
        assert_eq!(config_directory(Some("  "), r"C:\Users\me"), r"C:\Users\me\.config\opencode");
    }

    #[test]
    fn a_new_plugin_is_added() {
        let edit = add(None, PLUGIN);

        assert!(edit.changed);
        assert_eq!(edit.text, PLUGIN);
        assert_eq!(edit.problem, None);
    }

    #[test]
    fn the_same_plugin_is_not_written_again_but_an_older_one_is_replaced() {
        assert!(!add(Some(&PLUGIN.replace('\n', "\r\n")), PLUGIN).changed);

        let update = add(Some("// notch-opencode-plugin 0\nold"), PLUGIN);
        assert!(update.changed);
        assert_eq!(update.text, PLUGIN);
    }

    #[test]
    fn a_plugin_that_is_not_notchs_is_never_overwritten() {
        let theirs = "export default { id: 'mine' }";

        let edit = add(Some(theirs), PLUGIN);

        assert!(!edit.changed);
        assert_eq!(edit.text, theirs);
        assert!(edit.problem.unwrap().contains("not Notch's"));
    }

    #[test]
    fn removing_deletes_only_notchs_own_file() {
        let ours = remove(Some(PLUGIN));
        assert!(ours.changed);
        assert_eq!(ours.text, "");

        assert!(!remove(Some("export default { id: 'mine' }")).changed);
        assert!(!remove(None).changed);
    }

    #[test]
    fn the_plugin_that_ships_with_the_app_is_recognised_as_notchs_and_has_the_shape_opencode_2_requires() {
        let text = SHIPPED;

        assert!(is_notchs(Some(text)));
        // opencode 2 refuses a plugin whose default export is not an object with an id and a setup function.
        assert!(text.contains("export default {"));
        assert!(text.contains("id: \"notch\""));
        assert!(text.contains("async setup("));
        for name in
            ["session.execution.started", "session.execution.succeeded", "permission.asked", "permission.replied"]
        {
            assert!(text.contains(name), "{name}");
        }

        // It must talk only to Notch's pipes, never the network.
        assert!(!text.contains("fetch("));
        assert!(!text.contains("http://"));
        assert!(!text.contains("https://"));
        assert!(text.contains("notch-agent-shared"));
    }
}
