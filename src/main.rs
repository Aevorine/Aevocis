//! First vertical slice of the native Rust dictation app: hold Right Ctrl
//! anywhere on the desktop to dictate into whichever window has focus.
//!
//! Pipeline: hotkey-down captures the current foreground window as a
//! `TargetToken` and starts microphone capture; hotkey-up stops capture and
//! hands the audio to a background thread, which runs it through SenseVoice,
//! re-verifies the foreground window still matches the captured token
//! (non-negotiable safety guarantee -- see `target.rs`), and only then
//! injects the recognized text via `SendInput`. A minimal always-on-top,
//! non-activating Slint overlay shows the current phase.
//!
//! Design reference (not copied): `native/src/main.cpp`.

use std::cell::RefCell;
use std::sync::Arc;
use std::time::Duration;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{ComponentHandle, Weak};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{
    GWL_EXSTYLE, GetWindowLongPtrW, HHOOK, HWND_TOPMOST, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SetWindowLongPtrW,
    SetWindowPos, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
};

use osw_native::audio::AudioCapture;
use osw_native::recognizer::Recognizer;
use osw_native::target::{self, TargetToken};
use osw_native::{hotkey, inject};

slint::slint! {
    export component Overlay inherits Window {
        title: "OpenSuperWhisper";
        width: 320px;
        height: 52px;
        no-frame: true;
        always-on-top: true;
        background: #1a1a20e6;

        in property <string> status-text: "Idle";
        in property <color> dot-color: #556070;

        Rectangle {
            x: 16px;
            y: (parent.height - self.height) / 2;
            width: 14px;
            height: 14px;
            border-radius: 7px;
            background: dot-color;
        }

        Text {
            x: 40px;
            y: 0px;
            width: parent.width - 56px;
            height: parent.height;
            vertical-alignment: center;
            text: status-text;
            color: white;
            font-size: 15px;
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Phase {
    Idle,
    Capturing,
    Recognizing,
}

struct AppState {
    ui_weak: Weak<Overlay>,
    recognizer: Arc<Recognizer>,
    capture: AudioCapture,
    phase: Phase,
    target: Option<TargetToken>,
    next_session: u64,
}

thread_local! {
    static STATE: RefCell<Option<AppState>> = const { RefCell::new(None) };
}

fn idle_color() -> slint::Color {
    slint::Color::from_rgb_u8(0x55, 0x60, 0x70)
}
fn recording_color() -> slint::Color {
    slint::Color::from_rgb_u8(0xe0, 0x43, 0x3f)
}
fn busy_color() -> slint::Color {
    slint::Color::from_rgb_u8(0xf2, 0xa9, 0x3a)
}

fn set_status(ui_weak: &Weak<Overlay>, text: &str, color: slint::Color) {
    if let Some(ui) = ui_weak.upgrade() {
        ui.set_status_text(text.into());
        ui.set_dot_color(color);
    }
}

/// Hotkey-down: capture the foreground window as this dictation's target,
/// then start microphone capture. Ignored if a previous dictation is still
/// being recognized/injected -- mirrors the C++ prototype's phase guard.
fn on_hotkey_down() {
    STATE.with(|state| {
        let mut state = state.borrow_mut();
        let Some(state) = state.as_mut() else { return };
        if state.phase != Phase::Idle {
            return;
        }
        state.next_session += 1;
        let Some(captured_target) = target::capture(state.next_session) else {
            set_status(&state.ui_weak, "No foreground window to dictate into.", idle_color());
            return;
        };
        if let Err(e) = state.capture.start() {
            set_status(&state.ui_weak, &format!("Microphone error: {e}"), idle_color());
            return;
        }
        state.target = Some(captured_target);
        state.phase = Phase::Capturing;
        set_status(&state.ui_weak, "Listening... release Right Ctrl to transcribe", recording_color());
    });
}

/// Hotkey-up: stop capture and, if enough audio was collected, hand it to a
/// background thread for recognition + the safety re-check + injection, so
/// the UI/hook thread is never blocked on recognition latency.
fn on_hotkey_up() {
    type Job = (Arc<Recognizer>, Vec<f32>, TargetToken, Weak<Overlay>);

    let job: Option<Job> = STATE.with(|state| {
        let mut state = state.borrow_mut();
        let state = state.as_mut()?;
        if state.phase != Phase::Capturing {
            return None;
        }
        let samples = state.capture.stop();
        if samples.len() < osw_native::audio::SAMPLE_RATE_OUT as usize / 10 {
            state.phase = Phase::Idle;
            state.target = None;
            set_status(&state.ui_weak, "Recording too short.", idle_color());
            return None;
        }
        let captured_target = state.target.take()?;
        state.phase = Phase::Recognizing;
        set_status(&state.ui_weak, "Recognizing...", busy_color());
        Some((Arc::clone(&state.recognizer), samples, captured_target, state.ui_weak.clone()))
    });

    let Some((recognizer, samples, captured_target, ui_weak)) = job else {
        return;
    };

    std::thread::spawn(move || {
        let text = recognizer
            .recognize(&samples, osw_native::audio::SAMPLE_RATE_OUT as i32)
            .trim()
            .to_string();

        let message = if text.is_empty() {
            "No speech recognised.".to_string()
        } else if !target::matches_foreground(&captured_target) {
            // Non-negotiable safety guarantee: the foreground window changed
            // while recognition was running, so we must NOT inject.
            format!("Draft retained (you changed windows): {text}")
        } else if inject::inject_unicode(&text) {
            format!("Inserted: {text}")
        } else {
            format!("Input was blocked (likely an elevated target): {text}")
        };

        let _ = ui_weak.upgrade_in_event_loop(move |ui| {
            ui.set_status_text(message.into());
            ui.set_dot_color(idle_color());
            STATE.with(|state| {
                if let Some(state) = state.borrow_mut().as_mut() {
                    state.phase = Phase::Idle;
                }
            });
        });
    });
}

/// Applies native Win32 window styles that Slint's cross-platform `Window`
/// element has no equivalent for: `WS_EX_NOACTIVATE` (never steal foreground
/// activation -- the closest available replacement for the C++ prototype's
/// `WM_MOUSEACTIVATE -> MA_NOACTIVATE` handling, without needing to subclass
/// the window procedure Slint owns) and `WS_EX_TOOLWINDOW` (no taskbar entry).
/// Must run after the platform window actually exists, hence the caller
/// defers this to a zero-duration single-shot timer.
fn apply_native_overlay_style(ui: &Overlay) {
    let handle = ui.window().window_handle();
    let Ok(win) = handle.window_handle() else { return };
    let RawWindowHandle::Win32(win32) = win.as_raw() else { return };
    let hwnd = HWND(win32.hwnd.get() as *mut core::ffi::c_void);
    unsafe {
        let ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        let augmented = ex_style | WS_EX_NOACTIVATE.0 as isize | WS_EX_TOOLWINDOW.0 as isize | WS_EX_TOPMOST.0 as isize;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, augmented);
        let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }
}

fn main() {
    let model_dir = osw_native::resolve_model_dir();
    println!("Loading SenseVoice model from {}", model_dir.display());
    let recognizer = match Recognizer::load(&model_dir) {
        Ok(r) => Arc::new(r),
        Err(e) => {
            eprintln!("fatal: {e}");
            eprintln!("Set OSW_SENSEVOICE_MODEL_DIR to a directory containing model.int8.onnx + tokens.txt.");
            std::process::exit(1);
        }
    };
    println!("SenseVoice recognizer ready.");

    let ui = Overlay::new().expect("failed to create overlay window");
    ui.set_status_text("Idle -- hold Right Ctrl to dictate".into());
    ui.set_dot_color(idle_color());

    STATE.with(|state| {
        *state.borrow_mut() = Some(AppState {
            ui_weak: ui.as_weak(),
            recognizer,
            capture: AudioCapture::new(),
            phase: Phase::Idle,
            target: None,
            next_session: 0,
        });
    });

    let hook: HHOOK = hotkey::install(on_hotkey_down, on_hotkey_up).expect("failed to install low-level keyboard hook");
    println!("Push-to-talk hotkey armed: hold Right Ctrl to dictate, release to transcribe + inject.");

    ui.show().expect("failed to show overlay");
    {
        let weak = ui.as_weak();
        slint::Timer::single_shot(Duration::from_millis(0), move || {
            if let Some(ui) = weak.upgrade() {
                apply_native_overlay_style(&ui);
            }
        });
    }

    slint::run_event_loop().expect("event loop error");

    let _ = ui.hide();
    hotkey::uninstall(hook);
}
