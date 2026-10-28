//! Reads the project's GitHub releases to find installers to update to. Ported from
//! `src/Notch.Core/Updates/ReleaseFeed.cs`.

use std::fmt;

use serde_json::{Map, Value};

use crate::shell::hotkey::parse_integer;

pub const REPOSITORY: &str = "Brick-Bread/WNotch";

/// GitHub's "latest release" endpoint: never a draft or a pre-release.
pub const LATEST_RELEASE_URL: &str = "https://api.github.com/repos/Brick-Bread/WNotch/releases/latest";

// An update is an executable that gets run unattended, so it is only ever taken from this
// repository's own release downloads.
const TRUSTED_DOWNLOAD_PREFIX: &str = "https://github.com/Brick-Bread/WNotch/releases/download/";

// The release carries the same installer twice. People download Notch-Setup-*, which the site's
// download counter counts; the app downloads Notch-Update-*, so that updates do not count as new
// downloads. Releases before the second file existed only have Notch-Setup-*.
const UPDATE_PREFIX: &str = "Notch-Update-";
const INSTALLER_PREFIX: &str = "Notch-Setup-";

/// A `System.Version` with its fourth component dropped, so 1.2.3 and 1.2.3.0 compare equal and
/// a missing third one counts as 0.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Version {
    pub major: i32,
    pub minor: i32,
    pub build: i32,
}

impl Version {
    pub const fn new(major: i32, minor: i32, build: i32) -> Self {
        Self { major, minor, build }
    }

    /// `Version.TryParse`, normalised: two to four dot-separated numbers of at least 0.
    pub fn parse(text: &str) -> Option<Self> {
        let parts: Vec<&str> = text.split('.').collect();
        if !(2..=4).contains(&parts.len()) {
            return None;
        }

        let mut numbers = Vec::with_capacity(parts.len());
        for part in parts {
            numbers.push(parse_integer(part).filter(|&number| number >= 0)?);
        }
        Some(Self::new(numbers[0], numbers[1], numbers.get(2).copied().unwrap_or(0)))
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.build)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseInfo {
    pub version: Version,
    pub tag: String,
    pub installer_url: String,
    pub installer_size: i64,
    /// Lower-case hex digest GitHub reports for the installer, or `None` if it reports none.
    pub sha256: Option<String>,
    /// The installer's `.sig` file on the same release, or `None` when it has none.
    pub signature_url: Option<String>,
}

/// Parses a GitHub release object. `None` when it has no version or no installer to offer.
///
/// Where C# would throw reading the JSON (a value of the wrong type, say) and catch it, this
/// returns `None` at the same point, so a release reads the same in both.
pub fn parse(json: &str) -> Option<ReleaseInfo> {
    let release: Value = serde_json::from_str(json).ok()?;
    let tag = match &release {
        Value::Null => None,
        Value::Object(release) => text(release.get("tag_name"))?,
        // A JsonNode that is not an object throws when indexed by name.
        _ => return None,
    };
    let tag = tag?;
    let version = try_parse_version(tag)?;
    let Some(Value::Array(assets)) = release.get("assets") else {
        return None;
    };

    for prefix in [UPDATE_PREFIX, INSTALLER_PREFIX] {
        for asset in assets {
            let (name, url) = name_and_url(asset)?;
            if !starts_with_ignore_case(name, prefix)
                || !ends_with_ignore_case(name, ".exe")
                || !url.starts_with(TRUSTED_DOWNLOAD_PREFIX)
            {
                continue;
            }

            let asset = asset.as_object()?;
            let digest = text(asset.get("digest"))?;
            let sha256 = digest
                .filter(|digest| starts_with_ignore_case(digest, "sha256:"))
                .map(|digest| digest["sha256:".len()..].to_lowercase());

            // The signature is a small file next to the installer, named after it.
            let signature_name = format!("{name}.sig");
            let mut signature_url = None;
            for other in assets {
                let (other_name, other_url) = name_and_url(other)?;
                if other_name.eq_ignore_ascii_case(&signature_name) && other_url.starts_with(TRUSTED_DOWNLOAD_PREFIX) {
                    signature_url = Some(other_url.to_owned());
                    break;
                }
            }

            let installer_size = match asset.get("size") {
                None | Some(Value::Null) => 0,
                Some(size) => size.as_i64()?,
            };

            return Some(ReleaseInfo {
                version,
                tag: tag.to_owned(),
                installer_url: url.to_owned(),
                installer_size,
                sha256,
                signature_url,
            });
        }
    }

    None
}

/// Reads "v1.2.3" or "1.2.3"; a pre-release suffix such as "-beta" is ignored.
pub fn try_parse_version(tag: &str) -> Option<Version> {
    let text = tag.trim().trim_start_matches(['v', 'V']);
    let text = text.find(['-', '+']).map_or(text, |suffix| &text[..suffix]);
    Version::parse(text)
}

pub fn is_newer(release: &ReleaseInfo, current: Version) -> bool {
    release.version > current
}

/// `(string?)node`: `Some(None)` for a missing or null value, `None` where C# throws.
fn text(value: Option<&Value>) -> Option<Option<&str>> {
    match value {
        None | Some(Value::Null) => Some(None),
        Some(Value::String(text)) => Some(Some(text)),
        Some(_) => None,
    }
}

/// An asset's name and download link, empty when missing; `None` where C# throws.
fn name_and_url(asset: &Value) -> Option<(&str, &str)> {
    let asset: &Map<String, Value> = match asset {
        Value::Null => return Some(("", "")),
        Value::Object(asset) => asset,
        _ => return None,
    };
    Some((text(asset.get("name"))?.unwrap_or(""), text(asset.get("browser_download_url"))?.unwrap_or("")))
}

fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    text.len() >= prefix.len() && text.as_bytes()[..prefix.len()].eq_ignore_ascii_case(prefix.as_bytes())
}

fn ends_with_ignore_case(text: &str, suffix: &str) -> bool {
    text.len() >= suffix.len() && text.as_bytes()[text.len() - suffix.len()..].eq_ignore_ascii_case(suffix.as_bytes())
}
