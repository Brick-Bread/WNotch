//! Keeps an installed copy up to date without the user doing anything: checks GitHub for a newer
//! release, downloads its package, and installs it once the app is idle.
//!
//! On Windows the NSIS installer is run silently and restarts the app. On Linux an AppImage
//! replaces itself in place and restarts; a `.deb` install cannot be replaced by the app, so there
//! the settings panel only says that a newer release exists. Development builds never update.

use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use notch_core::activity::{Activity, ActivityTier};
use notch_core::glow::{Glow, GlowColor, GlowPattern};
use notch_core::updates::{
    up_to_date_message, was_attempted_recently, PackageKind, ReleaseInfo, Version, CHECK_INTERVAL, FIRST_CHECK_DELAY,
    IDLE_RETRY_INTERVAL, LATEST_RELEASE_URL, REPOSITORY,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tauri::{AppHandle, Emitter, Listener, Manager, State};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};

use crate::state::Backend;

const ACTIVITY_ID: &str = "update";
/// How soon a check follows a settings save, e.g. right after automatic updates were switched on.
const CHECK_SOON_DELAY: Duration = Duration::from_secs(5);
/// A restart this soon after an attempted install is taken to be the app coming back after the update.
const JUST_UPDATED_WITHIN_SECONDS: i64 = 10 * 60;

/// What the settings panel shows about updates.
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct UpdateStatus {
    /// The last thing worth telling the user; empty before anything happened.
    text: String,
    /// True while a check or download is running.
    busy: bool,
    version: String,
}

#[derive(Default)]
struct Progress {
    pending: Option<(ReleaseInfo, PathBuf)>,
    working: bool,
    text: String,
}

/// The background updater. Cheap to share; register one with Tauri's state.
pub struct Updater {
    app: AppHandle,
    kind: Option<PackageKind>,
    http: ureq::Agent,
    folder: PathBuf,
    progress: Mutex<Progress>,
    timer_running: AtomicBool,
    wake: Mutex<Sender<()>>,
}

/// The version of the running app, from the Cargo workspace.
pub fn current_version() -> Version {
    Version::parse(env!("CARGO_PKG_VERSION")).unwrap_or(Version::new(0, 0, 0))
}

fn now_seconds() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).ok().and_then(|d| i64::try_from(d.as_secs()).ok()).unwrap_or(0)
}

fn appimage_path() -> Option<PathBuf> {
    std::env::var_os("APPIMAGE").map(PathBuf::from).filter(|p| p.is_file())
}

/// Only a copy that a package put in place can be replaced by a newer package: the Windows
/// installer leaves `uninstall.exe` beside the program, an AppImage knows its own path.
fn is_installed_copy(kind: PackageKind) -> bool {
    match kind {
        PackageKind::WindowsInstaller => std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(|dir| dir.join("uninstall.exe")))
            .is_some_and(|uninstaller| uninstaller.is_file()),
        PackageKind::AppImage => appimage_path().is_some(),
        PackageKind::Deb => false,
    }
}

impl Updater {
    /// Starts the checks: the first shortly after start, then every four hours.
    pub fn start(app: &AppHandle) -> Arc<Self> {
        let (wake, wake_receiver) = mpsc::channel();
        let kind = PackageKind::for_platform(std::env::consts::OS, std::env::var_os("APPIMAGE").is_some());
        let http = ureq::AgentBuilder::new()
            .user_agent("Notch-Updater")
            .timeout(Duration::from_secs(10 * 60))
            .build();
        let folder = dirs::data_local_dir().unwrap_or_else(std::env::temp_dir).join("notch").join("updates");

        let updater = Arc::new(Self {
            app: app.clone(),
            kind,
            http,
            folder,
            progress: Mutex::default(),
            timer_running: AtomicBool::new(false),
            wake: Mutex::new(wake),
        });

        updater.clean_up_downloads();
        updater.announce_if_updated();

        let listener = updater.clone();
        app.listen("settings", move |_| listener.check_soon());

        // A running or paused countdown would be lost by a restart.
        let timers = updater.clone();
        app.listen("timer", move |event| {
            #[derive(Deserialize)]
            struct Timer {
                state: String,
            }
            if let Ok(timer) = serde_json::from_str::<Timer>(event.payload()) {
                let counting = ["running", "paused"].iter().any(|s| timer.state.eq_ignore_ascii_case(s));
                timers.timer_running.store(counting, Ordering::Relaxed);
            }
        });

        let worker = updater.clone();
        thread::spawn(move || worker.run(&wake_receiver));
        updater
    }

