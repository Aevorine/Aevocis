//! Global show/hide hotkey via `RegisterHotKey`, deliberately independent of
//! the push-to-talk `WH_KEYBOARD_LL` hook in `hotkey.rs`.
//!
//! `RegisterHotKey` is a *different, higher-level* Win32 mechanism than the
//! low-level keyboard hook: instead of us inspecting every keystroke
//! ourselves, the OS itself intercepts one specific key combination
//! system-wide and delivers it as a single `WM_HOTKEY` message. That message
//! is only ever delivered to the exact `HWND` passed to `RegisterHotKey` (or,
//! if `None`, posted as a thread message with no target window -- which a
//! toolkit's opaque message loop, like winit's here, would just swallow via
//! `DispatchMessage` since there is no window to route it to). So this module
//! creates a tiny invisible window purely to be that delivery target; it is
//! never shown and has no visual role of its own.
//!
//! Threading mirrors `hotkey.rs`: `RegisterHotKey` and the window it targets
//! must live on the same thread that pumps messages. `main.rs` creates this
//! window and registers the hotkey before handing control to
//! `slint::run_event_loop()` on that same thread, and winit's Win32 backend
//! runs a real `GetMessage`/`DispatchMessage` pump, so `WM_HOTKEY` reaches our
//! window procedure exactly like the existing low-level hook already gets
//! driven from that same pump -- `DispatchMessage` routes a window-targeted
//! message to that window's registered procedure regardless of which
//! subsystem created the window or owns the loop.

use std::mem::size_of;
use std::sync::OnceLock;

use windows::Win32::Foundation::{ERROR_CLASS_ALREADY_EXISTS, GetLastError, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::KeyboardAndMouse::{HOT_KEY_MODIFIERS, RegisterHotKey, UnregisterHotKey};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow, RegisterClassExW, WINDOW_EX_STYLE,
    WM_HOTKEY, WNDCLASSEXW, WS_OVERLAPPED,
};
use windows::core::{HRESULT, PCWSTR, w};

/// Only one hotkey is ever registered per process by this module, so a fixed
/// id is fine -- `RegisterHotKey`'s id just needs to be unique per-window.
const HOTKEY_ID: i32 = 1;

/// Set once by `register`, read from the window procedure. A plain function
/// pointer (not a closure) for the same reason `hotkey.rs`'s `CALLBACKS` is:
/// Win32 window procedures cannot carry captured state.
static CALLBACK: OnceLock<fn()> = OnceLock::new();

/// Owns the hidden target window + hotkey registration. Dropping it
/// unregisters the hotkey and destroys the window.
pub struct ShowHideHotkey {
    hwnd: HWND,
}

impl ShowHideHotkey {
    /// Registers `modifiers`+`vk` as a global hotkey; `on_press` runs every
    /// time it fires (on key-down, per `RegisterHotKey`'s own semantics).
    /// Returns the underlying Win32 error if registration fails, e.g. another
    /// application already owns that exact combination.
    pub fn register(modifiers: HOT_KEY_MODIFIERS, vk: u32, on_press: fn()) -> windows::core::Result<Self> {
        let _ = CALLBACK.set(on_press);
        unsafe {
            let hinstance = GetModuleHandleW(PCWSTR::null())?;
            let class_name = w!("OswShowHideHotkeyTarget");

            let wc = WNDCLASSEXW {
                cbSize: size_of::<WNDCLASSEXW>() as u32,
                style: CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc: Some(wndproc),
                hInstance: hinstance.into(),
                lpszClassName: class_name,
                ..Default::default()
            };
            if RegisterClassExW(&wc) == 0 {
                let error = GetLastError();
                if error != ERROR_CLASS_ALREADY_EXISTS {
                    return Err(windows::core::Error::from_hresult(HRESULT::from_win32(error.0)));
                }
            }

            // No WS_VISIBLE: this window is created purely as a message
            // target for RegisterHotKey and is never shown, so it has no
            // taskbar entry and is never drawn.
            let hwnd = CreateWindowExW(
                WINDOW_EX_STYLE(0),
                class_name,
                class_name,
                WS_OVERLAPPED,
                0,
                0,
                0,
                0,
                None,
                None,
                Some(hinstance.into()),
                None,
            )?;

            if let Err(error) = RegisterHotKey(Some(hwnd), HOTKEY_ID, modifiers, vk) {
                let _ = DestroyWindow(hwnd);
                return Err(error);
            }

            Ok(Self { hwnd })
        }
    }
}

impl Drop for ShowHideHotkey {
    fn drop(&mut self) {
        unsafe {
            let _ = UnregisterHotKey(Some(self.hwnd), HOTKEY_ID);
            let _ = DestroyWindow(self.hwnd);
        }
    }
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if msg == WM_HOTKEY && wparam.0 as i32 == HOTKEY_ID {
        if let Some(cb) = CALLBACK.get() {
            cb();
        }
        return LRESULT(0);
    }
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}
