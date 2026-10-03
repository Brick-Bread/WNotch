//! Finding newer releases: reading GitHub's release object, picking the package for this system,
//! comparing versions, checking a download, and deciding when a failed release may be tried again.
//!
//! No networking or file access happens here; the app does that and asks this module what to trust.

use std::fmt;
use std::time::Duration;

use serde_json::Value;

/// The repository whose releases are the only source of updates.
pub const REPOSITORY: &str = "Brick-Bread/WNotch";

/// GitHub's "latest release" endpoint: never a draft or a pre-release.
pub const LATEST_RELEASE_URL: &str = "https://api.github.com/repos/Brick-Bread/WNotch/releases/latest";

/// An update is a program that gets run unattended, so it is only ever taken from this
/// repository's own release downloads.
const TRUSTED_DOWNLOAD_PREFIX: &str = "https://github.com/Brick-Bread/WNotch/releases/download/";

/// How long after start the first check happens.
pub const FIRST_CHECK_DELAY: Duration = Duration::from_secs(45);
/// How often releases are checked.
pub const CHECK_INTERVAL: Duration = Duration::from_secs(4 * 60 * 60);
/// How often a downloaded update looks for a moment when restarting interrupts nothing.
pub const IDLE_RETRY_INTERVAL: Duration = Duration::from_secs(60);
/// How long a release that was tried is left alone, so a failing installer is not retried in a loop.
pub const RETRY_SAME_RELEASE_AFTER: Duration = Duration::from_secs(24 * 60 * 60);

/// A release number such as 1.2.3. A fourth component is dropped so 1.2.3 and 1.2.3.0 compare equal.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Version {
    pub major: u32,
    pub minor: u32,
    pub patch: u32,
}

impl Version {
    pub const fn new(major: u32, minor: u32, patch: u32) -> Self {
        Self { major, minor, patch }
    }

    /// Reads "1.2.3" or "1.2" (two to four numeric parts).
    pub fn parse(text: &str) -> Option<Self> {
        let parts: Vec<u32> = text.split('.').map(|part| part.parse().ok()).collect::<Option<_>>()?;
        match parts[..] {
            [major, minor] | [major, minor, _] | [major, minor, _, _] => {
                Some(Self::new(major, minor, parts.get(2).copied().unwrap_or(0)))
            }
            _ => None,
        }
    }

    /// Reads "v1.2.3" or "1.2.3"; a pre-release suffix such as "-beta" is ignored.
    pub fn from_tag(tag: &str) -> Option<Self> {
        let text = tag.trim().trim_start_matches(['v', 'V']);
        let end = text.find(['-', '+']).unwrap_or(text.len());
        Self::parse(&text[..end])
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)
    }
}

/// Which kind of file updates this installation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PackageKind {
    /// `Notch-Setup-<version>.exe`, run silently.
    WindowsInstaller,
    /// `Notch-<version>-x86_64.AppImage`, which replaces the running AppImage.
    AppImage,
    /// `Notch-<version>-amd64.deb`, which only the system's package manager may install; Notch just says it exists.
    Deb,
}

impl PackageKind {
    /// The package for an operating system (`std::env::consts::OS`) and whether the app runs from an AppImage.
    /// `None` where no package exists.
    pub fn for_platform(os: &str, running_as_appimage: bool) -> Option<Self> {
        match os {
            "windows" => Some(Self::WindowsInstaller),
            "linux" if running_as_appimage => Some(Self::AppImage),
            "linux" => Some(Self::Deb),
            _ => None,
        }
    }

    /// True when the app can replace itself with this kind of package.
    pub fn installs_itself(self) -> bool {
        !matches!(self, Self::Deb)
    }

    /// Whether a release asset of this name is this kind of package.
    pub fn matches(self, name: &str) -> bool {
        let name = name.to_ascii_lowercase();
        match self {
            Self::WindowsInstaller => name.starts_with("notch-setup-") && name.ends_with(".exe"),
            Self::AppImage => name.starts_with("notch-") && name.ends_with(".appimage"),
            Self::Deb => name.starts_with("notch-") && name.ends_with(".deb"),
        }
    }
}

/// A release and the package to update to.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ReleaseInfo {
    pub version: Version,
    pub tag: String,
    pub package_url: String,
    pub package_size: u64,
    /// Lower-case hex digest GitHub reports for the package, or `None` if it reports none.
    pub sha256: Option<String>,
}

/// Why a downloaded package is not trusted.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum VerifyError {
    Size { actual: u64, listed: u64 },
    Digest,
}