    /// Brings the next check forward.
    pub fn check_soon(&self) {
        let _ = self.wake.lock().unwrap_or_else(|e| e.into_inner()).send(());
    }

    /// What the settings panel shows right now.
    pub fn status(&self) -> UpdateStatus {
        let progress = self.progress();
        UpdateStatus { text: progress.text.clone(), busy: progress.working, version: current_version().to_string() }
    }

    fn progress(&self) -> MutexGuard<'_, Progress> {
        self.progress.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn set_text(&self, text: impl Into<String>) {
        self.progress().text = text.into();
        self.emit_status();
    }

    fn emit_status(&self) {
        if let Err(e) = self.app.emit("update-status", self.status()) {
            log::warn!("could not emit update-status: {e}");
        }
    }

    /// Restarting would interrupt the user: the notch is open, a terminal session is running or a timer counts down.
    fn is_busy(&self) -> bool {
        let backend = self.app.state::<Backend>();
        backend.window.is_expanded() || backend.terminals.session_count() > 0 || self.timer_running.load(Ordering::Relaxed)
    }

    /// Shows a short confirmation in the pill after the installer restarted the app.
    fn announce_if_updated(&self) {
        let backend = self.app.state::<Backend>();
        let updated = {
            let mut settings = backend.settings();
            let just_updated = settings.last_update_attempt_tag.as_deref().and_then(Version::from_tag) == Some(current_version())
                && settings.last_update_attempt_at.is_some_and(|at| (0..JUST_UPDATED_WITHIN_SECONDS).contains(&(now_seconds() - at)));
            if just_updated {
                settings.last_update_attempt_tag = None;
                settings.last_update_attempt_at = None;
            }
            just_updated.then(|| settings.clone())
        };
        let Some(snapshot) = updated else { return };

        backend.store.save(&snapshot);
        backend.hub.publish(Activity {
            id: ACTIVITY_ID.into(),
            tier: ActivityTier::Transient,
            title: "Notch updated".into(),
            detail: Some(current_version().to_string()),
            glyph: Some("\u{2191}".into()),
            glow: Some(Glow { color: GlowColor::GREEN, pattern: GlowPattern::Flash, strength: 1.0 }),
            lifetime: Some(Duration::from_secs(6)),
            ..Activity::default()
        });
    }

    /// Deletes packages left behind by earlier updates.
    fn clean_up_downloads(&self) {
        if self.folder.is_dir() {
            if let Err(e) = fs::remove_dir_all(&self.folder) {
                log::debug!("could not clean up the update downloads: {e}");
            }
        }
    }

    fn run(&self, wake: &mpsc::Receiver<()>) {
        let mut delay = FIRST_CHECK_DELAY;
        loop {
            match wake.recv_timeout(delay) {
                Ok(()) => delay = CHECK_SOON_DELAY,
                Err(RecvTimeoutError::Timeout) => delay = self.run_automatic(),
                Err(RecvTimeoutError::Disconnected) => return,
            }
        }
    }

    /// One automatic round. Returns how long to wait before the next one.
    fn run_automatic(&self) -> Duration {
        let Some(kind) = self.kind else { return CHECK_INTERVAL };
        if !self.begin_work() {
            return CHECK_INTERVAL;
        }
        let next = self.automatic_round(kind);
        self.end_work();
        next
    }

    fn automatic_round(&self, kind: PackageKind) -> Duration {
        let auto_update = self.app.state::<Backend>().settings().auto_update;
        if !auto_update || !is_installed_copy(kind) {
            self.progress().pending = None;
            return CHECK_INTERVAL;
        }

        let pending = self.progress().pending.clone();
        let (release, package) = match pending {
            Some(pending) => pending,
            None => {
                let release = match self.check(kind) {
                    Ok(Some(release)) if !self.attempted_recently(&release) => release,
                    Ok(_) => return CHECK_INTERVAL,
                    Err(e) => {
                        // Offline, rate limited, or a bad download: forget it and try again at the next check.
                        log::info!("update check failed: {e}");
                        return CHECK_INTERVAL;
                    }
                };
                match self.download(&release, kind) {
                    Ok(package) => {
                        self.progress().pending = Some((release.clone(), package.clone()));
                        (release, package)
                    }
                    Err(e) => {
                        log::info!("update download failed: {e}");
                        return CHECK_INTERVAL;
                    }
                }
            }
        };

        // The download is ready; wait for a moment when restarting interrupts nothing.
        if self.is_busy() {
            return IDLE_RETRY_INTERVAL;
        }
        if let Err(e) = self.install(&release, &package, kind) {
            log::warn!("could not install the update: {e}");
            self.progress().pending = None;
        }
        CHECK_INTERVAL
    }

