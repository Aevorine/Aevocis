//! Full-featured native Rust dictation app: hold (or toggle, per settings) a
//! configurable hotkey anywhere on the desktop to dictate into whichever
//! window has focus.
//!
//! Pipeline: hotkey-down captures the current foreground window as a
//! `TargetToken` and starts microphone capture; hotkey-up stops capture and
//! hands the audio to a background thread for SenseVoice recognition. Once
//! text comes back, the rest of the pipeline (foreground safety re-check,
//! voice-command/macro matching, term-dictionary correction, punctuation
//! fixing, optional draft-confirm, injection, history) all runs back on the
//! UI thread -- everything past "recognize the audio" is either fast pure
//! logic or a Win32 call cheap enough not to need a background thread, and
//! keeping it on one thread avoids ever needing `thread_local STATE` from
//! anywhere but the UI thread.
//!
//! See `SPEC.md`/`TECH_ROADMAP.md` for the full feature inventory and the
//! architecture decisions made while building this out to parity with the
//! C# reference app (`src-reference/`).

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;
use std::time::{Duration, Instant};

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{ComponentHandle, Model, Weak};
use tray_icon::menu::{Menu, MenuEvent, MenuItem};
use tray_icon::{Icon as TrayIcon, MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{HOT_KEY_MODIFIERS, MOD_NOREPEAT};
use windows::Win32::UI::WindowsAndMessaging::{
    GWL_EXSTYLE, GetWindowLongPtrW, HHOOK, HWND_TOPMOST, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SetWindowLongPtrW,
    SetWindowPos, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
};

use osw_native::audio::AudioCapture;
use osw_native::recognizer::Recognizer;
use osw_native::settings::{AppSettings, PushToTalkMode};
use osw_native::show_hide_hotkey::ShowHideHotkey;
use osw_native::target::{self, TargetToken};
use osw_native::voice::{CommandMatch, VoiceCommand, VoiceCommandAction, VoiceMacro};
use osw_native::{
    app_info, audio, autostart, crash_reporter, history, hotkey, hotkey_capture, inject, priority, punctuation,
    settings, settings_window, term_dictionary, term_dictionary_window, update, voice,
};

// Brings in `MainWindow` and `HistoryEntry`, generated at build time by
// `slint_build::compile("ui/main_window.slint")` in `build.rs`. This is a
// second, independent Slint component from the `Overlay` macro below -- the
// two do not interact and neither one's lifecycle affects the other.
slint::include_modules!();

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
    /// Target window captured, microphone open still pending -- see the
    /// `begin_dictation` doc comment for why this state exists.
    Starting,
    Capturing,
    Recognizing,
}

/// Debounce window for Toggle-mode key-downs, guarding against a single
/// physical keypress's electrical double-fire being read as two logical
/// toggles (start-then-immediately-stop). Empirical, mirrors the C# app.
const TOGGLE_DEBOUNCE: Duration = Duration::from_millis(300);

struct AppState {
    ui_weak: Weak<Overlay>,
    main_win_weak: Weak<MainWindow>,
    recognizer: Arc<Recognizer>,
    capture: AudioCapture,
    phase: Phase,
    target: Option<TargetToken>,
    next_session: u64,
    history_model: Rc<slint::VecModel<HistoryEntry>>,

    settings: AppSettings,
    term_corrections: Vec<term_dictionary::TermCorrection>,
    voice_commands: Vec<VoiceCommand>,
    voice_macros: Vec<VoiceMacro>,

    /// The raw VK that started the current session, latched at press-down so
    /// Hold-mode's key-up still matches even if the foreground app (and
    /// therefore the per-app-resolved "effective" VK) changed mid-hold.
    active_matched_vk: Option<u32>,
    toggle_active: bool,
    last_toggle_at: Instant,
    /// UTF-16-code-unit count of the last successfully injected text, used by
    /// the "取消/删除这段" voice command to send that many Backspace presses.
    /// Reset to 0 whenever a macro runs (undoing a macro's effects this way
    /// makes no sense) and updated after every successful plain-text or
    /// UppercaseSuffix injection.
    last_injected_length: usize,

