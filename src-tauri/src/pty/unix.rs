//! Pseudo terminals on Linux and other Unix systems, on top of `portable-pty`.

use std::io::{self, Write};
use std::sync::{Arc, Mutex, MutexGuard};

use portable_pty::{native_pty_system, Child, ChildKiller, CommandBuilder, MasterPty, PtySize};

use super::{PtyProcess, SpawnRequest, Spawned, Spawner};

/// Starts processes in PTYs.
pub struct PortableSpawner;

impl Spawner for PortableSpawner {
    fn spawn(&self, request: &SpawnRequest) -> Result<Spawned, String> {
        let pair = native_pty_system().openpty(size(request.cols, request.rows)).map_err(|e| e.to_string())?;

        let mut command = CommandBuilder::new(&request.executable);
        command.args(&request.arguments);
        if !request.folder.is_empty() {
            command.cwd(&request.folder);
        }
        // What a terminal emulator says about itself, unless the profile says otherwise.
        command.env("TERM", "xterm-256color");
        command.env("COLORTERM", "truecolor");
        for (name, value) in &request.environment {
            command.env(name, value);
        }

        let child = pair.slave.spawn_command(command).map_err(|e| e.to_string())?;
        drop(pair.slave);
        let reader = pair.master.try_clone_reader().map_err(|e| e.to_string())?;
        let writer = pair.master.take_writer().map_err(|e| e.to_string())?;

        let process = PortableProcess {
            pid: child.process_id(),
            killer: Mutex::new(child.clone_killer()),
            child: Mutex::new(child),
            master: Mutex::new(pair.master),
            writer: Mutex::new(writer),
        };
        Ok(Spawned { process: Arc::new(process), reader })
    }
}

fn size(cols: u16, rows: u16) -> PtySize {
    PtySize { rows: rows.max(1), cols: cols.max(1), pixel_width: 0, pixel_height: 0 }
}

fn lock<T: ?Sized>(mutex: &Mutex<Box<T>>) -> MutexGuard<'_, Box<T>> {
    mutex.lock().unwrap_or_else(|e| e.into_inner())
}

struct PortableProcess {
    pid: Option<u32>,
    killer: Mutex<Box<dyn ChildKiller + Send + Sync>>,
    child: Mutex<Box<dyn Child + Send + Sync>>,
    master: Mutex<Box<dyn MasterPty + Send>>,
    writer: Mutex<Box<dyn Write + Send>>,
}

impl PtyProcess for PortableProcess {
    fn write(&self, data: &[u8]) -> io::Result<()> {
        let mut writer = lock(&self.writer);
        writer.write_all(data)?;
        writer.flush()
    }

    fn resize(&self, cols: u16, rows: u16) {
        let _ = lock(&self.master).resize(size(cols, rows));
    }

    fn kill(&self) {
        // The process leads its own session (the PTY made it so), so its id is also that of the
        // group of everything it started.
        if let Some(group) = self.pid.and_then(|pid| i32::try_from(pid).ok()) {
            // SAFETY: plain system call; a group that no longer exists only makes it fail.
            unsafe { libc::kill(-group, libc::SIGKILL) };
        }
        let _ = lock(&self.killer).kill();
    }

    fn wait(&self) -> i64 {
        // Reading the output ends by itself once the process has gone.
        lock(&self.child).wait().map_or(-1, |status| i64::from(status.exit_code()))
    }
}
