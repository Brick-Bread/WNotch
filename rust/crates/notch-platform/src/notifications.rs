//! Reports new Windows notifications. Ported from
//! `src/Notch.Platform/Notifications/NotificationWatcher.cs`.
//!
//! Windows lets an app read notifications only when the user has allowed it under Settings,
//! Privacy and security, Notifications; [`NotificationWatcher::start`] says whether that is so.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use notch_core::shell::{NotificationDiff, NotificationInfo};
use windows::UI::Notifications::Management::{UserNotificationListener, UserNotificationListenerAccessStatus};
use windows::UI::Notifications::{KnownNotificationBindings, NotificationKinds, UserNotification};

const POLL_INTERVAL: Duration = Duration::from_secs(2);

/// Polls the notifications Windows holds while it exists; dropping it stops.
pub struct NotificationWatcher {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl NotificationWatcher {
    /// Starts watching; `received` is called on a background thread for each new notification.
    /// `Err` holds what the user needs to know when Windows says no. Call from the UI thread:
    /// Windows asks the user for access from there the first time.
    pub fn start(received: impl Fn(NotificationInfo) + Send + 'static) -> Result<Self, String> {
        let listener = access().map_err(|e| format!("Windows would not let Notch read notifications: {}", e.message()))?
            .ok_or_else(|| {
                "Windows has not allowed Notch to read notifications. Turn it on under Settings > Privacy & security > Notifications."
                    .to_owned()
            })?;

        let stop = Arc::new(AtomicBool::new(false));
        let flag = Arc::clone(&stop);
        let thread = thread::Builder::new()
            .name("notifications".into())
            .spawn(move || poll(&listener, &flag, received))
            .map_err(|e| e.to_string())?;
        Ok(Self { stop, thread: Some(thread) })
    }
}

impl Drop for NotificationWatcher {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// The listener when access is allowed, asking the user if it has not been decided yet.
fn access() -> windows::core::Result<Option<UserNotificationListener>> {
    let listener = UserNotificationListener::Current()?;
    let mut status = listener.GetAccessStatus()?;
    if status != UserNotificationListenerAccessStatus::Allowed {
        status = listener.RequestAccessAsync()?.join()?;
    }
    Ok((status == UserNotificationListenerAccessStatus::Allowed).then_some(listener))
}

fn poll(listener: &UserNotificationListener, stop: &AtomicBool, received: impl Fn(NotificationInfo)) {
    // The first poll only takes note of what is there, so the action centre is not replayed.
    let mut diff = NotificationDiff::new();
    while !stop.load(Ordering::Relaxed) {
        // Windows may refuse for a moment (the session is locked, say); try again at the next tick.
        if let Ok(toasts) = listener.GetNotificationsAsync(NotificationKinds::Toast).and_then(|o| o.join()) {
            let current: Vec<NotificationInfo> = toasts.into_iter().filter_map(|n| describe(&n)).collect();
            for fresh in diff.fresh(&current) {
                received(fresh.clone());
            }
        }

        let until = Instant::now() + POLL_INTERVAL;
        while !stop.load(Ordering::Relaxed) && Instant::now() < until {
            thread::sleep(Duration::from_millis(50));
        }
    }
}

fn describe(notification: &UserNotification) -> Option<NotificationInfo> {
    let app = notification
        .AppInfo()
        .and_then(|info| info.DisplayInfo())
        .and_then(|display| display.DisplayName())
        .map(|name| name.to_string())
        .unwrap_or_default();
    let texts: Vec<String> = notification
        .Notification()
        .and_then(|n| n.Visual())
        .and_then(|visual| visual.GetBinding(&KnownNotificationBindings::ToastGeneric()?))
        .and_then(|binding| binding.GetTextElements())
        .map(|elements| elements.into_iter().filter_map(|t| t.Text().ok()).map(|t| t.to_string()).collect())
        .unwrap_or_default();

    Some(NotificationInfo {
        id: i64::from(notification.Id().ok()?),
        app,
        title: texts.first().cloned().unwrap_or_default(),
        body: (texts.len() > 1).then(|| texts[1..].join(" ")),
    })
}
