//! Lists the running programs. Ported from `src/Notch.Platform/Agents/ProcessScanner.cs`.
//!
//! Names and parents come from a cheap snapshot of the whole process table; the command line,
//! path, start time and folder are asked only of programs that could be an agent (or one of the
//! desktop apps whose sessions are read), and remembered, so a scan every few seconds costs next
//! to nothing. The C# scanner asks WMI's `Win32_Process` for the details; this reads the same
//! values from the process itself, which is much quicker.

use std::collections::HashMap;
use std::os::windows::io::{FromRawHandle, OwnedHandle};

use chrono::{DateTime, Utc};
use notch_core::agents::app_sessions::is_app_program;
use notch_core::agents::catalog::{is_agent_program, is_runtime};
use notch_core::agents::detector::ProcessSnapshot;
use windows::Wdk::System::Threading::{NtQueryInformationProcess, PROCESSINFOCLASS};
use windows::Win32::Foundation::{FILETIME, HANDLE, UNICODE_STRING};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::Threading::{GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

use crate::process;

/// `ProcessCommandLineInformation`: the command line as a `UNICODE_STRING` followed by its text.
const PROCESS_COMMAND_LINE_INFORMATION: PROCESSINFOCLASS = PROCESSINFOCLASS(60);

/// 100 ns ticks from 1601 to 1970.
const TICKS_TO_UNIX_EPOCH: i64 = 116_444_736_000_000_000;

#[derive(Clone)]
struct Detail {
    parent: i32,
    name: String,
    command_line: Option<String>,
    path: Option<String>,
    started_at: Option<DateTime<Utc>>,
    working_directory: Option<String>,
}

/// Remembers the details of the programs it has seen between scans.
#[derive(Default)]
pub struct ProcessScanner {
    details: HashMap<i32, Detail>,
}

impl ProcessScanner {
    pub fn new() -> Self {
        Self::default()
    }

    /// The running programs, or an empty list when Windows would not say.
    pub fn scan(&mut self) -> Vec<ProcessSnapshot> {
        let table = read_table();
        if table.is_empty() {
            return Vec::new();
        }

        // Forget programs that have gone, and ones whose id was reused by something else.
        let live: HashMap<i32, &(i32, i32, String)> = table.iter().map(|entry| (entry.0, entry)).collect();
        self.details.retain(|pid, detail| {
            live.get(pid)
                .is_some_and(|(_, parent, name)| *parent == detail.parent && name.eq_ignore_ascii_case(&detail.name))
        });

        for (pid, parent, name) in &table {
            if !self.details.contains_key(pid) && (is_agent_program(name) || is_runtime(name) || is_app_program(name)) {
                // Remembered even when nothing could be read, so it is not asked every scan.
                self.details.insert(*pid, fetch(*pid, *parent, name));
            }
        }

        table
            .into_iter()
            .map(|(pid, parent, name)| {
                let mut snapshot = ProcessSnapshot::new(pid, parent, name);
                if let Some(detail) = self.details.get(&pid) {
                    snapshot.command_line = detail.command_line.clone();
                    snapshot.executable_path = detail.path.clone();
                    snapshot.started_at = detail.started_at;
                    snapshot.working_directory = detail.working_directory.clone();
                }
                snapshot
            })
            .collect()
    }
}

fn read_table() -> Vec<(i32, i32, String)> {
    let mut table = Vec::new();
    // SAFETY: the snapshot handle is owned and closed on return; `entry` is sized as the API expects.
    unsafe {
        let Ok(snapshot) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else { return table };
        let _close = OwnedHandle::from_raw_handle(snapshot.0);
        let mut entry = PROCESSENTRY32W { dwSize: size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
        let mut ok = Process32FirstW(snapshot, &mut entry).is_ok();
        while ok {
            let length = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
            table.push((
                entry.th32ProcessID as i32,
                entry.th32ParentProcessID as i32,
                String::from_utf16_lossy(&entry.szExeFile[..length]),
            ));
            ok = Process32NextW(snapshot, &mut entry).is_ok();
        }
    }
    table
}

fn fetch(pid: i32, parent: i32, name: &str) -> Detail {
    let mut detail = Detail {
        parent,
        name: name.to_owned(),
        command_line: None,
        path: None,
        started_at: None,
        working_directory: None,
    };

    // SAFETY: the handle is owned and closed when `_close` drops.
    let Ok(handle) = (unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid as u32) }) else {
        // Without details a runtime cannot be told apart, so only programs named for an agent are found.
        return detail;
    };
    let _close = unsafe { OwnedHandle::from_raw_handle(handle.0) };

    detail.command_line = command_line(handle);
    detail.path = image_path(handle);
    detail.started_at = start_time(handle);
    detail.working_directory = process::working_directory(pid as u32);
    detail
}