impl fmt::Display for VerifyError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Size { actual, listed } => write!(f, "The download is {actual} bytes; the release lists {listed}."),
            Self::Digest => f.write_str("The download does not match the digest the release lists for it."),
        }
    }
}

impl std::error::Error for VerifyError {}

impl ReleaseInfo {
    /// Parses a GitHub release object. `None` when it has no version or no package of `kind` to offer.
    pub fn parse(json: &str, kind: PackageKind) -> Option<Self> {
        let release: Value = serde_json::from_str(json).ok()?;
        let tag = release.get("tag_name")?.as_str()?;
        let version = Version::from_tag(tag)?;

        release.get("assets")?.as_array()?.iter().find_map(|asset| {
            let name = asset.get("name").and_then(Value::as_str).unwrap_or_default();
            let url = asset.get("browser_download_url").and_then(Value::as_str).unwrap_or_default();
            if !kind.matches(name) || !url.starts_with(TRUSTED_DOWNLOAD_PREFIX) {
                return None;
            }

            let sha256 = asset
                .get("digest")
                .and_then(Value::as_str)
                .and_then(|digest| {
                    let (scheme, hex) = digest.split_at_checked(7)?;
                    scheme.eq_ignore_ascii_case("sha256:").then(|| hex.to_ascii_lowercase())
                });

            Some(Self {
                version,
                tag: tag.to_owned(),
                package_url: url.to_owned(),
                package_size: asset.get("size").and_then(Value::as_u64).unwrap_or(0),
                sha256,
            })
        })
    }

    /// True when this release is newer than the running version.
    pub fn is_newer_than(&self, current: Version) -> bool {
        self.version > current
    }

    /// Checks a download's size and SHA-256 (hex) against what the release lists.
    pub fn verify(&self, size: u64, sha256: &str) -> Result<(), VerifyError> {
        if size == 0 || (self.package_size > 0 && size != self.package_size) {
            return Err(VerifyError::Size { actual: size, listed: self.package_size });
        }
        match &self.sha256 {
            Some(listed) if !listed.eq_ignore_ascii_case(sha256) => Err(VerifyError::Digest),
            _ => Ok(()),
        }
    }
}

/// Whether the updater tried `release_tag` so recently that it should leave it alone.
/// Times are seconds since the Unix epoch.
pub fn was_attempted_recently(last_tag: Option<&str>, last_at: Option<i64>, release_tag: &str, now: i64) -> bool {
    let window = i64::try_from(RETRY_SAME_RELEASE_AFTER.as_secs()).unwrap_or(i64::MAX);
    last_tag == Some(release_tag) && last_at.is_some_and(|at| now.saturating_sub(at) < window)
}

/// What the settings panel says when `current` is already the newest release.
pub fn up_to_date_message(current: Version) -> String {
    format!("Notch {current} is the latest version.")
}

#[cfg(test)]
mod tests {
    use super::*;

    const DOWNLOAD: &str = "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe";
    const INSTALLER: &[u8] = b"pretend this is an installer";

