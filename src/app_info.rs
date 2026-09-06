//! Foreground-window process-name lookup, used to resolve per-app push-to-talk
//! hotkey overrides (`AppSettings::app_specific_hotkeys`). Mirrors the C#
//! reference app's `ActiveWindowInfo.GetActiveProcessName()`.

use windows::Win32::Foundation::{CloseHandle, HANDLE, MAX_PATH};
use windows::Win32::System::Threading::{
    OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

/// Returns the lowercase, `.exe`-stripped process name of whatever window
/// currently has focus (e.g. `"weixin"`, `"code"`), or `None` if there is no
/// foreground window, the process has already exited (this is inherently
/// racy -- called from a hot keyboard-hook path -- so a vanished process is
/// expected, not an error), or the process is elevated/protected and denies
/// even limited-information queries. Callers must treat `None` as
/// "no per-app override available, use the default."
pub fn active_process_name() -> Option<String> {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.is_invalid() {
            return None;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        if pid == 0 {
            return None;
        }
        let handle: HANDLE = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; MAX_PATH as usize];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, windows::core::PWSTR(buf.as_mut_ptr()), &mut len);
        let _ = CloseHandle(handle);
        ok.ok()?;
        let path = String::from_utf16_lossy(&buf[..len as usize]);
        let file_name = path.rsplit(['\\', '/']).next().unwrap_or(&path);
        let stem = file_name.strip_suffix(".exe").unwrap_or(file_name);
        Some(stem.to_lowercase())
    }
}