fn command_line(process: HANDLE) -> Option<String> {
    // SAFETY: a size query, then a read into a u64-aligned buffer of that size; the string points into it.
    unsafe {
        let mut size = 0u32;
        let _ =
            NtQueryInformationProcess(process, PROCESS_COMMAND_LINE_INFORMATION, std::ptr::null_mut(), 0, &mut size);
        if size == 0 {
            return None;
        }
        let mut buffer = vec![0u64; (size as usize).div_ceil(8)];
        NtQueryInformationProcess(
            process,
            PROCESS_COMMAND_LINE_INFORMATION,
            buffer.as_mut_ptr().cast(),
            size,
            &mut size,
        )
        .ok()
        .ok()?;
        let text = &*buffer.as_ptr().cast::<UNICODE_STRING>();
        if text.Buffer.is_null() {
            return None;
        }
        let units = std::slice::from_raw_parts(text.Buffer.0, usize::from(text.Length) / 2);
        Some(String::from_utf16_lossy(units))
    }
}

fn image_path(process: HANDLE) -> Option<String> {
    use windows::Win32::System::Threading::{PROCESS_NAME_WIN32, QueryFullProcessImageNameW};
    let mut buffer = vec![0u16; 1024];
    let mut size = buffer.len() as u32;
    // SAFETY: `buffer` holds `size` characters.
    unsafe {
        QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, windows::core::PWSTR(buffer.as_mut_ptr()), &mut size)
    }
    .ok()?;
    Some(String::from_utf16_lossy(&buffer[..size as usize]))
}

fn start_time(process: HANDLE) -> Option<DateTime<Utc>> {
    let (mut created, mut exited, mut kernel, mut user) =
        (FILETIME::default(), FILETIME::default(), FILETIME::default(), FILETIME::default());
    // SAFETY: four valid out-pointers.
    unsafe { GetProcessTimes(process, &mut created, &mut exited, &mut kernel, &mut user) }.ok()?;
    let ticks = (i64::from(created.dwHighDateTime) << 32 | i64::from(created.dwLowDateTime)) - TICKS_TO_UNIX_EPOCH;
    DateTime::from_timestamp(ticks.div_euclid(10_000_000), (ticks.rem_euclid(10_000_000) * 100) as u32)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_this_program_with_its_parent() {
        let mut scanner = ProcessScanner::new();
        let processes = scanner.scan();
        let me = processes.iter().find(|p| p.pid == std::process::id() as i32).expect("this test is running");
        assert!(me.name.to_lowercase().ends_with(".exe"));
        assert!(processes.iter().any(|p| p.pid == me.parent_pid) || me.parent_pid > 0);
    }

    #[test]
    fn reads_details_of_its_own_process() {
        let handle = unsafe { windows::Win32::System::Threading::GetCurrentProcess() };
        let line = command_line(handle).expect("its own command line");
        assert!(!line.is_empty());
        assert!(image_path(handle).is_some_and(|p| p.to_lowercase().ends_with(".exe")));
        let started = start_time(handle).expect("its own start time");
        assert!(started <= Utc::now());
    }
}
