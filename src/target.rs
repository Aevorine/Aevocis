//! Foreground-window "target token" capture and re-verification.
//!
//! This is the safety-critical piece carried over by design (not by code copy)
//! from the C++ prototype's `TargetToken` / `SameForegroundTarget()`
//! (`native/src/main.cpp`): we record exactly which window the user was
//! focused on the instant they pressed the push-to-talk hotkey, and we refuse
//! to inject recognized text anywhere else -- even if the user has since
//! switched applications while recognition was running in the background.
//!
//! The window handle is stored as a plain `isize` rather than `windows::HWND`
//! so that `TargetToken` is `Send` and can be handed to the background
//! recognition/injection thread without any unsafe wrapper; `HWND` itself
//! wraps a raw pointer and is not `Send`.

use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId, IsWindow};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct TargetToken {
    hwnd_raw: isize,
    pub pid: u32,
    /// Monotonically increasing counter, bumped on every hotkey-down. Purely a
    /// defense-in-depth disambiguator (mirrors the C++ prototype's
    /// `next_session_`): HWND values can be reused by the OS across window
    /// lifetimes, so this makes two captures of the "same" HWND+PID from two
    /// different dictation attempts distinguishable if ever compared directly.
    pub session: u64,
}

impl TargetToken {
    pub fn hwnd(&self) -> HWND {
        HWND(self.hwnd_raw as *mut core::ffi::c_void)
    }
}

/// Captures the current foreground window as a `TargetToken`. Returns `None`
/// if there is no foreground window at all (e.g. the desktop has focus).
pub fn capture(session: u64) -> Option<TargetToken> {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.is_invalid() {
            return None;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        if pid == 0 || !IsWindow(Some(hwnd)).as_bool() {
            return None;
        }
        Some(TargetToken {
            hwnd_raw: hwnd.0 as isize,
            pid,
            session,
        })
    }
}

/// Re-verifies that the foreground window is still exactly the one captured
/// in `target`. Must be called immediately before injecting text -- this is
/// the non-negotiable guarantee that recognized speech never lands in a
/// window the user did not intend it for.
pub fn matches_foreground(target: &TargetToken) -> bool {
    unsafe {
        let current = GetForegroundWindow();
        let mut pid = 0u32;
        GetWindowThreadProcessId(current, Some(&mut pid));
        current.0 as isize == target.hwnd_raw && pid == target.pid && IsWindow(Some(target.hwnd())).as_bool()
    }
}
