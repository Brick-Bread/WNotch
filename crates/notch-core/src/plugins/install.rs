//! Installs plugins from GitHub: downloads the `.zip` attached to a repository's latest release,
//! checks it, and unpacks it into the plugins folder. Installing does not run or enable anything.
//!
//! The network is behind the [`Http`] trait, so the app supplies a real client and the tests a stub.

use std::fs::{self, File};
use std::io::{self, Read};
use std::path::{Component, Path, PathBuf};

use serde_json::Value;
use sha2::{Digest, Sha256};

use super::manifest::{Manifest, FILE_NAME};
use super::source::{is_newer, Origin, PluginSource, PluginUpdate};
use super::PluginError;

/// The largest download a plugin may be.
pub const MAX_DOWNLOAD_BYTES: u64 = 64 * 1024 * 1024;
/// The most a plugin may unpack to.
pub const MAX_UNPACKED_BYTES: u64 = 256 * 1024 * 1024;
/// The most files a plugin may contain.
pub const MAX_ENTRIES: usize = 2000;

// Work folders inside the plugins folder; the dot keeps the catalog from listing them.
const STAGING_PREFIX: &str = ".installing-";
const UPDATE_PREFIX: &str = ".update-";
const OLD_PREFIX: &str = ".old-";

/// Why a request failed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HttpError {
    /// The server answered with this status.
    Status(u16),
    /// The server could not be reached, or the answer could not be read.
    Other(String),
}

/// The two requests the installer makes. Both follow redirects and send a user agent.
pub trait Http {
    /// Fetches a JSON document as text.
    fn get_text(&self, url: &str) -> Result<String, HttpError>;
    /// Streams a file to `destination`, failing when it grows past `max_bytes`.
    fn download(&self, url: &str, destination: &Path, max_bytes: u64) -> Result<(), HttpError>;
}

/// A repository and the release asset chosen from it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Package {
    pub tag: String,
    pub name: String,
    pub url: String,
    pub size: u64,
    pub digest: Option<String>,
}

/// What an install did. `pending` is true when the plugin was running, so its files could not
/// be replaced: the new version is set aside and takes over the next time Notch starts.
#[derive(Debug, Clone, PartialEq)]
pub struct InstallResult {
    pub manifest: Manifest,
    pub tag: String,
    pub pending: bool,
}

/// What the update check needs to know about an installed plugin.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Installed {
    pub id: String,
    pub name: String,
    pub version: Option<String>,
    /// `owner/repo`, from the install record or the manifest.
    pub repository: Option<String>,
    pub installed_tag: Option<String>,
}

pub struct Installer<'a> {
    http: &'a dyn Http,
    plugins_directory: PathBuf,
    is_running: Box<dyn Fn(&str) -> bool + 'a>,
}

impl<'a> Installer<'a> {
    /// `is_running` says whether the plugin with this id has a live process (such a plugin is
    /// never replaced on the spot).
    pub fn new(http: &'a dyn Http, plugins_directory: impl Into<PathBuf>, is_running: impl Fn(&str) -> bool + 'a) -> Self {
        Self { http, plugins_directory: plugins_directory.into(), is_running: Box::new(is_running) }
    }

    /// Puts updates in place that could not be installed while their plugin was running, and
    /// clears leftovers of interrupted installs. Call before any plugin is started.
    pub fn apply_pending_updates(plugins_directory: &Path) {
        let Ok(entries) = fs::read_dir(plugins_directory) else { return };
        let folders: Vec<PathBuf> = entries.filter_map(Result::ok).map(|e| e.path()).filter(|p| p.is_dir()).collect();
        let name_of = |path: &Path| path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();

        // Whatever cannot be moved stays set aside and is tried again at the next start.
        for update in folders.iter().filter(|p| name_of(p).starts_with(UPDATE_PREFIX)) {
            let target = plugins_directory.join(&name_of(update)[UPDATE_PREFIX.len()..]);
            if target.exists() && fs::remove_dir_all(&target).is_err() {
                continue;
            }
            let _ = fs::rename(update, &target);
        }
        for leftover in folders.iter().filter(|p| {
            let name = name_of(p);
            name.starts_with(STAGING_PREFIX) || name.starts_with(OLD_PREFIX)
        }) {
            let _ = fs::remove_dir_all(leftover);
        }
        if let Ok(entries) = fs::read_dir(plugins_directory) {
            for file in entries.filter_map(Result::ok).map(|e| e.path()) {
                if file.is_file() && name_of(&file).starts_with(STAGING_PREFIX) {
                    let _ = fs::remove_file(file);
                }
            }
        }
    }