    /// Kept alive only while their window is open; dropped (closing the
    /// window) is fine since these hold no state the rest of the app depends
    /// on once the user is done with them.
    settings_controller: Option<settings_window::Controller>,
    term_dict_controller: Option<term_dictionary_window::Controller>,
    onboarding_controller: Option<osw_native::onboarding::OnboardingController>,
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

/// Coarse Chinese phase label shown in the main window's header, matching the
/// `Phase` enum's states. Deliberately separate from `Overlay`'s own
/// (English, more granular/transient) status text -- the two windows are
/// independent.
fn phase_label(phase: Phase) -> &'static str {
    match phase {
        Phase::Idle => "就绪",
        Phase::Starting => "准备麦克风",
        Phase::Capturing => "正在听",
        Phase::Recognizing => "正在识别",
    }
}

fn set_main_window_phase(main_win_weak: &Weak<MainWindow>, phase: Phase) {
    if let Some(win) = main_win_weak.upgrade() {
        win.set_status_text(phase_label(phase).into());
    }
}

fn show_main_window(win: &MainWindow) {
    let _ = win.show();
}

fn toggle_main_window(win: &MainWindow) {
    if win.window().is_visible() {
        let _ = win.hide();
    } else {
        show_main_window(win);
    }
}

fn on_toggle_show_hide_hotkey() {
    STATE.with(|state| {
        if let Some(state) = state.borrow().as_ref()
            && let Some(win) = state.main_win_weak.upgrade()
        {
            toggle_main_window(&win);
        }
    });
}

fn load_tray_icon() -> TrayIcon {
    let png_bytes = include_bytes!("../assets/app.png");
    let rgba = image::load_from_memory(png_bytes).expect("failed to decode embedded tray icon PNG").to_rgba8();
    let (width, height) = rgba.dimensions();
    TrayIcon::from_rgba(rgba.into_raw(), width, height).expect("failed to build tray icon from decoded PNG")
}

/// Resolves which raw VK currently counts as "the" push-to-talk key: a
/// per-foreground-app override (`AppSettings::app_specific_hotkeys`) if the
/// active process has one configured, else the global default.
fn effective_ptt_vk(settings: &AppSettings) -> u32 {
    if let Some(proc_name) = app_info::active_process_name()
        && let Some(&vk) = settings.app_specific_hotkeys.get(&proc_name)
    {
        return vk;
    }
    settings.push_to_talk_virtual_key
}

enum PttAction {
    None,
    Start,
    Stop,
}

/// Raw low-level-hook key-down callback (see `hotkey.rs`'s doc comment for
/// why this now receives every key, not just one hardcoded constant).
fn on_raw_key_down(vk: u32) {
    if hotkey_capture::try_consume(vk) {
        return;
    }
    let action = STATE.with(|state| {
        let mut state = state.borrow_mut();
        let Some(state) = state.as_mut() else { return PttAction::None };
        let target_vk = effective_ptt_vk(&state.settings);
        if vk != target_vk {
            return PttAction::None;
        }
        match state.settings.push_to_talk_mode {
            PushToTalkMode::Hold => {
                if state.phase == Phase::Idle {
                    state.active_matched_vk = Some(vk);
                    PttAction::Start
                } else {
                    PttAction::None
                }
            }
            PushToTalkMode::Toggle => {
                let now = Instant::now();
                if now.duration_since(state.last_toggle_at) < TOGGLE_DEBOUNCE {
                    return PttAction::None;
                }
                state.last_toggle_at = now;
                if state.phase == Phase::Idle && !state.toggle_active {
                    state.toggle_active = true;
                    state.active_matched_vk = Some(vk);
                    PttAction::Start
                } else if state.toggle_active {
                    state.toggle_active = false;
                    PttAction::Stop
                } else {
                    // A session is mid-flight (Starting/Capturing/Recognizing)
                    // from something other than a clean toggle-on -- ignore
                    // rather than risk a double-stop.
                    PttAction::None
                }
            }
        }
    });
    match action {
        PttAction::Start => begin_dictation(),
        PttAction::Stop => end_dictation(),
        PttAction::None => {}
    }
}

/// Raw low-level-hook key-up callback. Toggle mode ignores key-up entirely
/// (both transitions happen on key-down); Hold mode ends the session only if
/// this is the exact VK that started it (the latched `active_matched_vk`, not
/// a fresh per-app lookup -- see that field's doc comment).
fn on_raw_key_up(vk: u32) {
    let should_stop = STATE.with(|state| {
        let mut state = state.borrow_mut();
        let Some(state) = state.as_mut() else { return false };
        if matches!(state.settings.push_to_talk_mode, PushToTalkMode::Toggle) {
            return false;
        }
        if state.active_matched_vk == Some(vk) {
            state.active_matched_vk = None;
            true
        } else {
            false
        }
    });
    if should_stop {
        end_dictation();
    }
}