    /// Updates now, for the "Update Notch" button: ignores the automatic-update setting, the record
    /// of earlier attempts and whether the notch is in use. Blocks until it is done or the app is
    /// about to restart. Returns what to tell the user.
    pub fn force_update(&self) -> String {
        let Some(kind) = self.kind else {
            return "Updates are not available on this system.".into();
        };
        if kind.installs_itself() && !is_installed_copy(kind) {
            return "This copy of Notch was not set up by its installer, so it cannot update itself.".into();
        }
        if !self.begin_work() {
            return "An update is already being downloaded. Try again in a moment.".into();
        }
        self.set_text("Checking GitHub for a newer Notch\u{2026}");
        let text = self.force_update_checked(kind);
        self.end_work();
        self.set_text(text.clone());
        text
    }

    fn force_update_checked(&self, kind: PackageKind) -> String {
        let release = match self.check(kind) {
            Ok(Some(release)) => release,
            Ok(None) => return up_to_date_message(current_version()),
            Err(e) => return format!("Could not update: {e}"),
        };

        if !kind.installs_itself() {
            return format!(
                "Notch {} is available. Install the new .deb from https://github.com/{REPOSITORY}/releases/latest.",
                release.tag
            );
        }

        let pending = self.progress().pending.clone().filter(|(pending, _)| pending.tag == release.tag);
        let package = match pending {
            Some((_, package)) => package,
            None => match self.download(&release, kind) {
                Ok(package) => package,
                Err(e) => {
                    self.progress().pending = None;
                    return format!("Could not update: {e}");
                }
            },
        };
        self.progress().pending = Some((release.clone(), package.clone()));

        if !self.confirm_restart() {
            return format!("Notch {} is downloaded. Press the button again when you are ready to restart.", release.tag);
        }
        match self.install(&release, &package, kind) {
            Ok(()) => format!("Installing Notch {}\u{2026}", release.tag),
            Err(e) => {
                self.progress().pending = None;
                format!("The installer could not be started: {e}")
            }
        }
    }

    /// Asks before closing open terminal sessions; nothing to ask when there are none.
    fn confirm_restart(&self) -> bool {
        if self.app.state::<Backend>().terminals.session_count() == 0 {
            return true;
        }
        self.app
            .dialog()
            .message("Notch has to restart to finish updating, which closes the open terminal sessions. Update now?")
            .title("Notch")
            .kind(MessageDialogKind::Info)
            .buttons(MessageDialogButtons::OkCancelCustom("Update now".into(), "Cancel".into()))
            .blocking_show()
    }

    /// Marks a check or download as running. False when one already is.
    fn begin_work(&self) -> bool {
        let mut progress = self.progress();
        if progress.working {
            return false;
        }
        progress.working = true;
        drop(progress);
        self.emit_status();
        true
    }

    fn end_work(&self) {
        self.progress().working = false;
        self.emit_status();
    }

    fn attempted_recently(&self, release: &ReleaseInfo) -> bool {
        let backend = self.app.state::<Backend>();
        let settings = backend.settings();
        was_attempted_recently(
            settings.last_update_attempt_tag.as_deref(),
            settings.last_update_attempt_at,
            &release.tag,
            now_seconds(),
        )
    }

    /// The latest release if it is newer than the running version.
    fn check(&self, kind: PackageKind) -> Result<Option<ReleaseInfo>, String> {
        let body = self
            .http
            .get(LATEST_RELEASE_URL)
            .set("Accept", "application/vnd.github+json")
            .call()
            .map_err(|e| e.to_string())?
            .into_string()
            .map_err(|e| e.to_string())?;
        Ok(ReleaseInfo::parse(&body, kind).filter(|release| release.is_newer_than(current_version())))
    }

    /// Downloads the package into the updates folder and returns its path, once it matches the
    /// size and SHA-256 digest GitHub lists for it.
    fn download(&self, release: &ReleaseInfo, kind: PackageKind) -> Result<PathBuf, String> {
        let name = match kind {
            PackageKind::WindowsInstaller => format!("Notch-Setup-{}.exe", release.version),
            PackageKind::AppImage => format!("Notch-{}.AppImage", release.version),
            PackageKind::Deb => format!("Notch-{}.deb", release.version),
        };
        let path = self.folder.join(name);
        let partial = path.with_extension("part");

        let result = self.fetch(release, &partial).and_then(|()| fs::rename(&partial, &path).map_err(|e| e.to_string()));
        if result.is_err() {
            let _ = fs::remove_file(&partial);
        }
        result.map(|()| path)
    }