    /// Installs the latest release of `source` (`owner/repo` or a link to the repository).
    pub fn install(&self, source: &str) -> Result<InstallResult, PluginError> {
        let repository = PluginSource::parse(source)
            .ok_or_else(|| PluginError::new("Enter a GitHub repository as owner/name, or paste its link."))?;

        let id = unique_suffix();
        let staging = self.plugins_directory.join(format!("{STAGING_PREFIX}{id}"));
        let archive = self.plugins_directory.join(format!("{STAGING_PREFIX}{id}.zip"));
        let result = self.install_into(&repository, &staging, &archive);
        let _ = fs::remove_file(&archive);
        let _ = fs::remove_dir_all(&staging);
        result
    }

    fn install_into(&self, repository: &PluginSource, staging: &Path, archive: &Path) -> Result<InstallResult, PluginError> {
        let package = self.find_package(repository)?;
        fs::create_dir_all(&self.plugins_directory).map_err(install_failed)?;
        self.download(&package, archive, repository)?;

        let manifest = unpack(archive, staging)?;
        Origin::write(staging, repository, &package.tag).map_err(install_failed)?;
        let pending = self.place(staging, &manifest)?;
        Ok(InstallResult { manifest, tag: package.tag, pending })
    }

    /// Asks the plugin's repository whether its latest release is newer than what is installed.
    /// None when it is not (or when the plugin has no known repository, or an unreadable version).
    pub fn check_for_update(&self, plugin: &Installed) -> Result<Option<PluginUpdate>, PluginError> {
        let Some(source) = plugin.repository.as_deref().and_then(PluginSource::parse) else {
            return Ok(None);
        };
        let package = self.find_package(&source)?;
        Ok(is_newer(&package.tag, plugin.installed_tag.as_deref(), plugin.version.as_deref()).then(|| PluginUpdate {
            plugin_id: plugin.id.clone(),
            name: plugin.name.clone(),
            installed_version: plugin.version.clone(),
            latest_tag: package.tag,
            source: source.to_string(),
        }))
    }

    fn find_package(&self, repository: &PluginSource) -> Result<Package, PluginError> {
        let body = self
            .http
            .get_text(&repository.latest_release_url())
            .map_err(|e| github_error(e, repository))?;
        select_package(&body, repository)
    }

    fn download(&self, package: &Package, path: &Path, repository: &PluginSource) -> Result<(), PluginError> {
        if package.size > MAX_DOWNLOAD_BYTES {
            return Err(PluginError::new(format!(
                "{} is larger than the {} MB a plugin may be.",
                package.name,
                MAX_DOWNLOAD_BYTES / (1024 * 1024)
            )));
        }
        self.http
            .download(&package.url, path, MAX_DOWNLOAD_BYTES)
            .map_err(|e| github_error(e, repository))?;

        let size = fs::metadata(path).map_err(install_failed)?.len();
        if size == 0 || (package.size > 0 && size != package.size) {
            return Err(install_failed(format!("the download is {size} bytes, but the release lists {}.", package.size)));
        }
        if let Some(expected) = package.digest.as_deref().and_then(|d| d.strip_prefix("sha256:")) {
            let actual = sha256_hex(path).map_err(install_failed)?;
            if !actual.eq_ignore_ascii_case(expected) {
                return Err(install_failed("the download does not match the digest the release lists for it."));
            }
        }
        Ok(())
    }