    fn release_json(tag: &str, url: &str, size: u64, digest: Option<&str>) -> String {
        let digest = digest.map_or("null".to_owned(), |d| format!("\"{d}\""));
        let base = "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0";
        format!(
            r#"{{ "tag_name": "{tag}", "assets": [
                {{ "name": "notes.txt", "browser_download_url": "{base}/notes.txt", "size": 3 }},
                {{ "name": "Notch-Setup-0.2.0.exe", "browser_download_url": "{url}", "size": {size}, "digest": {digest} }},
                {{ "name": "Notch-0.2.0-x86_64.AppImage", "browser_download_url": "{base}/Notch-0.2.0-x86_64.AppImage", "size": 9 }},
                {{ "name": "Notch-0.2.0-amd64.deb", "browser_download_url": "{base}/Notch-0.2.0-amd64.deb", "size": 8 }}
            ] }}"#
        )
    }

    fn windows(json: &str) -> Option<ReleaseInfo> {
        ReleaseInfo::parse(json, PackageKind::WindowsInstaller)
    }

    fn default_release() -> ReleaseInfo {
        windows(&release_json("v0.2.0", DOWNLOAD, INSTALLER.len() as u64, None)).unwrap()
    }

    #[test]
    fn tags_parse_to_versions() {
        assert_eq!(Version::from_tag("v1.2.3"), Some(Version::new(1, 2, 3)));
        assert_eq!(Version::from_tag("1.2"), Some(Version::new(1, 2, 0)));
        assert_eq!(Version::from_tag("v2.0.0-beta.1"), Some(Version::new(2, 0, 0)));
        assert_eq!(Version::from_tag("1.2.3.4"), Some(Version::new(1, 2, 3)));
    }

    #[test]
    fn nonsense_tags_are_rejected() {
        for tag in ["nightly", "", "v", "1", "1.x.2", "1.2.3.4.5"] {
            assert_eq!(Version::from_tag(tag), None, "{tag}");
        }
    }

    #[test]
    fn versions_compare_numerically() {
        assert!(Version::new(0, 10, 0) > Version::new(0, 9, 9));
        assert_eq!(Version::new(1, 2, 3).to_string(), "1.2.3");
    }

    #[test]
    fn release_picks_the_installer_asset() {
        let release = windows(&release_json("v0.2.0", DOWNLOAD, 28, Some("sha256:ABCDEF"))).unwrap();
        assert_eq!(release.version, Version::new(0, 2, 0));
        assert_eq!(release.package_url, DOWNLOAD);
        assert_eq!(release.package_size, 28);
        assert_eq!(release.sha256.as_deref(), Some("abcdef"));
    }

    #[test]
    fn each_platform_picks_its_own_package() {
        let json = release_json("v0.2.0", DOWNLOAD, 28, None);
        let appimage = ReleaseInfo::parse(&json, PackageKind::AppImage).unwrap();
        let deb = ReleaseInfo::parse(&json, PackageKind::Deb).unwrap();
        assert!(appimage.package_url.ends_with(".AppImage"));
        assert!(deb.package_url.ends_with(".deb"));
        assert!(!PackageKind::WindowsInstaller.matches("Notch-0.2.0-x86_64.AppImage"));
    }

    #[test]
    fn packages_follow_the_platform() {
        assert_eq!(PackageKind::for_platform("windows", false), Some(PackageKind::WindowsInstaller));
        assert_eq!(PackageKind::for_platform("linux", true), Some(PackageKind::AppImage));
        assert_eq!(PackageKind::for_platform("linux", false), Some(PackageKind::Deb));
        assert_eq!(PackageKind::for_platform("macos", false), None);
        assert!(!PackageKind::Deb.installs_itself());
    }

    #[test]
    fn installers_from_anywhere_else_are_ignored() {
        for url in [
            "https://example.com/Notch-Setup-0.2.0.exe",
            "https://github.com/someone-else/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe",
            "http://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe",
        ] {
            assert_eq!(windows(&release_json("v0.2.0", url, 28, None)), None, "{url}");
        }
    }

    #[test]
    fn unusable_releases_parse_to_none() {
        for json in ["{}", "not json", r#"{"tag_name":"v1.0.0","assets":[]}"#, r#"{"tag_name":"nightly","assets":[]}"#] {
            assert_eq!(windows(json), None, "{json}");
        }
    }

    #[test]
    fn only_newer_releases_count() {
        let release = default_release();
        assert!(release.is_newer_than(Version::new(0, 1, 0)));
        assert!(!release.is_newer_than(Version::new(0, 2, 0)));
        assert!(!release.is_newer_than(Version::new(0, 3, 0)));
    }

    #[test]
    fn a_matching_download_is_trusted() {
        let mut release = default_release();
        release.sha256 = Some("abc123".into());
        assert_eq!(release.verify(INSTALLER.len() as u64, "ABC123"), Ok(()));
    }

    #[test]
    fn a_tampered_download_is_rejected() {
        let mut release = default_release();
        release.sha256 = Some("abc123".into());
        assert_eq!(release.verify(INSTALLER.len() as u64, "ffffff"), Err(VerifyError::Digest));
    }

    #[test]
    fn a_truncated_or_empty_download_is_rejected() {
        let release = default_release();
        assert!(matches!(release.verify(5, "x"), Err(VerifyError::Size { actual: 5, .. })));
        assert!(release.verify(0, "x").is_err());
    }

    #[test]
    fn a_release_without_a_digest_is_checked_by_size_only() {
        assert_eq!(default_release().verify(INSTALLER.len() as u64, "anything"), Ok(()));
    }

    #[test]
    fn a_release_is_left_alone_for_a_day_after_an_attempt() {
        let day = 24 * 60 * 60;
        assert!(was_attempted_recently(Some("v1"), Some(1000), "v1", 1000 + day - 1));
        assert!(!was_attempted_recently(Some("v1"), Some(1000), "v1", 1000 + day));
        assert!(!was_attempted_recently(Some("v1"), Some(1000), "v2", 1001));
        assert!(!was_attempted_recently(None, None, "v1", 1001));
        assert!(!was_attempted_recently(Some("v1"), None, "v1", 1001));
    }
}
