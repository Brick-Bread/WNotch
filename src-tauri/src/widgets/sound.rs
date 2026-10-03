//! The short alert sound for a finished timer or a new Pomodoro phase.

/// Plays the system's "asterisk" sound without waiting for it. Silent where there is none.
pub fn chime() {
    #[cfg(windows)]
    {
        use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBeep, MB_ICONASTERISK};
        // SAFETY: MessageBeep takes only a sound id.
        unsafe { MessageBeep(MB_ICONASTERISK) };
    }
    #[cfg(target_os = "linux")]
    {
        use std::process::{Command, Stdio};
        // The first player that exists wins; the sound theme is the freedesktop one.
        const PLAYERS: [(&str, &[&str]); 2] = [
            ("canberra-gtk-play", &["-i", "complete"]),
            ("paplay", &["/usr/share/sounds/freedesktop/stereo/complete.oga"]),
        ];
        for (program, args) in PLAYERS {
            let started = Command::new(program).args(args).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn();
            if let Ok(mut child) = started {
                // Reap it in the background so no zombie is left.
                std::thread::spawn(move || {
                    let _ = child.wait();
                });
                return;
            }
        }
        log::debug!("no sound player found for the timer chime");
    }
}