    /// Moves the unpacked plugin into the plugins folder. True when that has to wait for a restart.
    fn place(&self, staging: &Path, manifest: &Manifest) -> Result<bool, PluginError> {
        // Replace the copy that is already installed, whatever its folder is called.
        let folder = self.installed_folder_of(&manifest.id).unwrap_or_else(|| manifest.id.clone());
        let target = self.plugins_directory.join(&folder);
        let update = self.plugins_directory.join(format!("{UPDATE_PREFIX}{folder}"));
        if update.exists() {
            fs::remove_dir_all(&update).map_err(install_failed)?;
        }

        if target.exists() {
            // A running plugin keeps its files open, so swapping them under it would mix two versions.
            if (self.is_running)(&manifest.id) {
                fs::rename(staging, &update).map_err(install_failed)?;
                return Ok(true);
            }

            let old = self.plugins_directory.join(format!("{OLD_PREFIX}{folder}"));
            // Moved aside in one step rather than deleted file by file, which could stop half
            // way on a file something has open and leave a broken plugin.
            let moved_aside = (|| {
                if old.exists() {
                    fs::remove_dir_all(&old)?;
                }
                fs::rename(&target, &old)
            })();
            if moved_aside.is_err() {
                fs::rename(staging, &update).map_err(install_failed)?;
                return Ok(true);
            }
            let _ = fs::remove_dir_all(&old);
        }

        fs::rename(staging, &target).map_err(install_failed)?;
        Ok(false)
    }

    fn installed_folder_of(&self, plugin_id: &str) -> Option<String> {
        for entry in fs::read_dir(&self.plugins_directory).ok()?.filter_map(Result::ok) {
            let name = entry.file_name().to_string_lossy().into_owned();
            let manifest_path = entry.path().join(FILE_NAME);
            if name.starts_with('.') || !manifest_path.is_file() {
                continue;
            }
            let matches = fs::read_to_string(&manifest_path)
                .ok()
                .and_then(|text| Manifest::parse(&text).ok())
                .is_some_and(|manifest| manifest.id == plugin_id);
            if matches {
                return Some(name);
            }
        }
        None
    }
}

fn install_failed(reason: impl ToString) -> PluginError {
    PluginError::new(format!("The plugin could not be installed: {}", reason.to_string()))
}

fn github_error(error: HttpError, repository: &PluginSource) -> PluginError {
    PluginError::new(match error {
        HttpError::Status(404) => format!("{repository} was not found on GitHub, or has not published a release."),
        HttpError::Status(403 | 429) => "GitHub is not accepting more requests right now. Try again in a few minutes.".to_owned(),
        HttpError::Status(code) => format!("GitHub could not be reached: it answered {code}."),
        HttpError::Other(message) => format!("GitHub could not be reached: {message}"),
    })
}

fn unique_suffix() -> String {
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};
    static COUNTER: AtomicU32 = AtomicU32::new(0);
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or_default();
    format!("{nanos:x}{:x}{:x}", std::process::id(), COUNTER.fetch_add(1, Ordering::Relaxed))
}

fn sha256_hex(path: &Path) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect())
}

/// Picks the one `.zip` attached to a release. Code is only ever taken from GitHub's own release downloads.
pub fn select_package(release_json: &str, repository: &PluginSource) -> Result<Package, PluginError> {
    let unreadable = || PluginError::new(format!("GitHub's answer about {repository} could not be read."));
    let release: Value = serde_json::from_str(release_json).map_err(|_| unreadable())?;
    let tag = release.get("tag_name").and_then(Value::as_str).unwrap_or_default().to_owned();
    let assets = release.get("assets").and_then(Value::as_array);

    let packages: Vec<Package> = assets
        .into_iter()
        .flatten()
        .map(|asset| Package {
            tag: tag.clone(),
            name: asset.get("name").and_then(Value::as_str).unwrap_or_default().to_owned(),
            url: asset.get("browser_download_url").and_then(Value::as_str).unwrap_or_default().to_owned(),
            size: asset.get("size").and_then(Value::as_u64).unwrap_or_default(),
            digest: asset.get("digest").and_then(Value::as_str).map(str::to_owned),
        })
        .filter(|p| p.name.to_ascii_lowercase().ends_with(".zip") && p.url.starts_with("https://github.com/"))
        .collect();

    match packages.len() {
        1 => Ok(packages.into_iter().next().ok_or_else(unreadable)?),
        0 => Err(PluginError::new(format!("The latest release of {repository} ({tag}) has no .zip attached, so there is no plugin to install."))),
        n => Err(PluginError::new(format!("The latest release of {repository} ({tag}) has {n} .zip files attached; a plugin release must have exactly one."))),
    }
}

