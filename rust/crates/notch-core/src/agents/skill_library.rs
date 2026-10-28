//! Finds the skills and subagents the coding agents (Claude Code and the like) would load, by
//! reading the `.claude/skills` and `.claude/agents` folders. Ported from
//! `src/Notch.Core/Agents/SkillLibrary.cs`.
//!
//! A skill is a folder with a `SKILL.md`; a subagent is a single `.md` file. Both open with a YAML
//! front-matter block carrying a name and description, which is what the lists show.

use std::fs;
use std::path::{Path, PathBuf};

use indexmap::IndexMap;

use super::ignore_case;

/// Whether a library entry is a skill or a subagent.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum LibraryItemKind {
    Skill,
    Agent,
}

/// A skill or subagent available to the coding agents, as read from a `.claude` folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LibraryItem {
    pub kind: LibraryItemKind,
    pub name: String,
    pub description: String,
    /// Where it was found: "User" for the home folder, else the project folder's name.
    pub scope: String,
    /// The folder that holds the `.claude` this came from: where a session for it should start.
    pub root: PathBuf,
}

/// Reads every skill and subagent under the given `.claude` folders. When a name repeats
/// within a kind a later folder wins, matching how a project's own entries shadow the user's.
/// Entries come back sorted by kind, then name.
///
/// `claude_dirs`: each a `.claude` folder and the scope label to show for what it holds.
pub fn discover<P: AsRef<Path>, S: AsRef<str>>(claude_dirs: &[(P, S)]) -> Vec<LibraryItem> {
    let mut by_key: IndexMap<(LibraryItemKind, String), LibraryItem> = IndexMap::new();
    for (dir, scope) in claude_dirs {
        let (dir, scope) = (dir.as_ref(), scope.as_ref());

        // The folder that owns the .claude is where a session launched for one of its entries runs.
        let text = dir.to_string_lossy();
        let trimmed = Path::new(text.trim_end_matches(['\\', '/']));
        let root = trimmed.parent().unwrap_or(dir).to_path_buf();

        let items = read_skills(&dir.join("skills"), scope, &root).into_iter().chain(read_agents(
            &dir.join("agents"),
            scope,
            &root,
        ));
        for item in items {
            by_key.insert((item.kind, item.name.clone()), item);
        }
    }

    let mut items: Vec<LibraryItem> = by_key.into_values().collect();
    items.sort_by(|a, b| a.kind.cmp(&b.kind).then_with(|| ignore_case::cmp(&a.name, &b.name)));
    items
}

/// One skill per sub-folder, named by its `SKILL.md` or, failing that, the folder.
fn read_skills(skills_dir: &Path, scope: &str, root: &Path) -> Vec<LibraryItem> {
    let mut items = Vec::new();
    for folder in safe_entries(skills_dir, |path| path.is_dir()) {
        let file = folder.join("SKILL.md");
        if !file.is_file() {
            continue;
        }

        let (name, description) = read_metadata(&file);
        items.push(LibraryItem {
            kind: LibraryItemKind::Skill,
            name: name.filter(|n| !n.trim().is_empty()).unwrap_or_else(|| file_name(&folder)),
            description: description.unwrap_or_default(),
            scope: scope.to_owned(),
            root: root.to_path_buf(),
        });
    }
    items
}

/// One subagent per `.md` file, named by its front matter or, failing that, the file.
fn read_agents(agents_dir: &Path, scope: &str, root: &Path) -> Vec<LibraryItem> {
    // `*.md`, which Windows matches ignoring case.
    let is_markdown = |path: &Path| path.is_file() && ignore_case::ends_with(&file_name(path), ".md");

    let mut items = Vec::new();
    for file in safe_entries(agents_dir, is_markdown) {
        let (name, description) = read_metadata(&file);
        items.push(LibraryItem {
            kind: LibraryItemKind::Agent,
            name: name.filter(|n| !n.trim().is_empty()).unwrap_or_else(|| file_stem(&file)),
            description: description.unwrap_or_default(),
            scope: scope.to_owned(),
            root: root.to_path_buf(),
        });
    }
    items
}

fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

/// `Path.GetFileNameWithoutExtension`: up to the last `.`.
fn file_stem(path: &Path) -> String {
    let name = file_name(path);
    name.rfind('.').map_or(name.clone(), |i| name[..i].to_owned())
}

/// Lists a folder, treating a missing or unreadable one as empty.
fn safe_entries(dir: &Path, keep: impl Fn(&Path) -> bool) -> Vec<PathBuf> {
    let Ok(entries) = fs::read_dir(dir) else {
        return Vec::new();
    };

    entries.filter_map(|entry| entry.ok().map(|e| e.path())).filter(|path| keep(path)).collect()
}

