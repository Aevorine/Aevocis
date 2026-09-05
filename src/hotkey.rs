//! Global push-to-talk hotkey via a low-level keyboard hook (`WH_KEYBOARD_LL`).
//!
//! Default key: Right Ctrl (`VK_RCONTROL`), matching the C++ prototype
//! (`native/src/main.cpp`). This is intentionally a constant rather than a
//! configurable binding -- a full hotkey-configuration system is out of scope
//! for this first vertical slice.
//!
//! ## Threading design
//!
//! `WH_KEYBOARD_LL` hook callbacks always run synchronously on the thread that
//! installed the hook (this is a documented Win32 guarantee, not an
//! implementation detail we're relying on informally), *as long as that
//! thread keeps pumping messages*. `main.rs` installs this hook before handing
//! control to Slint's (winit-backed) event loop on the same thread, and
//! winit's Win32 backend does run a real `GetMessage`/`PeekMessage` pump
//! internally, so the hook fires correctly on the UI thread.
//!
//! That means it is safe to react to the hotkey directly inside the hook
//! callback rather than re-deriving the C++ prototype's `PostThreadMessageW`
//! plumbing: we're already on the right thread, so `on_down`/`on_up` can touch
//! UI state (Slint properties) or app state directly without any extra
//! message-routing layer. Heavy work (opening the audio device, running
//! recognition) is still kept off this callback by the caller -- see
//! `main.rs`'s `on_hotkey_down`/`on_hotkey_up`, which only do lightweight
//! state transitions here and hand real work to a background thread.
//!
//! The hook procedure itself must be a plain `extern "system" fn` (Win32 hook
//! procs cannot carry captured closure state), so callbacks are plain
//! zero-argument function pointers registered once via [`install`].

use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};

use windows::Win32::Foundation::{HINSTANCE, LPARAM, LRESULT, WPARAM};
use windows::Win32::UI::Input::KeyboardAndMouse::VK_RCONTROL;
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT, LLKHF_INJECTED, SetWindowsHookExW, UnhookWindowsHookEx,
    WH_KEYBOARD_LL, WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::core::PCWSTR;

static HOTKEY_DOWN: AtomicBool = AtomicBool::new(false);
static CALLBACKS: OnceLock<(fn(), fn())> = OnceLock::new();

/// Installs the global low-level keyboard hook. `on_down` is invoked on the
/// rising edge of the hotkey (auto-repeat while held is filtered out), `on_up`
/// on the falling edge.
pub fn install(on_down: fn(), on_up: fn()) -> windows::core::Result<HHOOK> {
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
        if kb.vkCode == VK_RCONTROL.0 as u32 && !kb.flags.contains(LLKHF_INJECTED) {
            let msg = wparam.0 as u32;
            if let Some(&(on_down, on_up)) = CALLBACKS.get() {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && !HOTKEY_DOWN.swap(true, Ordering::SeqCst) {
                    on_down();
                } else if (msg == WM_KEYUP || msg == WM_SYSKEYUP) && HOTKEY_DOWN.swap(false, Ordering::SeqCst) {
                    on_up();
                }
            }
        }
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}