/// Begin a dictation session: capture the foreground window, then (after a
/// zero-delay timer so this returns to the hook procedure quickly -- see the
/// original design note this preserves) open the microphone.
///
/// ## Why microphone startup is deferred (2026-09-06 security audit finding)
///
/// This runs synchronously inside the `WH_KEYBOARD_LL` hook callback (see
/// `hotkey.rs`). Windows enforces `LowLevelHooksTimeout` (~300ms by default)
/// on that callback: if it doesn't return in time, Windows silently unhooks
/// it -- push-to-talk would then be permanently dead until the app restarts,
/// with no error logged anywhere. Opening the audio device
/// (`AudioCapture::start`, WASAPI/COM device negotiation via `cpal`) can
/// genuinely exceed that budget, especially for Bluetooth microphones. So the
/// hook callback only does cheap work here and schedules the actual device
/// open via a zero-delay Slint timer, which runs on the next event-loop
/// iteration -- after the hook procedure has already returned to Windows, but
/// still on this same thread (so `AudioCapture`'s non-`Send` cpal `Stream`
/// never crosses a thread boundary).
fn begin_dictation() {
    STATE.with(|state| {
        let mut state = state.borrow_mut();
        let Some(state) = state.as_mut() else { return };
        state.next_session += 1;
        let Some(captured_target) = target::capture(state.next_session) else {
            set_status(&state.ui_weak, "No foreground window to dictate into.", idle_color());
            return;
        };
        state.target = Some(captured_target);
        state.phase = Phase::Starting;
        set_status(&state.ui_weak, "Starting microphone...", busy_color());
        set_main_window_phase(&state.main_win_weak, Phase::Starting);
    });

    slint::Timer::single_shot(Duration::from_millis(0), || {
        STATE.with(|state| {
            let mut state = state.borrow_mut();
            let Some(state) = state.as_mut() else { return };
            if state.phase != Phase::Starting {
                return;
            }
            let device_id =
                if state.settings.microphone_device_id.is_empty() { None } else { Some(state.settings.microphone_device_id.as_str()) };
            if let Err(e) = state.capture.start(device_id) {
                state.phase = Phase::Idle;
                state.target = None;
                set_status(&state.ui_weak, &format!("Microphone error: {e}"), idle_color());
                set_main_window_phase(&state.main_win_weak, Phase::Idle);
                return;
            }
            priority::raise();
            state.phase = Phase::Capturing;
            set_status(&state.ui_weak, "Listening... release to transcribe", recording_color());
            set_main_window_phase(&state.main_win_weak, Phase::Capturing);
        });
    });
}

/// End a dictation session: stop capture and, if enough audio was collected,
/// hand it to a background thread for recognition. Everything after
/// recognition (safety re-check, command/macro/term-dict/punctuation/
/// draft-confirm/inject/history) runs back on the UI thread -- see this
/// file's top doc comment for why.
fn end_dictation() {
    type Job = (Arc<Recognizer>, Vec<f32>, TargetToken, Weak<Overlay>, Weak<MainWindow>);

    let job: Option<Job> = STATE.with(|state| {
        let mut state = state.borrow_mut();
        let state = state.as_mut()?;

        if state.phase == Phase::Starting {
            // Key released before the deferred microphone-open callback in
            // `begin_dictation` even ran -- nothing was ever recorded.
            state.phase = Phase::Idle;
            state.target = None;
            state.toggle_active = false;
            priority::lower();
            set_status(&state.ui_weak, "Cancelled (released too soon).", idle_color());
            set_main_window_phase(&state.main_win_weak, Phase::Idle);
            return None;
        }
        if state.phase != Phase::Capturing {
            return None;
        }
        let samples = state.capture.stop();
        // Memory hygiene: the raw sample buffer is dropped right after this
        // scope ends anyway, but zeroing content mirrors the C# reference's
        // explicit `Array.Clear` -- cheap, and never wrong to do.
        if samples.len() < audio::SAMPLE_RATE_OUT as usize / 10 {
            state.phase = Phase::Idle;
            state.target = None;
            priority::lower();
            set_status(&state.ui_weak, "Recording too short.", idle_color());
            set_main_window_phase(&state.main_win_weak, Phase::Idle);
            return None;
        }
        let captured_target = state.target.take()?;
        state.phase = Phase::Recognizing;
        set_status(&state.ui_weak, "Recognizing...", busy_color());
        set_main_window_phase(&state.main_win_weak, Phase::Recognizing);
        Some((Arc::clone(&state.recognizer), samples, captured_target, state.ui_weak.clone(), state.main_win_weak.clone()))
    });

    let Some((recognizer, mut samples, captured_target, ui_weak, main_win_weak)) = job else { return };

    std::thread::spawn(move || {
        let text = recognizer.recognize(&samples, audio::SAMPLE_RATE_OUT as i32).trim().to_string();
        samples.fill(0.0);

        let _ = ui_weak.upgrade_in_event_loop(move |ui| {
            handle_recognition_result(text, captured_target, ui, main_win_weak);
        });
    });
}