fn read_metadata(file: &Path) -> (Option<String>, Option<String>) {
    match fs::read(file) {
        Ok(bytes) => parse_front_matter(&decode(&bytes)),
        Err(_) => (None, None),
    }
}

/// `File.ReadAllText`: UTF-8 unless a byte order mark says UTF-16; bad bytes become U+FFFD.
fn decode(bytes: &[u8]) -> String {
    let utf16 = |rest: &[u8], unit: fn([u8; 2]) -> u16| {
        let units: Vec<u16> = rest.chunks_exact(2).map(|pair| unit([pair[0], pair[1]])).collect();
        String::from_utf16_lossy(&units)
    };

    match bytes {
        [0xEF, 0xBB, 0xBF, rest @ ..] => String::from_utf8_lossy(rest).into_owned(),
        [0xFF, 0xFE, rest @ ..] => utf16(rest, u16::from_le_bytes),
        [0xFE, 0xFF, rest @ ..] => utf16(rest, u16::from_be_bytes),
        _ => String::from_utf8_lossy(bytes).into_owned(),
    }
}

/// Pulls `name` and `description` out of a leading `---` YAML block. Only the two
/// top-level keys are read; a block scalar (`>` or `|`) folds its indented lines into one.
pub(crate) fn parse_front_matter(text: &str) -> (Option<String>, Option<String>) {
    let text = text.replace("\r\n", "\n");
    let lines: Vec<&str> = text.split('\n').collect();
    let mut i = 0;
    while i < lines.len() && lines[i].trim().is_empty() {
        i += 1;
    }

    if i >= lines.len() || lines[i].trim() != "---" {
        return (None, None);
    }

    let mut name = None;
    let mut description = None;
    i += 1;
    while i < lines.len() {
        let line = lines[i];
        if line.trim() == "---" {
            break;
        }

        // Indented lines belong to the value above, which the block-scalar reader already took.
        if !(line.is_empty() || line.starts_with([' ', '\t'])) {
            if let Some(value) = try_key(line, "name") {
                name = Some(read_value(value, &lines, &mut i));
            } else if let Some(value) = try_key(line, "description") {
                description = Some(read_value(value, &lines, &mut i));
            }
        }

        i += 1;
    }

    (name, description)
}

/// The text after `key:` when the line starts with it.
fn try_key<'a>(line: &'a str, key: &str) -> Option<&'a str> {
    ignore_case::strip_prefix(line, &format!("{key}:")).map(str::trim)
}

/// The value on the key's line, unquoted; or, for a block scalar, the indented lines folded together.
fn read_value(inline: &str, lines: &[&str], i: &mut usize) -> String {
    let block_scalar =
        matches!(inline, ">" | "|" | ">-" | "|-" | ">+" | "|+") || inline.starts_with("> ") || inline.starts_with("| ");
    if !block_scalar {
        return unquote(inline).to_owned();
    }

    let mut parts = Vec::new();
    while *i + 1 < lines.len() && (lines[*i + 1].is_empty() || lines[*i + 1].starts_with([' ', '\t'])) {
        *i += 1;
        let folded = lines[*i].trim();
        if !folded.is_empty() {
            parts.push(folded);
        }
    }

    parts.join(" ")
}

