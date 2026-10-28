//! The few registry values Notch reads and writes, all under `HKEY_CURRENT_USER`, so no admin
//! rights are needed. Ported from `src/Notch.Platform/Display/SystemTheme.cs` and
//! `src/Notch.Platform/Startup/StartupRegistration.cs`.

use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, WIN32_ERROR};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE, REG_SZ, REG_VALUE_TYPE, RRF_RT_REG_DWORD,
    RRF_RT_REG_SZ, RegCloseKey, RegCreateKeyExW, RegDeleteKeyValueW, RegGetValueW, RegSetValueExW,
};
use windows::core::{HSTRING, PCWSTR};

const PERSONALIZE_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const RUN_VALUE: &str = "Notch";

/// True when Windows is set to the light mode for apps. Dark when it cannot be read.
pub fn apps_use_light_theme() -> bool {
    read_dword(PERSONALIZE_KEY, "AppsUseLightTheme").is_some_and(|value| value != 0)
}

/// Whether Notch starts at sign-in, through the current user's Run key.
pub fn starts_with_windows() -> bool {
    read_string(RUN_KEY, RUN_VALUE).is_some_and(|value| !value.is_empty())
}

/// Starts Notch at sign-in, or stops it. `executable` is used when enabling; it is quoted, as the
/// C# app and the installer write it.
pub fn set_starts_with_windows(enabled: bool, executable: &str) -> windows::core::Result<()> {
    if enabled {
        write_string(RUN_KEY, RUN_VALUE, &format!("\"{executable}\""))
    } else {
        // SAFETY: plain strings for the key and value names.
        let result =
            unsafe { RegDeleteKeyValueW(HKEY_CURRENT_USER, &HSTRING::from(RUN_KEY), &HSTRING::from(RUN_VALUE)) };
        if result == ERROR_FILE_NOT_FOUND { Ok(()) } else { result.ok() }
    }
}

pub(crate) fn read_dword(key: &str, value: &str) -> Option<u32> {
    let mut data = 0u32;
    let mut size = size_of::<u32>() as u32;
    // SAFETY: `data` and `size` describe a 4-byte buffer.
    let result = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            &HSTRING::from(key),
            &HSTRING::from(value),
            RRF_RT_REG_DWORD,
            None,
            Some((&raw mut data).cast()),
            Some(&mut size),
        )
    };
    result.is_ok().then_some(data)
}

pub(crate) fn read_string(key: &str, value: &str) -> Option<String> {
    let (key, value) = (HSTRING::from(key), HSTRING::from(value));
    let mut size = 0u32;
    // SAFETY: a size query with no buffer.
    let result = unsafe { RegGetValueW(HKEY_CURRENT_USER, &key, &value, RRF_RT_REG_SZ, None, None, Some(&mut size)) };
    if result.is_err() {
        return None;
    }

    let mut buffer = vec![0u16; (size as usize).div_ceil(2)];
    // SAFETY: `buffer` holds `size` bytes.
    let result = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            &key,
            &value,
            RRF_RT_REG_SZ,
            None,
            Some(buffer.as_mut_ptr().cast()),
            Some(&mut size),
        )
    };
    if result.is_err() {
        return None;
    }
    let length = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    Some(String::from_utf16_lossy(&buffer[..length]))
}

pub(crate) fn write_string(key: &str, value: &str, data: &str) -> windows::core::Result<()> {
    write_value(key, value, REG_SZ, &wide_bytes(data))
}

fn wide_bytes(text: &str) -> Vec<u8> {
    text.encode_utf16().chain(Some(0)).flat_map(u16::to_le_bytes).collect()
}

fn write_value(key: &str, value: &str, kind: REG_VALUE_TYPE, data: &[u8]) -> windows::core::Result<()> {
    let mut handle = HKEY::default();
    // SAFETY: `handle` receives the opened key, closed below.
    unsafe {
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            &HSTRING::from(key),
            None,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_SET_VALUE,
            None,
            &mut handle,
            None,
        )
        .ok()?;
        let result: WIN32_ERROR = RegSetValueExW(handle, &HSTRING::from(value), None, kind, Some(data));
        let _ = RegCloseKey(handle);
        result.ok()
    }
}
