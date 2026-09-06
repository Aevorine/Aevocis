//! One-shot "capture the next key the user presses" mechanism, used by the
//! Settings window's hotkey-rebind buttons.
//!
//! Lives as a thread-local (not a cross-thread `Mutex`) because everything
//! that touches it -- the `WH_KEYBOARD_LL` hook callback in `main.rs` and the
//! Settings window's Slint callbacks -- already runs on the single UI thread
//! (see `hotkey.rs`'s threading doc comment); a `Mutex<Box<dyn FnOnce(u32)>>`
//! would additionally require the boxed closure to be `Send`, which is not
//! achievable here since these closures capture Slint `Weak` window handles
//! and `Rc`s, neither of which is `Send`.

use std::cell::RefCell;

use windows::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_CONTROL, VK_MENU, VK_SHIFT};

/// `MOD_CONTROL|MOD_ALT|MOD_SHIFT` bits (as `RegisterHotKey` expects), read
/// live via `GetAsyncKeyState` at the moment a show/hide hotkey capture
/// fires -- lets the user bind e.g. "Ctrl+Alt+H" by literally holding Ctrl+Alt
/// and pressing H, without this module needing to track key-down/up state
/// itself across multiple events.
pub fn current_modifier_flags() -> u32 {
    const MOD_ALT: u32 = 0x0001;
    const MOD_CONTROL: u32 = 0x0002;
    const MOD_SHIFT: u32 = 0x0004;
    let held = |vk: windows::Win32::UI::Input::KeyboardAndMouse::VIRTUAL_KEY| unsafe { (GetAsyncKeyState(vk.0 as i32) as u16 & 0x8000) != 0 };
    let mut flags = 0u32;
    if held(VK_CONTROL) {
        flags |= MOD_CONTROL;
    }
    if held(VK_MENU) {
        flags |= MOD_ALT;
    }
    if held(VK_SHIFT) {
        flags |= MOD_SHIFT;
    }
    flags
}

/// Human-readable label for a `RegisterHotKey`-style modifiers+vk combo, e.g.
/// "Ctrl+Alt+H".
pub fn combo_label(modifiers: u32, vk: u32) -> String {
    let mut parts = Vec::new();
    if modifiers & 0x0002 != 0 {
        parts.push("Ctrl".to_string());
    }
    if modifiers & 0x0001 != 0 {
        parts.push("Alt".to_string());
    }
    if modifiers & 0x0004 != 0 {
        parts.push("Shift".to_string());
    }
    parts.push(vk_label(vk));
    parts.join("+")
}

thread_local! {
    static ARMED_CALLBACK: RefCell<Option<Box<dyn FnOnce(u32)>>> = const { RefCell::new(None) };
}

/// Arms capture mode: the very next raw key-down `main.rs`'s hook callback
/// sees will be consumed by `try_consume` below instead of being dispatched
/// as a normal push-to-talk/show-hide event, and `on_captured` will be
/// called with that key's virtual-key code.
pub fn arm(on_captured: impl FnOnce(u32) + 'static) {
    ARMED_CALLBACK.with(|c| *c.borrow_mut() = Some(Box::new(on_captured)));
}

/// Call from `main.rs`'s raw key-down handler before any other dispatch.
/// Returns `true` if this key event was consumed as a capture (the caller
/// must not process it as a normal hotkey event in that case).
pub fn try_consume(vk: u32) -> bool {
    let cb = ARMED_CALLBACK.with(|c| c.borrow_mut().take());
    match cb {
        Some(cb) => {
            cb(vk);
            true
        }
        None => false,
    }
}

/// Best-effort human-readable label for a raw virtual-key code, for display
/// in the Settings window (e.g. "右 Ctrl", "F9", "A"). Falls back to a hex
/// VK dump for anything not in this small named table, rather than pretending
/// to support the full VK_* space.
pub fn vk_label(vk: u32) -> String {
    match vk {
        0xA0 => "左 Shift".to_string(),
        0xA1 => "右 Shift".to_string(),
        0xA2 => "左 Ctrl".to_string(),
        0xA3 => "右 Ctrl".to_string(),
        0xA4 => "左 Alt".to_string(),
        0xA5 => "右 Alt".to_string(),
        0x14 => "CapsLock".to_string(),
        0x09 => "Tab".to_string(),
        0x20 => "空格".to_string(),
        0x0D => "Enter".to_string(),
        0x1B => "Esc".to_string(),
        n @ 0x30..=0x39 => (((n - 0x30) as u8 + b'0') as char).to_string(),
        n @ 0x41..=0x5A => (((n - 0x41) as u8 + b'A') as char).to_string(),
        n @ 0x70..=0x7B => format!("F{}", n - 0x70 + 1),
        n => format!("VK 0x{n:02X}"),
    }
}

/// Inverse of [`vk_label`] (case-insensitive), for parsing the Settings
/// window's app-specific-hotkeys textbox. Returns `None` for anything not
/// produced by `vk_label` itself (including a malformed `VK 0xNN` string) --
/// callers should silently skip a line that fails to parse rather than error.
pub fn parse_vk_label(label: &str) -> Option<u32> {
    let l = label.trim();
    let upper = l.to_uppercase();
    match upper.as_str() {
        "左 SHIFT" | "LSHIFT" => return Some(0xA0),
        "右 SHIFT" | "RSHIFT" => return Some(0xA1),
        "左 CTRL" | "LCTRL" => return Some(0xA2),
        "右 CTRL" | "RCTRL" => return Some(0xA3),
        "左 ALT" | "LALT" => return Some(0xA4),
        "右 ALT" | "RALT" => return Some(0xA5),
        "CAPSLOCK" => return Some(0x14),
        "TAB" => return Some(0x09),
        "空格" | "SPACE" => return Some(0x20),
        "ENTER" => return Some(0x0D),
        "ESC" => return Some(0x1B),
        _ => {}
    }
    if upper.len() == 1 {
        let c = upper.chars().next().unwrap();
        if c.is_ascii_digit() {
            return Some(0x30 + (c as u32 - '0' as u32));
        }
        if c.is_ascii_uppercase() {
            return Some(0x41 + (c as u32 - 'A' as u32));
        }
    }
    if let Some(rest) = upper.strip_prefix('F') {
        if let Ok(n) = rest.parse::<u32>() {
            if (1..=12).contains(&n) {
                return Some(0x70 + n - 1);
            }
        }
    }
    if let Some(rest) = upper.strip_prefix("VK 0X").or_else(|| upper.strip_prefix("0X")) {
        return u32::from_str_radix(rest, 16).ok();
    }
    None
}
