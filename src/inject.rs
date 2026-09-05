//! Unicode text injection via `SendInput`.
//!
//! Design mirrors the C++ prototype's `InjectUnicode` (`native/src/main.cpp`):
//! each UTF-16 code unit becomes a synthetic key-down/key-up pair carrying
//! `KEYEVENTF_UNICODE`, sent in batches (SendInput has no hard batch-size
//! limit, but chunking keeps any single call small and bounds worst-case
//! latency), skipping raw control characters and Unicode bidi override/isolate
//! marks that some third-party input hooks mishandle.

use windows::Win32::UI::Input::KeyboardAndMouse::{
    INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, SendInput, VIRTUAL_KEY,
};

const CHUNK_SIZE: usize = 256;

/// Sends `text` as synthetic Unicode keystrokes to whichever window currently
/// has focus. Returns `false` if the OS reports it did not accept every
/// synthesized input (e.g. the foreground process is running elevated and
/// UIPI blocked the injection).
pub fn inject_unicode(text: &str) -> bool {
    let units: Vec<u16> = text.encode_utf16().collect();

    for chunk in units.chunks(CHUNK_SIZE) {
        let mut inputs: Vec<INPUT> = Vec::with_capacity(chunk.len() * 2);
        for &unit in chunk {
            if unit < 0x20 || (0x202A..=0x202E).contains(&unit) || (0x2066..=0x2069).contains(&unit) {
                continue;
            }
            inputs.push(keyboard_input(unit, KEYEVENTF_UNICODE));
            inputs.push(keyboard_input(unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if inputs.is_empty() {
            continue;
        }
        let sent = unsafe { SendInput(&inputs, core::mem::size_of::<INPUT>() as i32) };
        if sent as usize != inputs.len() {
            return false;
        }
    }
    true
}

fn keyboard_input(scan: u16, flags: windows::Win32::UI::Input::KeyboardAndMouse::KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(0),
                wScan: scan,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}