/// The relative path an archive entry unpacks to, or none when it would land outside the plugin's folder.
fn safe_relative(name: &str) -> Option<PathBuf> {
    let name = name.replace('\\', "/");
    if name.starts_with('/') || name.split('/').next().is_some_and(|first| first.contains(':')) {
        return None;
    }
    let mut path = PathBuf::new();
    for part in name.split('/').filter(|p| !p.is_empty() && *p != ".") {
        if part == ".." {
            return None;
        }
        path.push(part);
    }
    path.components().all(|c| matches!(c, Component::Normal(_))).then_some(path)
}

/// Unpacks the plugin folder from the archive into `staging` and returns its manifest.
fn unpack(archive: &Path, staging: &Path) -> Result<Manifest, PluginError> {
    let file = File::open(archive).map_err(install_failed)?;
    let mut zip = zip::ZipArchive::new(file).map_err(install_failed)?;
    if zip.len() > MAX_ENTRIES {
        return Err(PluginError::new("The download unpacks to more than a plugin may be."));
    }

    let mut names = Vec::with_capacity(zip.len());
    let mut total: u64 = 0;
    for index in 0..zip.len() {
        let entry = zip.by_index(index).map_err(install_failed)?;
        total = total.saturating_add(entry.size());
        names.push(entry.name().replace('\\', "/"));
    }

    // The plugin's files are either at the top of the archive or inside its only folder,
    // which is how "zip this folder" and GitHub's own tools tend to pack things.
    let manifests: Vec<&String> = names
        .iter()
        .filter(|n| n.as_str() == FILE_NAME || (n.ends_with(&format!("/{FILE_NAME}")) && n.matches('/').count() == 1))
        .collect();
    let prefix = match manifests.as_slice() {
        [one] => one[..one.len() - FILE_NAME.len()].to_owned(),
        [] => {
            return Err(PluginError::new(format!(
                "The download has no {FILE_NAME} at its top level or in a single folder, so it is not a Notch plugin."
            )))
        }
        _ => {
            return Err(PluginError::new(format!(
                "The download holds more than one plugin; a release must contain exactly one {FILE_NAME}."
            )))
        }
    };
    if total > MAX_UNPACKED_BYTES {
        return Err(PluginError::new("The download unpacks to more than a plugin may be."));
    }

    fs::create_dir_all(staging).map_err(install_failed)?;
    let mut written: u64 = 0;
    for (index, name) in names.iter().enumerate() {
        if !name.starts_with(&prefix) || name.ends_with('/') {
            continue;
        }
        // An entry named "../x" must not be able to write outside the plugin's folder.
        let Some(relative) = safe_relative(&name[prefix.len()..]) else {
            return Err(PluginError::new("The download contains files that point outside the plugin's folder."));
        };
        let mut entry = zip.by_index(index).map_err(install_failed)?;
        let mode = entry.unix_mode();
        if mode.is_some_and(|m| m & 0o170000 == 0o120000) {
            return Err(PluginError::new("The download contains links, which a plugin may not."));
        }

        let destination = staging.join(&relative);
        if let Some(folder) = destination.parent() {
            fs::create_dir_all(folder).map_err(install_failed)?;
        }
        let mut output = File::create(&destination).map_err(install_failed)?;
        // Bounded by what was actually unpacked, not by what the archive claims.
        let limit = MAX_UNPACKED_BYTES.saturating_sub(written) + 1;
        written += io::copy(&mut (&mut entry).take(limit), &mut output).map_err(install_failed)?;
        if written > MAX_UNPACKED_BYTES {
            return Err(PluginError::new("The download unpacks to more than a plugin may be."));
        }
        drop(output);
        make_executable_as_archived(&destination, mode);
    }

    let text = fs::read_to_string(staging.join(FILE_NAME)).map_err(install_failed)?;
    let manifest = Manifest::parse(&text)?;
    if manifest.entry.command.contains('/') {
        let program = staging.join(manifest.entry.command.trim_start_matches("./"));
        if !program.is_file() {
            return Err(PluginError::new(format!(
                "The download does not contain {}, which its {FILE_NAME} names.",
                manifest.entry.command
            )));
        }
    }
    Ok(manifest)
}