/// A trimmed recognition result wrapped entirely in brackets/parens, e.g.
/// `[BLANK_AUDIO]` or `(noise)` -- SenseVoice's spelling for "no real speech
/// detected", ported verbatim from the C# reference's `IsNonSpeechMarker`.
fn is_non_speech_marker(text: &str) -> bool {
    let t = text.trim();
    (t.len() >= 2 && t.starts_with('[') && t.ends_with(']')) || (t.len() >= 2 && t.starts_with('(') && t.ends_with(')'))
}

/// Returns the app back to Idle, restores default (lowered) process priority,
/// and clears the in-flight target -- the common tail of every path through
/// `handle_recognition_result` and its helpers below.
fn finish_idle(ui: &Overlay, main_win_weak: &Weak<MainWindow>, message: String) {
    ui.set_status_text(message.into());
    ui.set_dot_color(idle_color());
    set_main_window_phase(main_win_weak, Phase::Idle);
    priority::lower();
    STATE.with(|state| {
        if let Some(state) = state.borrow_mut().as_mut() {
            state.phase = Phase::Idle;
            state.target = None;
        }
    });
}

fn append_history(text: &str) {
    STATE.with(|state| {
        if let Some(state) = state.borrow_mut().as_mut() {
            let record = history::Record { time: history::now_hhmm(), text: text.to_string(), epoch_secs: history::now_epoch_secs() };
            state.history_model.insert(
                0,
                HistoryEntry { time: record.time.into(), text: record.text.into(), epoch_secs: history::now_epoch_secs() as f64 },
            );
            while state.history_model.row_count() > history::MAX_ENTRIES {
                state.history_model.remove(state.history_model.row_count() - 1);
            }
            let snapshot: Vec<history::Record> = state
                .history_model
                .iter()
                .map(|e| history::Record { time: e.time.to_string(), text: e.text.to_string(), epoch_secs: e.epoch_secs as i64 })
                .collect();
            history::save(&snapshot);
        }
    });
}

fn send_vk_press(vk: u16) -> bool {
    use windows::Win32::UI::Input::KeyboardAndMouse::{INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, SendInput, VIRTUAL_KEY};
    let make = |up: bool| INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT { wVk: VIRTUAL_KEY(vk), wScan: 0, dwFlags: if up { KEYEVENTF_KEYUP } else { Default::default() }, time: 0, dwExtraInfo: 0 },
        },
    };
    let inputs = [make(false), make(true)];
    let sent = unsafe { SendInput(&inputs, core::mem::size_of::<INPUT>() as i32) };
    sent as usize == inputs.len()
}

fn send_backspaces(count: usize) {
    // Sane ceiling: a runaway count here (e.g. corrupted state) must not hang
    // the UI thread sending tens of thousands of synthetic keystrokes.
    for _ in 0..count.min(2000) {
        send_vk_press(0x08);
    }
}

/// Runs the shared term-dictionary + (optionally) punctuation pass, per the
/// current settings snapshot already captured by the caller.
fn post_process(text: &str, corrections: &[term_dictionary::TermCorrection], autocorrect: bool) -> String {
    let corrected = term_dictionary::apply(text, corrections);
    if autocorrect { punctuation::apply(&corrected) } else { corrected }
}