fn unquote(value: &str) -> &str {
    let quoted = value.len() >= 2
        && ((value.starts_with('"') && value.ends_with('"')) || (value.starts_with('\'') && value.ends_with('\'')));
    if quoted { &value[1..value.len() - 1] } else { value }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Library {
        root: tempfile::TempDir,
    }

    impl Library {
        fn new() -> Self {
            Self { root: tempfile::tempdir().unwrap() }
        }

        fn claude(&self, scope: &str) -> PathBuf {
            let dir = self.root.path().join(scope).join(".claude");
            fs::create_dir_all(&dir).unwrap();
            dir
        }
    }

    fn write_skill(claude_dir: &Path, folder: &str, body: &str) {
        let dir = claude_dir.join("skills").join(folder);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("SKILL.md"), body).unwrap();
    }

    fn write_agent(claude_dir: &Path, file: &str, body: &str) {
        let dir = claude_dir.join("agents");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(file), body).unwrap();
    }

    #[test]
    fn reads_skills_and_agents_with_their_front_matter() {
        let library = Library::new();
        let user = library.claude("user");
        write_skill(&user, "pdf", "---\nname: pdf\ndescription: Work with PDF files\n---\nbody");
        write_agent(&user, "reviewer.md", "---\nname: reviewer\ndescription: Reviews code\n---\nbody");

        let items = discover(&[(&user, "User")]);

        let skills: Vec<_> = items.iter().filter(|i| i.kind == LibraryItemKind::Skill).collect();
        assert_eq!(skills.len(), 1);
        assert_eq!(skills[0].name, "pdf");
        assert_eq!(skills[0].description, "Work with PDF files");
        assert_eq!(skills[0].scope, "User");

        let agents: Vec<_> = items.iter().filter(|i| i.kind == LibraryItemKind::Agent).collect();
        assert_eq!(agents.len(), 1);
        assert_eq!(agents[0].name, "reviewer");
        assert_eq!(agents[0].description, "Reviews code");
    }

    #[test]
    fn root_is_the_folder_that_holds_the_claude_directory() {
        let library = Library::new();
        let user = library.claude("user");
        write_agent(&user, "reviewer.md", "---\nname: reviewer\n---");

        let items = discover(&[(&user, "User")]);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].root, library.root.path().join("user"));

        // A trailing separator does not make the .claude folder its own root.
        let with_slash = PathBuf::from(format!("{}/", user.display()));
        assert_eq!(discover(&[(&with_slash, "User")])[0].root, library.root.path().join("user"));
    }

    #[test]
    fn falls_back_to_the_folder_or_file_name_when_front_matter_has_none() {
        let library = Library::new();
        let user = library.claude("user");
        write_skill(&user, "my-skill", "no front matter here");
        write_agent(&user, "helper.md", "plain text");
        write_agent(&user, "notes.txt", "---\nname: not an agent\n---");

        let items = discover(&[(&user, "User")]);

        assert!(
            items.iter().any(|i| i.kind == LibraryItemKind::Skill && i.name == "my-skill" && i.description.is_empty())
        );
        assert!(
            items.iter().any(|i| i.kind == LibraryItemKind::Agent && i.name == "helper" && i.description.is_empty())
        );
        assert_eq!(items.len(), 2);
    }

    #[test]
    fn a_folder_without_a_skill_md_is_not_a_skill() {
        let library = Library::new();
        let user = library.claude("user");
        fs::create_dir_all(user.join("skills").join("empty")).unwrap();

        assert!(discover(&[(&user, "User")]).is_empty());
    }

    #[test]
    fn a_later_root_shadows_an_earlier_one_with_the_same_name() {
        let library = Library::new();
        let user = library.claude("user");
        let project = library.claude("project");
        write_skill(&user, "pdf", "---\nname: pdf\ndescription: user copy\n---");
        write_skill(&project, "pdf", "---\nname: pdf\ndescription: project copy\n---");

        let items = discover(&[(&user, "User"), (&project, "proj")]);

        assert_eq!(items.len(), 1);
        assert_eq!(items[0].description, "project copy");
        assert_eq!(items[0].scope, "proj");
    }

    #[test]
    fn a_missing_claude_folder_yields_nothing() {
        let library = Library::new();
        assert!(discover(&[(library.root.path().join("nope").join(".claude"), "User")]).is_empty());
    }

    #[test]
    fn results_are_sorted_by_kind_then_name() {
        let library = Library::new();
        let user = library.claude("user");
        write_skill(&user, "zebra", "---\nname: zebra\n---");
        write_skill(&user, "alpha", "---\nname: alpha\n---");
        write_agent(&user, "beta.md", "---\nname: beta\n---");

        let order: Vec<String> = discover(&[(&user, "User")]).into_iter().map(|i| i.name).collect();

        assert_eq!(order, ["alpha", "zebra", "beta"]);
    }

    #[test]
    fn parses_description_values() {
        for (body, expected) in [
            ("---\ndescription: \"quoted value\"\n---", "quoted value"),
            ("---\ndescription: 'single quoted'\n---", "single quoted"),
            ("---\ndescription: >\n  folded over\n  two lines\n---", "folded over two lines"),
            ("---\ndescription: |\n  literal block\n---", "literal block"),
        ] {
            let (_, description) = parse_front_matter(body);
            assert_eq!(description.as_deref(), Some(expected), "{body}");
        }
    }

    #[test]
    fn no_front_matter_block_parses_as_empty() {
        let (name, description) = parse_front_matter("# Just a heading\nname: not in front matter");
        assert_eq!(name, None);
        assert_eq!(description, None);
    }

    #[test]
    fn files_are_decoded_as_read_all_text_does() {
        assert_eq!(decode(b"\xEF\xBB\xBF---"), "---");
        assert_eq!(decode(b"\xFF\xFEa\x00b\x00"), "ab");
        assert_eq!(parse_front_matter("\r\n---\r\nNAME: 'x'\r\n---").0.as_deref(), Some("x"));
    }
}