/// Keeps the permission bits the archive recorded, so a shipped program stays runnable on Linux.
#[cfg(unix)]
fn make_executable_as_archived(path: &Path, mode: Option<u32>) {
    use std::os::unix::fs::PermissionsExt;
    if let Some(mode) = mode {
        let _ = fs::set_permissions(path, fs::Permissions::from_mode(mode & 0o777));
    }
}

#[cfg(not(unix))]
fn make_executable_as_archived(_path: &Path, _mode: Option<u32>) {}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::io::Write;

    use super::*;
    use crate::plugins::store::testing::TempDir;

    const DOWNLOAD: &str = "https://github.com/acme/hello/releases/download/v1.0.0/hello.zip";
    const RELEASE_URL: &str = "https://api.github.com/repos/acme/hello/releases/latest";

    enum Reply {
        Text(String),
        Bytes(Vec<u8>),
    }

    struct Stub {
        replies: HashMap<String, Reply>,
    }

    impl Http for Stub {
        fn get_text(&self, url: &str) -> Result<String, HttpError> {
            match self.replies.get(url) {
                Some(Reply::Text(text)) => Ok(text.clone()),
                _ => Err(HttpError::Status(404)),
            }
        }

        fn download(&self, url: &str, destination: &Path, _max_bytes: u64) -> Result<(), HttpError> {
            match self.replies.get(url) {
                Some(Reply::Bytes(bytes)) => fs::write(destination, bytes).map_err(|e| HttpError::Other(e.to_string())),
                _ => Err(HttpError::Status(404)),
            }
        }
    }

    fn manifest_json(version: &str) -> String {
        format!(r#"{{ "id": "acme.hello", "name": "Hello", "version": "{version}", "protocol": 1, "entry": ["python", "hello.py"] }}"#)
    }

    fn zip_of(folder: &str, version: &str, extra: Option<&str>) -> Vec<u8> {
        let mut bytes = Vec::new();
        {
            let mut zip = zip::ZipWriter::new(io::Cursor::new(&mut bytes));
            let options = zip::write::SimpleFileOptions::default();
            let mut add = |name: String, content: &str| {
                zip.start_file(name, options).unwrap();
                zip.write_all(content.as_bytes()).unwrap();
            };
            add(format!("{folder}{FILE_NAME}"), &manifest_json(version));
            add(format!("{folder}hello.py"), "print('hi')");
            add(format!("{folder}lib/helper.py"), "# helper");
            if let Some(extra) = extra {
                add(format!("{folder}{extra}"), "should never be written");
            }
            zip.finish().unwrap();
        }
        bytes
    }

    fn digest_of(bytes: &[u8]) -> String {
        format!("sha256:{}", Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect::<String>())
    }

    fn release(zip_len: usize, digest: Option<&str>) -> String {
        let digest = digest.map_or("null".to_owned(), |d| format!("\"{d}\""));
        format!(
            r#"{{ "tag_name": "v1.0.0", "assets": [
                {{ "name": "notes.txt", "browser_download_url": "https://github.com/acme/hello/releases/download/v1.0.0/notes.txt", "size": 3 }},
                {{ "name": "hello.zip", "browser_download_url": "{DOWNLOAD}", "size": {zip_len}, "digest": {digest} }}
            ] }}"#
        )
    }

    fn stub(zip: Vec<u8>, digest: Option<&str>) -> Stub {
        let mut replies = HashMap::new();
        replies.insert(RELEASE_URL.to_owned(), Reply::Text(release(zip.len(), digest)));
        replies.insert(DOWNLOAD.to_owned(), Reply::Bytes(zip));
        Stub { replies }
    }

    fn names(dir: &Path) -> Vec<String> {
        let mut names: Vec<String> = fs::read_dir(dir).unwrap().map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
        names.sort();
        names
    }

    #[test]
    fn installs_the_zip_of_the_latest_release() {
        for folder in ["", "hello-1.0.0/"] {
            let plugins = TempDir::new("install");
            let zip = zip_of(folder, "1.0.0", None);
            let http = stub(zip.clone(), Some(&digest_of(&zip)));

            let result = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap();

            assert_eq!(result.manifest.id, "acme.hello");
            assert_eq!(result.tag, "v1.0.0");
            assert!(!result.pending);
            let installed = plugins.0.join("acme.hello");
            assert!(installed.join(FILE_NAME).is_file());
            assert!(installed.join("hello.py").is_file());
            assert!(installed.join("lib").join("helper.py").is_file());
            assert_eq!(names(&plugins.0), ["acme.hello"]);
        }
    }

    #[test]
    fn replaces_an_installed_plugin_that_is_not_running_whatever_its_folder_is_called() {
        let plugins = TempDir::new("replace");
        let existing = plugins.0.join("copied-by-hand");
        fs::create_dir_all(&existing).unwrap();
        fs::write(existing.join(FILE_NAME), manifest_json("0.9.0")).unwrap();
        fs::write(existing.join("stale.txt"), "from the old version").unwrap();
        let http = stub(zip_of("", "1.0.0", None), None);

        let result = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap();

        assert!(!result.pending);
        assert_eq!(names(&plugins.0), ["copied-by-hand"]);
        let manifest = Manifest::parse(&fs::read_to_string(existing.join(FILE_NAME)).unwrap()).unwrap();
        assert_eq!(manifest.version.as_deref(), Some("1.0.0"));
        assert!(!existing.join("stale.txt").exists());
    }

    #[test]
    fn a_running_plugin_is_replaced_at_the_next_start() {
        let plugins = TempDir::new("pending");
        let existing = plugins.0.join("acme.hello");
        fs::create_dir_all(&existing).unwrap();
        fs::write(existing.join(FILE_NAME), manifest_json("0.9.0")).unwrap();
        let http = stub(zip_of("", "1.0.0", None), None);

        let result = Installer::new(&http, &plugins.0, |id| id == "acme.hello").install("acme/hello").unwrap();

        assert!(result.pending);
        let version = |dir: &Path| Manifest::parse(&fs::read_to_string(dir.join(FILE_NAME)).unwrap()).unwrap().version;
        assert_eq!(version(&existing).as_deref(), Some("0.9.0"));

        Installer::apply_pending_updates(&plugins.0);

        assert_eq!(names(&plugins.0), ["acme.hello"]);
        assert_eq!(version(&existing).as_deref(), Some("1.0.0"));
    }

    #[test]
    fn leftovers_of_interrupted_installs_are_cleared() {
        let plugins = TempDir::new("leftovers");
        fs::create_dir_all(plugins.0.join(".installing-abc")).unwrap();
        fs::create_dir_all(plugins.0.join(".old-acme.hello")).unwrap();
        fs::write(plugins.0.join(".installing-abc.zip"), "partial").unwrap();
        fs::create_dir_all(plugins.0.join("kept")).unwrap();

        Installer::apply_pending_updates(&plugins.0);

        assert_eq!(names(&plugins.0), ["kept"]);
    }

    #[test]
    fn rejects_a_download_that_does_not_match_its_digest() {
        let plugins = TempDir::new("digest");
        let http = stub(zip_of("", "1.0.0", None), Some(&digest_of(b"something else")));

        let error = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap_err();

        assert!(error.0.contains("digest"), "{error}");
        assert!(names(&plugins.0).is_empty());
    }

    #[test]
    fn rejects_a_download_whose_size_differs_from_the_release() {
        let plugins = TempDir::new("size");
        let zip = zip_of("", "1.0.0", None);
        let mut http = stub(zip.clone(), None);
        http.replies.insert(RELEASE_URL.to_owned(), Reply::Text(release(zip.len() + 5, None)));

        let error = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap_err();

        assert!(error.0.contains("bytes"), "{error}");
        assert!(names(&plugins.0).is_empty());
    }

    #[test]
    fn rejects_entries_that_point_outside_the_plugin_folder() {
        for extra in ["../../escaped.txt", "..\\escaped.txt", "/abs.txt"] {
            let plugins = TempDir::new("slip");
            let http = stub(zip_of("", "1.0.0", Some(extra)), None);

            let error = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap_err();

            assert!(error.0.contains("outside"), "{extra}: {error}");
            assert!(names(&plugins.0).is_empty());
            assert!(!plugins.0.parent().unwrap().join("escaped.txt").exists());
        }
    }

    #[test]
    fn rejects_a_zip_that_is_not_a_plugin() {
        let plugins = TempDir::new("notplugin");
        let mut bytes = Vec::new();
        {
            let mut zip = zip::ZipWriter::new(io::Cursor::new(&mut bytes));
            zip.start_file("readme.txt", zip::write::SimpleFileOptions::default()).unwrap();
            zip.write_all(b"no manifest here").unwrap();
            zip.finish().unwrap();
        }
        let http = stub(bytes, None);

        let error = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap_err();

        assert!(error.0.contains("not a Notch plugin"), "{error}");
    }

    #[test]
    fn rejects_a_plugin_whose_program_is_missing() {
        let plugins = TempDir::new("noprogram");
        let mut bytes = Vec::new();
        {
            let mut zip = zip::ZipWriter::new(io::Cursor::new(&mut bytes));
            zip.start_file(FILE_NAME, zip::write::SimpleFileOptions::default()).unwrap();
            zip.write_all(br#"{ "id": "a.b", "name": "B", "protocol": 1, "entry": ["./bin/b"] }"#).unwrap();
            zip.finish().unwrap();
        }
        let http = stub(bytes, None);

        let error = Installer::new(&http, &plugins.0, |_| false).install("acme/hello").unwrap_err();

        assert!(error.0.contains("./bin/b"), "{error}");
    }

    #[test]
    fn explains_a_release_without_a_zip() {
        let plugins = TempDir::new("nozip");
        let mut replies = HashMap::new();
        replies.insert(RELEASE_URL.to_owned(), Reply::Text(r#"{ "tag_name": "v1.0.0", "assets": [] }"#.to_owned()));

        let error = Installer::new(&Stub { replies }, &plugins.0, |_| false).install("acme/hello").unwrap_err();

        assert!(error.0.contains("no .zip"), "{error}");
    }

    #[test]
    fn explains_a_release_with_several_zips() {
        let source = PluginSource::parse("acme/hello").unwrap();
        let json = r#"{ "tag_name": "v1", "assets": [
            { "name": "a.zip", "browser_download_url": "https://github.com/x/a.zip" },
            { "name": "b.ZIP", "browser_download_url": "https://github.com/x/b.zip" } ] }"#;
        assert!(select_package(json, &source).unwrap_err().0.contains("exactly one"));
    }

    #[test]
    fn only_takes_code_from_githubs_own_downloads() {
        let source = PluginSource::parse("acme/hello").unwrap();
        let json = r#"{ "tag_name": "v1", "assets": [
            { "name": "a.zip", "browser_download_url": "https://evil.example/a.zip" } ] }"#;
        assert!(select_package(json, &source).unwrap_err().0.contains("no .zip"));
        assert!(select_package("nonsense", &source).unwrap_err().0.contains("could not be read"));
    }

    #[test]
    fn explains_a_repository_that_does_not_exist_or_is_throttled() {
        let plugins = TempDir::new("missing");
        let none = Stub { replies: HashMap::new() };
        let error = Installer::new(&none, &plugins.0, |_| false).install("acme/hello").unwrap_err();
        assert!(error.0.contains("was not found"), "{error}");

        let source = PluginSource::parse("acme/hello").unwrap();
        assert!(github_error(HttpError::Status(403), &source).0.contains("not accepting more requests"));
        assert!(github_error(HttpError::Other("timed out".into()), &source).0.contains("timed out"));
    }

    #[test]
    fn explains_input_that_is_not_a_repository() {
        let plugins = TempDir::new("badinput");
        let none = Stub { replies: HashMap::new() };
        assert!(Installer::new(&none, &plugins.0, |_| false).install("hello").is_err());
    }

    #[test]
    fn an_installed_plugin_remembers_its_repository_and_release() {
        let plugins = TempDir::new("remember");
        let http = stub(zip_of("", "1.0.0", None), None);
        Installer::new(&http, &plugins.0, |_| false).install("https://github.com/acme/hello").unwrap();

        let origin = Origin::read(&plugins.0.join("acme.hello"));

        assert_eq!(origin, Some(Origin { repository: "acme/hello".into(), tag: "v1.0.0".into() }));
    }

    fn installed(version: &str, tag: Option<&str>, repository: Option<&str>) -> Installed {
        Installed {
            id: "acme.hello".into(),
            name: "Hello".into(),
            version: Some(version.into()),
            repository: repository.map(str::to_owned),
            installed_tag: tag.map(str::to_owned),
        }
    }

    #[test]
    fn finds_an_update_when_the_latest_release_is_newer() {
        let plugins = TempDir::new("update");
        let http = stub(zip_of("", "1.0.0", None), None);
        let installer = Installer::new(&http, &plugins.0, |_| false);

        let update = installer.check_for_update(&installed("0.9.0", Some("v0.9.0"), Some("acme/hello"))).unwrap().unwrap();

        assert_eq!(update.latest_tag, "v1.0.0");
        assert_eq!(update.source, "acme/hello");
        assert_eq!(update.installed_version.as_deref(), Some("0.9.0"));
    }

    #[test]
    fn finds_no_update_when_up_to_date_or_the_repository_is_unknown() {
        let plugins = TempDir::new("noupdate");
        let http = stub(zip_of("", "1.0.0", None), None);
        let installer = Installer::new(&http, &plugins.0, |_| false);

        assert_eq!(installer.check_for_update(&installed("1.0.0", Some("v1.0.0"), Some("acme/hello"))), Ok(None));
        assert_eq!(installer.check_for_update(&installed("0.1.0", Some("v0.1.0"), None)), Ok(None));
    }

    #[test]
    fn a_repository_that_cannot_be_reached_is_reported_to_the_user() {
        let plugins = TempDir::new("unreachable");
        let http = stub(zip_of("", "1.0.0", None), None);
        let installer = Installer::new(&http, &plugins.0, |_| false);

        let error = installer.check_for_update(&installed("0.1.0", None, Some("acme/missing"))).unwrap_err();

        assert!(error.0.contains("acme/missing"), "{error}");
    }

    #[test]
    fn archive_paths_are_checked() {
        assert_eq!(safe_relative("lib/x.py"), Some(PathBuf::from("lib").join("x.py")));
        assert_eq!(safe_relative("./a//b"), Some(PathBuf::from("a").join("b")));
        for bad in ["../x", "a/../../x", "/x", "C:/x", "C:\\x", "a\\..\\..\\x"] {
            assert_eq!(safe_relative(bad), None, "{bad}");
        }
    }
}
