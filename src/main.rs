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
use std::rc::Rc;
use std::sync::Arc;
use std::time::Duration;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{ComponentHandle, Model, Weak};
use tray_icon::menu::{CheckMenuItem, Menu, MenuEvent, MenuItem};
use tray_icon::{Icon as TrayIcon, MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{MOD_ALT, MOD_CONTROL, MOD_NOREPEAT};
use windows::Win32::UI::WindowsAndMessaging::{
    GWL_EXSTYLE, GetWindowLongPtrW, HHOOK, HWND_TOPMOST, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SetWindowLongPtrW,
    SetWindowPos, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
};

use osw_native::audio::AudioCapture;
use osw_native::recognizer::Recognizer;
use osw_native::show_hide_hotkey::ShowHideHotkey;
use osw_native::target::{self, TargetToken};
use osw_native::{autostart, history, hotkey, inject};

// Brings in `MainWindow` and `HistoryEntry`, generated at build time by
// `slint_build::compile("ui/main_window.slint")` in `build.rs`. This is a
// second, independent Slint component from the `Overlay` macro above -- the
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
    /// `on_hotkey_down` doc comment for why this state exists.
    Starting,
    Capturing,
    Recognizing,
}

struct AppState {
    ui_weak: Weak<Overlay>,
    main_win_weak: Weak<MainWindow>,
    recognizer: Arc<Recognizer>,
    capture: AudioCapture,
    phase: Phase,
    target: Option<TargetToken>,
    next_session: u64,
    /// Backing model for the main window's history list. Kept here (rather
    /// than only inside the Slint component) so the background recognition
    /// thread's UI-thread callback can prepend a new entry after a
    /// successful dictation -- see `on_hotkey_up`.
    history_model: Rc<slint::VecModel<HistoryEntry>>,
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
/// `Phase` enum's three states. Deliberately separate from `Overlay`'s own
/// (English, more granular/transient) status text above -- the two windows
/// are independent and this must not change what `Overlay` already shows.
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

/// Shows and brings focus to the main window, matching the C# app's
/// `ShowMainWindow()` (used by the tray menu's "显示主界面", which always
/// shows regardless of current visibility -- unlike the tray-icon-left-click/
/// hotkey toggle below).
fn show_main_window(win: &MainWindow) {
    let _ = win.show();
}

/// Toggles main window visibility, matching the C# app's `ToggleMainWindow()`
/// -- used by both the tray icon's left-click and the global show/hide
/// hotkey. "Hidden" here means `Window::hide()`, i.e. actually unshown and
/// off the taskbar, not merely minimized.
fn toggle_main_window(win: &MainWindow) {
    if win.window().is_visible() {
        let _ = win.hide();
    } else {
        show_main_window(win);
    }
}

/// Global show/hide hotkey callback (`Ctrl+Alt+H` by default, registered in
/// `main()`). Must be a plain `fn()` -- see `show_hide_hotkey.rs` -- so it
/// reaches the main window via the same thread-local `STATE` the push-to-talk
/// callbacks below already use.
fn on_toggle_show_hide_hotkey() {
    STATE.with(|state| {
        if let Some(state) = state.borrow().as_ref()
            && let Some(win) = state.main_win_weak.upgrade()
        {
            toggle_main_window(&win);
        }
    });
}

/// Decodes the embedded app icon PNG into the RGBA buffer `tray_icon::Icon`
/// wants. Loaded from `include_bytes!` (baked into the executable at compile
/// time) rather than a runtime path like `assets/app.png`, so this works
/// regardless of the process's current working directory when launched.
fn load_tray_icon() -> TrayIcon {
    let png_bytes = include_bytes!("../assets/app.png");
    let rgba = image::load_from_memory(png_bytes)
        .expect("failed to decode embedded tray icon PNG")
        .to_rgba8();
    let (width, height) = rgba.dimensions();
    TrayIcon::from_rgba(rgba.into_raw(), width, height).expect("failed to build tray icon from decoded PNG")
}

/// Hotkey-down: capture the foreground window as this dictation's target,
/// then start microphone capture. Ignored if a previous dictation is still
/// being recognized/injected -- mirrors the C++ prototype's phase guard.
///
/// ## Why microphone startup is deferred (2026-09-06 security audit finding)
///
/// This function runs synchronously inside the `WH_KEYBOARD_LL` hook
/// callback (see `hotkey.rs`). Windows enforces `LowLevelHooksTimeout`
/// (~300ms by default) on that callback: if it doesn't return in time,
/// Windows silently unhooks it -- push-to-talk would then be permanently
/// dead until the app restarts, with no error logged anywhere. Opening the
/// audio device (`AudioCapture::start`, which does WASAPI/COM device
/// negotiation via `cpal`) can genuinely exceed that budget, especially for
/// Bluetooth microphones -- this project's own history has multiple
/// documented cases of Bluetooth capture-path latency. So the hook callback
/// only does cheap, fast work (capturing the target window, an
/// `AtomicBool`-guarded phase check) and schedules the actual device open
/// via a zero-delay Slint timer, which runs on the very next event-loop
/// iteration -- after the hook procedure has already returned to Windows,
/// but still on this same thread (so `AudioCapture`'s cpal `Stream`, which
/// is not `Send`, never has to cross a thread boundary).
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
        state.target = Some(captured_target);
        state.phase = Phase::Starting;
        set_status(&state.ui_weak, "Starting microphone...", busy_color());
        set_main_window_phase(&state.main_win_weak, Phase::Starting);
    });

    slint::Timer::single_shot(Duration::from_millis(0), || {
        STATE.with(|state| {
            let mut state = state.borrow_mut();
            let Some(state) = state.as_mut() else { return };
            // The hotkey may already have been released (see `on_hotkey_up`'s
            // `Phase::Starting` branch) before this deferred callback ran --
            // in that case there is nothing left to start.
            if state.phase != Phase::Starting {
                return;
            }
            if let Err(e) = state.capture.start() {
                state.phase = Phase::Idle;
                state.target = None;
                set_status(&state.ui_weak, &format!("Microphone error: {e}"), idle_color());
                set_main_window_phase(&state.main_win_weak, Phase::Idle);
                return;
            }
            state.phase = Phase::Capturing;
            set_status(&state.ui_weak, "Listening... release Right Ctrl to transcribe", recording_color());
            set_main_window_phase(&state.main_win_weak, Phase::Capturing);
        });
    });
}

