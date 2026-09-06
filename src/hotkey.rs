//! Global low-level keyboard hook (`WH_KEYBOARD_LL`), reporting every
//! non-injected key event system-wide as a raw virtual-key code.
//!
//! ## Why this reports *every* key, not just one fixed constant
//!
//! The first vertical slice of this app hardcoded `VK_RCONTROL` matching
//! directly inside the hook procedure. That stopped being enough once the
//! app needed: a user-configurable push-to-talk key (`AppSettings`), a
//! per-foreground-app override of that key, and a Hold-vs-Toggle mode that
//! changes how down/up edges are interpreted. All of that decision logic
//! now lives in `main.rs` (which already owns the thread-local `STATE` with
//! the current settings), not here -- this module's only job is "tell the
//! caller which virtual-key went down or up, as fast and simply as possible,
//! filtering out synthetic (`SendInput`-injected) events so this app's own
//! text-injection output can never be mistaken for the user pressing keys."
//!
//! ## Not a keylogger
//!
//! Every real keypress system-wide does pass through `keyboard_hook_proc`
//! (a documented, necessary consequence of `WH_KEYBOARD_LL`), but this
//! module never stores, logs, or transmits any key code -- it is compared
//! in-memory, once, against whatever the caller's settings currently say is
//! the active hotkey, and then discarded. `main.rs`'s callbacks must
//! preserve that property: never write raw key codes to `history.json`,
//! `settings.json`, or any log file.
//!
//! ## Threading design
//!
//! Unchanged from the original design: `WH_KEYBOARD_LL` callbacks run
//! synchronously on the thread that installed the hook, as long as that
//! thread keeps pumping messages. `main.rs` installs this hook before
//! handing control to Slint's (winit-backed) event loop on the same thread,
//! which does run a real message pump, so the hook fires correctly there.
//! The hook procedure itself must be a plain `extern "system" fn` (Win32
//! hook procs cannot carry captured closure state), so callbacks are plain
//! function pointers registered once via [`install`].

use std::sync::OnceLock;

use windows::Win32::Foundation::{HINSTANCE, LPARAM, LRESULT, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT, LLKHF_INJECTED, SetWindowsHookExW, UnhookWindowsHookEx,
    WH_KEYBOARD_LL, WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::core::PCWSTR;

/// A raw Win32 virtual-key code (e.g. `VK_RCONTROL.0 as u32`).
pub type RawVk = u32;

static CALLBACKS: OnceLock<(fn(RawVk), fn(RawVk))> = OnceLock::new();

/// Installs the global low-level keyboard hook. `on_down` is invoked for
/// every non-injected key-down (including OS auto-repeat while a key is
/// held -- callers that only want the rising edge must debounce themselves,
/// e.g. by checking their own "already active" state, since which vk counts
/// as "the" hotkey can now change at runtime and this module can no longer
/// own that debounce), `on_up` for every non-injected key-up.
pub fn install(on_down: fn(RawVk), on_up: fn(RawVk)) -> windows::core::Result<HHOOK> {
    let _ = CALLBACKS.set((on_down, on_up));
    unsafe {
        let hinstance: HINSTANCE = GetModuleHandleW(PCWSTR::null())?.into();
        SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard_hook_proc), Some(hinstance), 0)
    }
}

pub fn uninstall(hook: HHOOK) {
    unsafe {
        let _ = UnhookWindowsHookEx(hook);
    }
}

unsafe extern "system" fn keyboard_hook_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32 {
        // SAFETY: for HC_ACTION, lParam always points to a valid KBDLLHOOKSTRUCT
        // for the lifetime of this callback invocation (Win32 contract).
        let kb = unsafe { &*(lparam.0 as *const KBDLLHOOKSTRUCT) };
        if !kb.flags.contains(LLKHF_INJECTED) {
            let msg = wparam.0 as u32;
            if let Some(&(on_down, on_up)) = CALLBACKS.get() {
                if msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN {
                    on_down(kb.vkCode);
                } else if msg == WM_KEYUP || msg == WM_SYSKEYUP {
                    on_up(kb.vkCode);
                }
            }
        }
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}