fn finalize_injection(text: String, target: TargetToken, ui_weak: Weak<Overlay>, main_win_weak: Weak<MainWindow>) {
    let Some(ui) = ui_weak.upgrade() else { return };
    let ok = inject::inject_unicode(&text, &target);
    let message = if ok {
        STATE.with(|state| {
            if let Some(state) = state.borrow_mut().as_mut() {
                state.last_injected_length = text.encode_utf16().count();
            }
        });
        append_history(&text);
        format!("Inserted: {text}")
    } else {
        format!("Input was blocked or interrupted: {text}")
    };
    finish_idle(&ui, &main_win_weak, message);
}

fn handle_voice_command(
    cmd: CommandMatch,
    target: &TargetToken,
    ui: &Overlay,
    main_win_weak: &Weak<MainWindow>,
    corrections: &[term_dictionary::TermCorrection],
    autocorrect: bool,
) {
    match cmd.action {
        VoiceCommandAction::Cancel => {
            let last_len = STATE.with(|s| s.borrow().as_ref().map(|s| s.last_injected_length).unwrap_or(0));
            if last_len > 0 {
                send_backspaces(last_len);
            }
            STATE.with(|s| {
                if let Some(s) = s.borrow_mut().as_mut() {
                    s.last_injected_length = 0;
                }
            });
            finish_idle(ui, main_win_weak, "Cancelled last utterance.".to_string());
        }
        VoiceCommandAction::SendEnter => {
            let ok = send_vk_press(0x0D);
            if ok {
                STATE.with(|s| {
                    if let Some(s) = s.borrow_mut().as_mut() {
                        s.last_injected_length = 1;
                    }
                });
            }
            finish_idle(ui, main_win_weak, if ok { "Enter sent.".to_string() } else { "Enter blocked.".to_string() });
        }
        VoiceCommandAction::UppercaseSuffix => {
            let processed = post_process(&cmd.remaining_text, corrections, autocorrect);
            let upper = processed.to_uppercase();
            let ok = inject::inject_unicode(&upper, target);
            if ok {
                STATE.with(|s| {
                    if let Some(s) = s.borrow_mut().as_mut() {
                        s.last_injected_length = upper.encode_utf16().count();
                    }
                });
                append_history(&upper);
            }
            finish_idle(ui, main_win_weak, format!("Inserted: {upper}"));
        }
    }
}

/// Runs entirely on the UI thread: everything past raw recognition.
fn handle_recognition_result(text: String, captured_target: TargetToken, ui: Overlay, main_win_weak: Weak<MainWindow>) {
    if text.is_empty() || is_non_speech_marker(&text) {
        finish_idle(&ui, &main_win_weak, "No speech recognised.".to_string());
        return;
    }
    if !target::matches_foreground(&captured_target) {
        // Non-negotiable safety guarantee: the foreground window changed
        // while recognition was running, so nothing (not even a voice
        // command/macro) may act on this utterance -- only the plain text
        // is retained as an unattempted draft.
        finish_idle(&ui, &main_win_weak, format!("Draft retained (you changed windows): {text}"));
        return;
    }

    let (commands, macros, corrections, autocorrect, draft_gate) = STATE.with(|state| {
        let state = state.borrow();
        let s = state.as_ref().expect("STATE initialized before the event loop starts");
        (s.voice_commands.clone(), s.voice_macros.clone(), s.term_corrections.clone(), s.settings.autocorrect_punctuation, s.settings.show_draft_before_inject)
    });

    if let Some(cmd) = voice::match_command(&text, &commands) {
        handle_voice_command(cmd, &captured_target, &ui, &main_win_weak, &corrections, autocorrect);
        return;
    }
    if let Some(m) = voice::match_macro(&text, &macros) {
        let errors = voice::execute_macro(m, &captured_target);
        STATE.with(|s| {
            if let Some(s) = s.borrow_mut().as_mut() {
                s.last_injected_length = 0;
            }
        });
        let message = if errors.is_empty() { "Macro executed.".to_string() } else { format!("Macro executed with errors: {}", errors.join("; ")) };
        finish_idle(&ui, &main_win_weak, message);
        return;
    }

    let processed = post_process(&text, &corrections, autocorrect);

    if draft_gate {
        ui.set_status_text("Draft ready -- confirm to insert.".into());
        let ui_weak2 = ui.as_weak();
        let main_win_weak2 = main_win_weak.clone();
        osw_native::draft_confirm::show(processed, move |result| match result {
            Some(edited) => finalize_injection(edited, captured_target, ui_weak2, main_win_weak2),
            None => {
                if let Some(ui) = ui_weak2.upgrade() {
                    finish_idle(&ui, &main_win_weak2, "Draft cancelled.".to_string());
                }
            }
        });
    } else {
        finalize_injection(processed, captured_target, ui.as_weak(), main_win_weak);
    }
}