    fn fetch(&self, release: &ReleaseInfo, partial: &Path) -> Result<(), String> {
        fs::create_dir_all(&self.folder).map_err(|e| e.to_string())?;
        let response = self.http.get(&release.package_url).call().map_err(|e| e.to_string())?;
        let mut reader = response.into_reader();
        let mut file = File::create(partial).map_err(|e| e.to_string())?;

        let mut hasher = Sha256::new();
        let mut size = 0_u64;
        let mut buffer = vec![0_u8; 64 * 1024];
        loop {
            let read = reader.read(&mut buffer).map_err(|e| e.to_string())?;
            if read == 0 {
                break;
            }
            hasher.update(&buffer[..read]);
            file.write_all(&buffer[..read]).map_err(|e| e.to_string())?;
            size += read as u64;
        }
        file.flush().map_err(|e| e.to_string())?;

        let digest: String = hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect();
        release.verify(size, &digest).map_err(|e| e.to_string())
    }

    /// Starts the installer and quits so it can replace the files. On `Err` the app is still running.
    fn install(&self, release: &ReleaseInfo, package: &Path, kind: PackageKind) -> Result<(), String> {
        // Recorded first: if this release's installer fails, the app must not retry it on every start.
        {
            let backend = self.app.state::<Backend>();
            let snapshot = {
                let mut settings = backend.settings();
                settings.last_update_attempt_tag = Some(release.tag.clone());
                settings.last_update_attempt_at = Some(now_seconds());
                settings.clone()
            };
            backend.store.save(&snapshot);
        }

        match kind {
            PackageKind::WindowsInstaller => run_windows_installer(package)?,
            PackageKind::AppImage => replace_appimage(package)?,
            PackageKind::Deb => return Err("a .deb has to be installed with the system's package manager".into()),
        }
        self.app.exit(0);
        Ok(())
    }
}

/// `/S` installs silently, `/UPDATE` keeps the user's choices and `/R` starts the app again afterwards.
fn run_windows_installer(installer: &Path) -> Result<(), String> {
    std::process::Command::new(installer).args(["/S", "/UPDATE", "/R"]).spawn().map(drop).map_err(|e| e.to_string())
}

/// Puts the downloaded AppImage where the running one is and starts it a moment after this
/// process has exited (the single-instance check would otherwise stop it).
#[cfg(unix)]
fn replace_appimage(package: &Path) -> Result<(), String> {
    use std::os::unix::fs::PermissionsExt;

    let target = appimage_path().ok_or("the AppImage's own path is unknown")?;
    let mut staged = target.clone().into_os_string();
    staged.push(".update");
    let staged = PathBuf::from(staged);

    // Copied beside the target first, so the final rename stays on one file system and is atomic.
    let replaced = fs::copy(package, &staged)
        .and_then(|_| fs::set_permissions(&staged, fs::Permissions::from_mode(0o755)))
        .and_then(|()| fs::rename(&staged, &target));
    if let Err(e) = replaced {
        let _ = fs::remove_file(&staged);
        return Err(e.to_string());
    }

    std::process::Command::new("sh")
        .args(["-c", "sleep 1; exec \"$0\""])
        .arg(&target)
        .env_remove("APPIMAGE")
        .env_remove("APPDIR")
        .env_remove("ARGV0")
        .env_remove("OWD")
        .spawn()
        .map(drop)
        .map_err(|e| e.to_string())
}

#[cfg(not(unix))]
fn replace_appimage(_package: &Path) -> Result<(), String> {
    Err("AppImages only exist on Linux".into())
}

/// Checks for a newer Notch and installs it, whatever the automatic-update setting says.
/// Resolves with what to tell the user.
#[tauri::command]
pub async fn check_updates_now(updater: State<'_, Arc<Updater>>) -> Result<String, String> {
    let updater = updater.inner().clone();
    tauri::async_runtime::spawn_blocking(move || updater.force_update()).await.map_err(|e| e.to_string())
}

/// What the settings panel shows about updates, for when it opens between `update-status` events.
#[tauri::command]
pub fn update_status(updater: State<'_, Arc<Updater>>) -> UpdateStatus {
    updater.status()
}