/// Hotkey-up: stop capture and, if enough audio was collected, hand it to a
/// background thread for recognition + the safety re-check + injection, so
/// the UI/hook thread is never blocked on recognition latency.
fn on_hotkey_up() {
    type Job = (Arc<Recognizer>, Vec<f32>, TargetToken, Weak<Overlay>, Weak<MainWindow>);

    let job: Option<Job> = STATE.with(|state| {
        let mut state = state.borrow_mut();
        let state = state.as_mut()?;

        // The key was released before the deferred microphone-open callback
        // in `on_hotkey_down` even ran (a very fast tap, or a slow device).
        // Nothing was ever recorded, so just cancel cleanly -- the deferred
        // callback checks `phase` itself and will no-op when it does run.
        if state.phase == Phase::Starting {
            state.phase = Phase::Idle;
            state.target = None;
            set_status(&state.ui_weak, "Cancelled (released too soon).", idle_color());
            set_main_window_phase(&state.main_win_weak, Phase::Idle);
            return None;
        }
        if state.phase != Phase::Capturing {
            return None;
        }
        let samples = state.capture.stop();
        if samples.len() < osw_native::audio::SAMPLE_RATE_OUT as usize / 10 {
            state.phase = Phase::Idle;
            state.target = None;
            set_status(&state.ui_weak, "Recording too short.", idle_color());
            set_main_window_phase(&state.main_win_weak, Phase::Idle);
            return None;
        }
        let captured_target = state.target.take()?;
        state.phase = Phase::Recognizing;
        set_status(&state.ui_weak, "Recognizing...", busy_color());
        set_main_window_phase(&state.main_win_weak, Phase::Recognizing);
        Some((
            Arc::clone(&state.recognizer),
            samples,
            captured_target,
            state.ui_weak.clone(),
            state.main_win_weak.clone(),
        ))
    });

    let Some((recognizer, samples, captured_target, ui_weak, main_win_weak)) = job else {
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
        } else if inject::inject_unicode(&text, &captured_target) {
            format!("Inserted: {text}")
        } else {
            format!("Input was blocked or interrupted (elevated target, or you switched windows mid-dictation): {text}")
        };

        let _ = ui_weak.upgrade_in_event_loop(move |ui| {
            ui.set_status_text(message.into());
            ui.set_dot_color(idle_color());
            set_main_window_phase(&main_win_weak, Phase::Idle);
            STATE.with(|state| {
                if let Some(state) = state.borrow_mut().as_mut() {
                    state.phase = Phase::Idle;
                    // Recorded regardless of injection outcome (inserted,
                    // blocked, or retained as a draft) -- matches the C#
                    // app's HistoryStore, which logs the recognized
                    // utterance itself, not what happened to it afterward.
                    if !text.is_empty() {
                        let record = history::Record { time: history::now_hhmm(), text: text.clone() };
                        state.history_model.insert(0, HistoryEntry { time: record.time.clone().into(), text: record.text.clone().into() });
                        while state.history_model.row_count() > history::MAX_ENTRIES {
                            state.history_model.remove(state.history_model.row_count() - 1);
                        }
                        let snapshot: Vec<history::Record> = state.history_model.iter().map(|e| history::Record {
                            time: e.time.to_string(),
                            text: e.text.to_string(),
                        }).collect();
                        history::save(&snapshot);
                    }
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

    // --- Main window (GUI shell): hidden by default, shown via the tray icon
    // left-click, the tray menu's "显示主界面", or the Ctrl+Alt+H hotkey below.
    // Entirely additive: it has no effect on the push-to-talk pipeline above,
    // which keeps talking only to `Overlay`.
    let main_win = MainWindow::new().expect("failed to create main window");
    main_win.set_status_text(phase_label(Phase::Idle).into());
    let loaded_history: Vec<HistoryEntry> = history::load()
        .into_iter()
        .map(|r| HistoryEntry { time: r.time.into(), text: r.text.into() })
        .collect();
    let history_model = Rc::new(slint::VecModel::from(loaded_history));
    main_win.set_history(history_model.clone().into());
    {
        // Closing the window (the titlebar X) hides it instead of tearing it
        // down, matching the tray-app convention the C# app already uses --
        // the process keeps running in the tray either way.
        let weak = main_win.as_weak();
        main_win.window().on_close_requested(move || {
            if let Some(win) = weak.upgrade() {
                let _ = win.hide();
            }
            slint::CloseRequestResponse::HideWindow
        });
    }

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
        });
    });

    let hook: HHOOK = hotkey::install(on_hotkey_down, on_hotkey_up).expect("failed to install low-level keyboard hook");
    println!("Push-to-talk hotkey armed: hold Right Ctrl to dictate, release to transcribe + inject.");

    // --- Global show/hide hotkey (Ctrl+Alt+H, matching the C# app's default)
    // via `RegisterHotKey` -- a different, higher-level Win32 mechanism from
    // the low-level keyboard hook `hotkey.rs` uses for push-to-talk above; see
    // `show_hide_hotkey.rs` for why the two must not be conflated.
    //
    // Registration failure (most commonly `ERROR_HOTKEY_ALREADY_REGISTERED`,
    // e.g. another app -- including, on this exact dev machine, the shipping
    // C# build of this same app -- already owns Ctrl+Alt+H) must not be
    // fatal: the tray icon's left-click and "显示主界面" menu item are fully
    // independent ways to reach the same window, so this degrades to "no
    // hotkey" with a logged warning rather than taking the whole process
    // down over a non-essential convenience binding.
    let _show_hide_hotkey = match ShowHideHotkey::register(MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x48, on_toggle_show_hide_hotkey) {
        Ok(hotkey) => {
            println!("Show/hide hotkey armed: Ctrl+Alt+H toggles the main window.");
            Some(hotkey)
        }
        Err(e) => {
            eprintln!("warning: could not register Ctrl+Alt+H show/hide hotkey ({e}); use the tray icon instead.");
            None
        }
    };

    // --- System tray icon. `tray-icon` (crates.io, maintained by the Tauri
    // project) was chosen over hand-rolling `Shell_NotifyIconW` directly:
    // Shell_NotifyIconW's own contract requires the app to also pump
    // `WM_TASKBARCREATED` (Explorer restart), rebuild the icon on DPI/explicit
    // teardown, and hand-manage a native popup menu (`TrackPopupMenu` reentrancy
    // footguns, `SetForegroundWindow` before/after the call) -- `tray-icon`
    // (composed with `muda` for the menu) already gets all of that right and
    // is exercised in production by Tauri itself, for one extra dependency.
    // It integrates with any host event loop (not just its own) via the
    // documented polling pattern used below: `TrayIconEvent`/`MenuEvent` are
    // delivered through global channels that any thread already pumping
    // Win32 messages (winit's, here) can poll -- see the repeating timer.
    let show_menu_item = MenuItem::new("显示主界面", true, None);
    // Initial checked state reads the real registry value (see
    // `autostart::is_enabled`) rather than any cached setting, so the menu
    // never shows a state that disagrees with what Windows will actually do
    // at next sign-in.
    let autostart_menu_item = CheckMenuItem::new("开机自启", true, autostart::is_enabled(), None);
    let quit_menu_item = MenuItem::new("退出", true, None);
    let show_menu_item_id = show_menu_item.id().clone();
    let autostart_menu_item_id = autostart_menu_item.id().clone();
    let quit_menu_item_id = quit_menu_item.id().clone();
    let tray_menu = Menu::new();
    tray_menu.append(&show_menu_item).expect("failed to build tray context menu");
    tray_menu.append(&autostart_menu_item).expect("failed to build tray context menu");
    tray_menu.append(&quit_menu_item).expect("failed to build tray context menu");

    let _tray_icon = TrayIconBuilder::new()
        .with_icon(load_tray_icon())
        .with_tooltip("Aevocis - 就绪")
        .with_menu(Box::new(tray_menu))
        // Left-click toggles the main window (see the polling timer below);
        // only right-click should pop the context menu, matching the spec.
        .with_menu_on_left_click(false)
        .build()
        .expect("failed to create tray icon");

    let _tray_poll_timer = {
        let win_weak = main_win.as_weak();
        let timer = slint::Timer::default();
        timer.start(slint::TimerMode::Repeated, Duration::from_millis(50), move || {
            if let Ok(TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            }) = TrayIconEvent::receiver().try_recv()
                && let Some(win) = win_weak.upgrade()
            {
                toggle_main_window(&win);
            }
            if let Ok(event) = MenuEvent::receiver().try_recv() {
                if event.id == show_menu_item_id {
                    if let Some(win) = win_weak.upgrade() {
                        show_main_window(&win);
                    }
                } else if event.id == autostart_menu_item_id {
                    // `muda` already flipped the visible checkbox before this
                    // event fires; treat that as the user's intent and make
                    // the registry match it, rolling the checkbox back on
                    // failure so it never lies about the real state.
                    let wanted = autostart_menu_item.is_checked();
                    if let Err(e) = autostart::set_enabled(wanted) {
                        eprintln!("warning: could not update autostart registry entry: {e}");
                        autostart_menu_item.set_checked(!wanted);
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
    hotkey::uninstall(hook);
}