/// Applies native Win32 window styles Slint's cross-platform `Window` has no
/// equivalent for: `WS_EX_NOACTIVATE` (never steal foreground activation) and
/// `WS_EX_TOOLWINDOW` (no taskbar entry). Must run after the platform window
/// actually exists, hence the caller defers this to a zero-duration timer.
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

/// Opens (or re-focuses, if already open) the Settings window.
fn open_settings_window() {
    STATE.with(|state| {
        let mut state_ref = state.borrow_mut();
        let Some(app_state) = state_ref.as_mut() else { return };
        if let Some(controller) = &app_state.settings_controller {
            let _ = controller.window.show();
            return;
        }
        let params = settings_window::OpenParams {
            settings: app_state.settings.clone(),
            microphone_names: audio::list_input_device_names(),
            voice_commands_text: voice::format_commands(&app_state.voice_commands),
            voice_macros_text: voice::format_macros(&app_state.voice_macros),
        };
        let controller = settings_window::open(
            params,
            |result: settings_window::SaveResult| {
                apply_saved_settings(result);
            },
            || {
                open_term_dictionary_window();
            },
        );
        app_state.settings_controller = Some(controller);
    });
}

fn apply_saved_settings(result: settings_window::SaveResult) {
    let autostart_changed = STATE.with(|state| {
        let mut state = state.borrow_mut();
        let Some(state) = state.as_mut() else { return None };
        let old_autostart = state.settings.auto_start_with_windows;
        state.settings = result.settings;
        settings::save(&state.settings);

        state.voice_commands = voice::parse_commands(&result.voice_commands_text);
        voice::save_commands(&state.voice_commands);
        state.voice_macros = voice::parse_macros(&result.voice_macros_text);
        voice::save_macros(&state.voice_macros);

        state.history_model.set_vec(
            history::purge_older_than_days(
                state.history_model.iter().map(|e| history::Record { time: e.time.to_string(), text: e.text.to_string(), epoch_secs: e.epoch_secs as i64 }).collect(),
                state.settings.history_retention_days,
            )
            .into_iter()
            .map(|r| HistoryEntry { time: r.time.into(), text: r.text.into(), epoch_secs: r.epoch_secs as f64 })
            .collect::<Vec<_>>(),
        );
        history::save(
            &state
                .history_model
                .iter()
                .map(|e| history::Record { time: e.time.to_string(), text: e.text.to_string(), epoch_secs: e.epoch_secs as i64 })
                .collect::<Vec<_>>(),
        );

        state.settings_controller = None;
        Some(state.settings.auto_start_with_windows != old_autostart)
    });

    if autostart_changed == Some(true) {
        let wanted = STATE.with(|s| s.borrow().as_ref().map(|s| s.settings.auto_start_with_windows).unwrap_or(false));
        if let Err(e) = autostart::set_enabled(wanted) {
            eprintln!("warning: could not update autostart registry entry: {e}");
        }
    }
}

fn open_term_dictionary_window() {
    STATE.with(|state| {
        let mut state_ref = state.borrow_mut();
        let Some(app_state) = state_ref.as_mut() else { return };
        if let Some(controller) = &app_state.term_dict_controller {
            let _ = controller.window.show();
            return;
        }
        let corrections = app_state.term_corrections.clone();
        let controller = term_dictionary_window::open(corrections, |saved: Vec<term_dictionary::TermCorrection>| {
            term_dictionary::save(&saved);
            STATE.with(|state| {
                if let Some(state) = state.borrow_mut().as_mut() {
                    state.term_corrections = saved;
                    state.term_dict_controller = None;
                }
            });
        });
        app_state.term_dict_controller = Some(controller);
    });
}

