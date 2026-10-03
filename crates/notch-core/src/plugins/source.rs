//! Where a plugin comes from on GitHub, and whether a release is newer than what is installed.

use std::fmt;
use std::fs;
use std::io;
use std::path::Path;

use serde::{Deserialize, Serialize};

use super::manifest::Manifest;

/// A GitHub repository that publishes a plugin as a `.zip` attached to its releases.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginSource {
    pub owner: String,
    pub repository: String,
}

impl PluginSource {
    pub fn latest_release_url(&self) -> String {
        format!("https://api.github.com/repos/{}/{}/releases/latest", self.owner, self.repository)
    }

    /// Reads `owner/repo` or a github.com link to the repository or anything inside it.
    pub fn parse(text: &str) -> Option<PluginSource> {
        let text = text.trim();
        let rest = strip_github_prefix(text).unwrap_or(text);

        let (owner, rest) = rest.split_once('/')?;
        let mut owner_chars = owner.chars();
        let valid_owner = (1..=39).contains(&owner.len())
            && owner_chars.next().is_some_and(|c| c.is_ascii_alphanumeric())
            && owner_chars.all(|c| c.is_ascii_alphanumeric() || c == '-');
        if !valid_owner {
            return None;
        }

        let end = rest.find(|c: char| !(c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-'))).unwrap_or(rest.len());
        let (repository, tail) = rest.split_at(end);
        if repository.is_empty() || repository.len() > 100 || !(tail.is_empty() || tail.starts_with(['/', '?', '#'])) {
            return None;
        }

        let repository = match repository.len().checked_sub(4) {
            Some(cut) if repository[cut..].eq_ignore_ascii_case(".git") => &repository[..cut],
            _ => repository,
        };
        (!repository.trim_matches('.').is_empty()).then(|| PluginSource { owner: owner.to_owned(), repository: repository.to_owned() })
    }
}

/// `https://`, `www.` and `github.com/`, each optional in front of it; only a whole prefix counts.
fn strip_github_prefix(text: &str) -> Option<&str> {
    let rest = text.strip_prefix("https://").or_else(|| text.strip_prefix("http://")).unwrap_or(text);
    let rest = rest.strip_prefix("www.").unwrap_or(rest);
    rest.strip_prefix("github.com/")
}

impl fmt::Display for PluginSource {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}/{}", self.owner, self.repository)
    }
}

/// A newer release of an installed plugin.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginUpdate {
    pub plugin_id: String,
    pub name: String,
    /// What is installed now, for display.
    pub installed_version: Option<String>,
    /// The release tag that would be installed, e.g. `v1.1.0`.
    pub latest_tag: String,
    /// `owner/repo`.
    pub source: String,
}

/// Where a plugin was installed from and which release, remembered in a hidden file inside its
/// folder so Notch can later ask the same repository whether there is something newer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Origin {
    pub repository: String,
    pub tag: String,
}

/// The hidden file inside a plugin's folder that holds its [`Origin`].
pub const ORIGIN_FILE_NAME: &str = ".notch-source";

impl Origin {
    pub fn write(plugin_directory: &Path, source: &PluginSource, tag: &str) -> io::Result<()> {
        let record = Origin { repository: source.to_string(), tag: tag.to_owned() };
        fs::write(plugin_directory.join(ORIGIN_FILE_NAME), serde_json::to_string(&record)?)
    }

    /// What was recorded at install time, or none for a plugin copied in by hand or an unreadable record.
    pub fn read(plugin_directory: &Path) -> Option<Origin> {
        let text = fs::read_to_string(plugin_directory.join(ORIGIN_FILE_NAME)).ok()?;
        serde_json::from_str(&text).ok()
    }

    /// The repository to ask about updates: the one it was installed from, else the one its manifest names.
    pub fn source_of(plugin_directory: &Path, manifest: Option<&Manifest>) -> Option<PluginSource> {
        Self::read(plugin_directory)
            .and_then(|origin| PluginSource::parse(&origin.repository))
            .or_else(|| manifest.and_then(|m| m.repository.as_deref()).and_then(PluginSource::parse))
    }
}

