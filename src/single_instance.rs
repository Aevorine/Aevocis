//! One process per interactive Windows session.

use std::io;

use windows::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, HWND};
use windows::Win32::System::Threading::CreateMutexW;
use windows::Win32::UI::WindowsAndMessaging::{
    FindWindowW, SW_SHOW, SetForegroundWindow, ShowWindow,
};
use windows::core::w;

const MUTEX_NAME: windows::core::PCWSTR = w!("Local\\Aevocis.SingleInstance");

pub struct SingleInstance {
    handle: HANDLE,
}

/// Acquires the per-user-session instance lock. `Ok(None)` means another
/// Aevocis process already owns it.
pub fn acquire() -> io::Result<Option<SingleInstance>> {
    let handle = unsafe { CreateMutexW(None, false, MUTEX_NAME) }
        .map_err(|error| io::Error::other(error.to_string()))?;
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        unsafe {
            let _ = CloseHandle(handle);
        }
        return Ok(None);
    }
    Ok(Some(SingleInstance { handle }))
}

/// Gives a second invocation a useful result instead of silently exiting.
pub fn show_existing() {
    let hwnd: HWND = unsafe { FindWindowW(None, w!("Aevocis")).unwrap_or_default() };
    if hwnd.is_invalid() {
        return;
    }
    unsafe {
        let _ = ShowWindow(hwnd, SW_SHOW);
        let _ = SetForegroundWindow(hwnd);
    }
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.handle);
        }
    }
}