fn main() {
    crash_reporter::install();
    priority::lower();

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

    let settings = settings::load();
    let term_corrections = term_dictionary::load();
    let voice_commands = voice::load_commands();
    let voice_macros = voice::load_macros();

    let ui = Overlay::new().expect("failed to create overlay window");
    ui.set_status_text("Idle -- hold the push-to-talk key to dictate".into());
    ui.set_dot_color(idle_color());

    let main_win = MainWindow::new().expect("failed to create main window");
    main_win.set_status_text(phase_label(Phase::Idle).into());
    let loaded_history: Vec<HistoryEntry> = history::purge_older_than_days(history::load(), settings.history_retention_days)
        .into_iter()
        .map(|r| HistoryEntry { time: r.time.into(), text: r.text.into(), epoch_secs: r.epoch_secs as f64 })
        .collect();
    history::save(&loaded_history.iter().map(|e| history::Record { time: e.time.to_string(), text: e.text.to_string(), epoch_secs: e.epoch_secs as i64 }).collect::<Vec<_>>());
    let history_model = Rc::new(slint::VecModel::from(loaded_history));
    main_win.set_history(history_model.clone().into());
    main_win.on_open_settings(open_settings_window);
    main_win.on_clear_history(|| {
        STATE.with(|state| {
            if let Some(state) = state.borrow_mut().as_mut() {
                while state.history_model.row_count() > 0 {
                    state.history_model.remove(0);
                }
                history::save(&[]);
            }
        });
    });
    {
        let weak = main_win.as_weak();
        main_win.window().on_close_requested(move || {
            if let Some(win) = weak.upgrade() {
                let _ = win.hide();
            }
            slint::CloseRequestResponse::HideWindow
        });
    }

    let has_seen_onboarding = settings.has_seen_onboarding;

    STATE.with(|state| {
        *state.borrow_mut() = Some(AppState {
            ui_weak: ui.as_weak(),
            main_win_weak: main_win.as_weak(),
            recognizer,
            capture: AudioCapture::new(),
            phase: Phase::Idle,
            target: None,
            next_session: 0,
            history_model: history_model.clone(),
            settings,
            term_corrections,
            voice_commands,
            voice_macros,
            active_matched_vk: None,
            toggle_active: false,
            last_toggle_at: Instant::now() - TOGGLE_DEBOUNCE,
            last_injected_length: 0,
            settings_controller: None,
            term_dict_controller: None,
            onboarding_controller: None,
        });
    });

    let hook: HHOOK = hotkey::install(on_raw_key_down, on_raw_key_up).expect("failed to install low-level keyboard hook");
    println!("Push-to-talk hotkey armed.");

    let (show_hide_mods, show_hide_vk) = STATE.with(|s| {
        let s = s.borrow();
        let s = s.as_ref().unwrap();
        (s.settings.show_hide_hotkey_modifiers, s.settings.show_hide_virtual_key)
    });
    let _show_hide_hotkey = match ShowHideHotkey::register(HOT_KEY_MODIFIERS(show_hide_mods) | MOD_NOREPEAT, show_hide_vk, on_toggle_show_hide_hotkey) {
        Ok(hotkey) => {
            println!("Show/hide hotkey armed.");
            Some(hotkey)
        }
        Err(e) => {
            eprintln!("warning: could not register the show/hide hotkey ({e}); use the tray icon instead.");
            None
        }
    };

    if !has_seen_onboarding {
        let ptt_vk = STATE.with(|s| s.borrow().as_ref().unwrap().settings.push_to_talk_virtual_key);
        let controller = osw_native::onboarding::show(hotkey_capture::vk_label(ptt_vk), || {
            STATE.with(|state| {
                if let Some(state) = state.borrow_mut().as_mut() {
                    state.settings.has_seen_onboarding = true;
                    settings::save(&state.settings);
                    state.onboarding_controller = None;
                }
            });
        });
        STATE.with(|state| {
            if let Some(state) = state.borrow_mut().as_mut() {
                state.onboarding_controller = Some(controller);
            }
        });
    }

    // --- Auto-update: checked once per launch (not on a timer), fully
    // silent on failure -- see `update.rs`'s doc comment for why this only
    // ever considers this app's own `native-rust-v*` release line.
    //
    // `slint::invoke_from_event_loop`/`Weak::upgrade_in_event_loop` both
    // require `F: Send`, but `tray_icon`/`muda`'s `Menu`/`MenuItem` are
    // `Rc`-based (deliberately `!Send`, like most GUI toolkit types) -- so
    // the background thread below cannot touch them directly, even via
    // `invoke_from_event_loop`. Instead it only computes the Send-safe
    // `Option<UpdateInfo>` and hands it across via this `Arc<Mutex<..>>`,
    // which the existing 50ms tray-poll timer (already running on the UI
    // thread for tray/menu events) picks up and acts on.
    let update_check_result: Arc<std::sync::Mutex<Option<Option<update::UpdateInfo>>>> = Arc::new(std::sync::Mutex::new(None));
    let update_menu_item: Rc<RefCell<Option<MenuItem>>> = Rc::new(RefCell::new(None));
    let pending_update: Rc<RefCell<Option<update::UpdateInfo>>> = Rc::new(RefCell::new(None));

    let show_menu_item = MenuItem::new("显示主界面", true, None);
    let settings_menu_item = MenuItem::new("设置", true, None);
    let check_update_menu_item = MenuItem::new("检查更新", true, None);
    let quit_menu_item = MenuItem::new("退出", true, None);
    let show_menu_item_id = show_menu_item.id().clone();
    let settings_menu_item_id = settings_menu_item.id().clone();
    let check_update_menu_item_id = check_update_menu_item.id().clone();
    let quit_menu_item_id = quit_menu_item.id().clone();
    let tray_menu = Menu::new();
    tray_menu.append(&show_menu_item).expect("failed to build tray context menu");
    tray_menu.append(&settings_menu_item).expect("failed to build tray context menu");
    tray_menu.append(&check_update_menu_item).expect("failed to build tray context menu");
    tray_menu.append(&quit_menu_item).expect("failed to build tray context menu");

    let tray_icon = TrayIconBuilder::new()
        .with_icon(load_tray_icon())
        .with_tooltip("Aevocis - 就绪")
        .with_menu(Box::new(tray_menu.clone()))
        .with_menu_on_left_click(false)
        .build()
        .expect("failed to create tray icon");

    {
        let update_check_result = Arc::clone(&update_check_result);
        std::thread::spawn(move || {
            let result = update::check_latest();
            *update_check_result.lock().unwrap() = Some(result);
        });
    }

    let _tray_poll_timer = {
        let win_weak = main_win.as_weak();
        let tray_menu = tray_menu.clone();
        let timer = slint::Timer::default();
        timer.start(slint::TimerMode::Repeated, Duration::from_millis(50), move || {
            if let Ok(mut guard) = update_check_result.try_lock()
                && let Some(result) = guard.take()
                && let Some(info) = result
            {
                let item = MenuItem::new(&format!("下载新版本 v{}", info.version), true, None);
                let _ = tray_menu.append(&item);
                *update_menu_item.borrow_mut() = Some(item);
                *pending_update.borrow_mut() = Some(info);
            }
            if let Ok(TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. }) = TrayIconEvent::receiver().try_recv()
                && let Some(win) = win_weak.upgrade()
            {
                toggle_main_window(&win);
            }
            if let Ok(event) = MenuEvent::receiver().try_recv() {
                if event.id == show_menu_item_id {
                    if let Some(win) = win_weak.upgrade() {
                        show_main_window(&win);
                    }
                } else if event.id == settings_menu_item_id {
                    open_settings_window();
                } else if event.id == check_update_menu_item_id {
                    if let Some(info) = pending_update.borrow_mut().take() {
                        if let Err(e) = update::download_and_relaunch(&info) {
                            eprintln!("warning: update download/relaunch failed: {e}");
                        }
                    } else {
                        // Manual re-check, matching the tray menu's other
                        // "do it now" affordances.
                        if let Some(info) = update::check_latest() {
                            *pending_update.borrow_mut() = Some(info);
                        }
                    }
                } else if let Some(item) = update_menu_item.borrow().as_ref()
                    && event.id == *item.id()
                {
                    if let Some(info) = pending_update.borrow_mut().take() {
                        if let Err(e) = update::download_and_relaunch(&info) {
                            eprintln!("warning: update download/relaunch failed: {e}");
                        }
                    }
                } else if event.id == quit_menu_item_id {
                    slint::quit_event_loop().expect("failed to request event loop shutdown");
                }
            }
        });
        timer
    };

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
    let _ = main_win.hide();
    drop(tray_icon);
    hotkey::uninstall(hook);
}