/// Reads `v1.2.3` or `1.2` as a version (two to four numbers); none when it is neither.
/// `1.2.0-beta` and `1.2.0+build` compare by their numeric part.
pub fn parse_version(text: Option<&str>) -> Option<[i64; 4]> {
    let trimmed = text?.trim().trim_start_matches(['v', 'V']);
    let numeric = &trimmed[..trimmed.find(['-', '+']).unwrap_or(trimmed.len())];
    let parts: Vec<&str> = numeric.split('.').collect();
    if !(2..=4).contains(&parts.len()) {
        return None;
    }
    // A missing component sorts below a present one, as .NET's Version does: 1.2 < 1.2.0.
    let mut version = [-1; 4];
    for (slot, part) in version.iter_mut().zip(&parts) {
        if part.is_empty() || !part.chars().all(|c| c.is_ascii_digit()) {
            return None;
        }
        *slot = part.parse().ok()?;
    }
    Some(version)
}

/// Whether a release is newer than what is installed. The version recorded at install time
/// (the tag) wins over the manifest's, which authors may forget to bump.
pub fn is_newer(latest_tag: &str, installed_tag: Option<&str>, manifest_version: Option<&str>) -> bool {
    let latest = parse_version(Some(latest_tag));
    let installed = parse_version(installed_tag).or_else(|| parse_version(manifest_version));
    matches!((latest, installed), (Some(latest), Some(installed)) if latest > installed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::store::testing::TempDir;

    fn source(owner: &str, repository: &str) -> Option<PluginSource> {
        Some(PluginSource { owner: owner.into(), repository: repository.into() })
    }

    #[test]
    fn reads_names_and_links() {
        let cases = [
            ("acme/build-status", "acme", "build-status"),
            ("  acme/build-status  ", "acme", "build-status"),
            ("https://github.com/acme/build-status", "acme", "build-status"),
            ("https://github.com/acme/build-status.git", "acme", "build-status"),
            ("github.com/acme/build.status/releases/tag/v1.0.0", "acme", "build.status"),
            ("https://www.github.com/acme/build-status?tab=readme", "acme", "build-status"),
        ];
        for (text, owner, repository) in cases {
            assert_eq!(PluginSource::parse(text), source(owner, repository), "{text}");
        }
    }

    #[test]
    fn rejects_anything_else() {
        for text in ["", "just-a-name", "https://example.com/acme/build-status", "acme/..", "-acme/repo", "acme/has spaces", "acme/"] {
            assert_eq!(PluginSource::parse(text), None, "{text}");
        }
    }

    #[test]
    fn the_latest_release_url_is_the_api_one() {
        assert_eq!(
            PluginSource::parse("acme/hello").unwrap().latest_release_url(),
            "https://api.github.com/repos/acme/hello/releases/latest"
        );
    }

    #[test]
    fn compares_release_tags_with_the_installed_version() {
        let cases = [
            ("v1.1.0", Some("v1.0.0"), None, true),
            ("v1.0.0", Some("v1.0.0"), None, false),
            ("v1.0.0", Some("v1.1.0"), None, false),
            ("v1.1.0", None, Some("1.0.0"), true),
            ("v1.1.0", Some("v1.1.0"), Some("1.0.0"), false),
            ("v1.1.0-beta", Some("v1.0.0"), None, true),
            ("nightly", Some("v1.0.0"), None, false),
            ("v1.1.0", None, None, false),
            ("v1.10.0", Some("v1.9.0"), None, true),
        ];
        for (latest, tag, manifest, newer) in cases {
            assert_eq!(is_newer(latest, tag, manifest), newer, "{latest} vs {tag:?} / {manifest:?}");
        }
    }

    #[test]
    fn an_installed_plugin_remembers_its_repository_and_release() {
        let dir = TempDir::new("origin");
        Origin::write(&dir.0, &PluginSource::parse("acme/hello").unwrap(), "v1.0.0").unwrap();
        assert_eq!(Origin::read(&dir.0), Some(Origin { repository: "acme/hello".into(), tag: "v1.0.0".into() }));
        assert_eq!(Origin::read(&dir.0.join("missing")), None);
    }

    #[test]
    fn a_manifest_may_name_its_repository() {
        let manifest = Manifest::parse_for(
            r#"{ "id": "a.b", "name": "B", "protocol": 1, "entry": ["b"], "repository": "acme/hello" }"#,
            "linux",
        )
        .unwrap();
        let nowhere = std::env::temp_dir().join("notch-core-no-such-plugin");
        assert_eq!(Origin::source_of(&nowhere, Some(&manifest)), source("acme", "hello"));
        assert_eq!(Origin::source_of(&nowhere, None), None);
    }
}
