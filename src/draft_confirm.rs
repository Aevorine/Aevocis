//! Draft-confirmation popup controller: an optional (settings-gated --
//! wiring that setting into the dictation pipeline is someone else's
//! integration work, not this file's) step where, instead of injecting
//! recognized text immediately, a small non-modal window shows it pre-filled
//! and editable, positioned bottom-center of the screen's work area. Enter
//! confirms (with whatever edits the user made); Escape, or closing the
//! window any other way, cancels.
//!
//! There is no async runtime here -- the real app's event loop
//! (`slint::run_event_loop()` in `main.rs`) is single-threaded and cannot
//! literally block waiting for a dialog -- so `show` below is fire-and-forget:
//! it returns immediately, and the caller-supplied closure fires exactly once
//! later, from that same event loop, with the outcome.
//!
//! Design reference (behavior, not code): the C# app's
//! `DraftConfirmationService` / `DraftConfirmWindow`.

// Manual equivalent of `slint::include_modules!()` (which `main.rs` already
// uses for its own single generated file -- that macro can only target one
// file per crate). Pulls in the `DraftConfirmWindow` type generated from
// `ui/draft_confirm_window.slint`.
//
// INTEGRATION NOTE: this only compiles once `build.rs` also compiles that
// .slint file. Add this as a second line in `build.rs` (this worktree's
// `build.rs` is deliberately left unmodified -- see this crate's
// draft-confirm feature branch / PR description for why):
//     slint_build::compile("ui/draft_confirm_window.slint").expect("Slint build failed");
include!(concat!(env!("OUT_DIR"), "/draft_confirm_window.rs"));

use std::cell::RefCell;
use std::rc::{Rc, Weak};
use std::time::Duration;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::ComponentHandle;
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::UI::WindowsAndMessaging::{
    GWL_EXSTYLE, GetWindowLongPtrW, HWND_TOPMOST, SWP_NOSIZE, SetWindowLongPtrW, SetWindowPos, SPI_GETWORKAREA,
    SystemParametersInfoW, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
};

thread_local! {
    /// The single strong owner of every currently-shown draft-confirm
    /// `Session` (and therefore of its `DraftConfirmWindow`) -- `show`
    /// pushes into this, `finish` removes from it. The window's own
    /// callback closures below deliberately capture only a `Weak` back
    /// reference to their `Session` (see `show`), never a strong `Rc`, so
    /// there is no window-owns-closure-owns-Rc-owns-window reference cycle:
    /// removing an entry here is sufficient for that session to actually
    /// deallocate. Mirrors `main.rs`'s `STATE` thread-local -- this crate's
    /// established single-thread convention for otherwise-orphaned UI state
    /// that must outlive the function call that created it.
    static SESSIONS: RefCell<Vec<Rc<RefCell<Session>>>> = const { RefCell::new(Vec::new()) };
}

/// Owns the live window and the not-yet-fired result callback. See
/// `SESSIONS` above for why this is reached only through a `Weak` from the
/// window's own callbacks, and only strongly owned by that thread-local.
struct Session {
    window: DraftConfirmWindow,
    on_result: Option<Box<dyn FnOnce(Option<String>)>>,
}

/// Shows a draft-confirm window pre-filled with `draft_text`, positioned
/// bottom-center of the primary monitor's work area. `on_result` is invoked
/// exactly once, with `Some(edited_text)` on Enter/confirm or `None` on
/// Escape/close-without-confirming. The window is destroyed after either
/// outcome. Must not block the caller.
pub fn show(draft_text: String, on_result: impl FnOnce(Option<String>) + 'static) {
    let window = DraftConfirmWindow::new().expect("failed to create draft-confirm window");
    window.set_draft_text(draft_text.into());
    let weak_window = window.as_weak();

    let session = Rc::new(RefCell::new(Session { window, on_result: Some(Box::new(on_result)) }));
    let session_weak = Rc::downgrade(&session);

    {
        let session_weak = session_weak.clone();
        session.borrow().window.on_confirmed(move |text| {
            finish(&session_weak, Some(text.to_string()));
        });
    }
    {
        let session_weak = session_weak.clone();
        session.borrow().window.on_cancelled(move || {
            finish(&session_weak, None);
        });
    }
    {
        let session_weak = session_weak.clone();
        session.borrow().window.window().on_close_requested(move || {
            // Any dismissal that isn't Enter/`confirmed` is a cancel --
            // matches the C# app. `finish` is a no-op if `confirmed`/
            // `cancelled` already fired first (e.g. Enter, then the window
            // tears itself down and this fires anyway on some platforms).
            finish(&session_weak, None);
            slint::CloseRequestResponse::HideWindow
        });
    }

    session.borrow().window.show().expect("failed to show draft-confirm window");

    // `session` is the ONLY strong owner (see `SESSIONS` above) -- keep it
    // alive by registering it here now that every callback is wired up.
    SESSIONS.with(|sessions| sessions.borrow_mut().push(session));

    // The platform HWND doesn't exist until after `.show()`. A *single*
    // zero-duration `slint::Timer::single_shot` (the pattern
    // `main.rs::apply_native_overlay_style`'s caller uses) is not reliably
    // enough of a delay: Slint's own docs on `Window::window_handle()` say
    // the handle "may only become available ... after at least one
    // iteration of the event loop following a call to `show()`" -- i.e. it
    // can take more than one iteration. So this retries with a fresh
    // zero-duration single-shot each time the handle isn't ready yet
    // (bounded, so a platform that never produces a handle can't spin
    // forever) rather than assuming one deferral is always enough.
    finalize_window_when_ready(weak_window, 50);
}

/// See the comment in `show` above for why this must retry rather than
/// assume the native window exists after exactly one deferred callback.
fn finalize_window_when_ready(weak_window: slint::Weak<DraftConfirmWindow>, attempts_left: u32) {
    let Some(window) = weak_window.upgrade() else { return };
    if hwnd_of(&window).is_none() {
        if attempts_left == 0 {
            // Give up quietly: the window still shows (just without native
            // repositioning/styling applied) rather than never appearing.
            return;
        }
        slint::Timer::single_shot(Duration::from_millis(0), move || {
            finalize_window_when_ready(weak_window, attempts_left - 1);
        });
        return;
    }
    position_bottom_center(&window);
    apply_native_focusable_style(&window);
    window.invoke_focus_edit();
    window.invoke_select_all();
}

/// Fires the result callback exactly once (subsequent calls, from whichever
/// of `confirmed`/`cancelled`/close-requested didn't win the race, are
/// no-ops because either the session already dropped out of `SESSIONS` or
/// its `on_result` is already `None`), hides the window, then drops this
/// session's only strong reference (`SESSIONS`'s entry) so it deallocates.
fn finish(session_weak: &Weak<RefCell<Session>>, result: Option<String>) {
    let Some(session) = session_weak.upgrade() else { return };
    let callback = session.borrow_mut().on_result.take();
    let Some(callback) = callback else { return };
    let _ = session.borrow().window.hide();
    SESSIONS.with(|sessions| sessions.borrow_mut().retain(|s| !Rc::ptr_eq(s, &session)));
    callback(result);
}

/// Extracts the native `HWND` behind a Slint window, the same
/// `raw-window-handle` route `main.rs::apply_native_overlay_style` uses.
fn hwnd_of(window: &DraftConfirmWindow) -> Option<HWND> {
    let handle = window.window().window_handle();
    let win = handle.window_handle().ok()?;
    let RawWindowHandle::Win32(win32) = win.as_raw() else { return None };
    Some(HWND(win32.hwnd.get() as *mut core::ffi::c_void))
}

/// Applies `WS_EX_TOOLWINDOW` (no taskbar entry), mirroring
/// `main.rs::apply_native_overlay_style` -- but, unlike the push-to-talk
/// overlay, deliberately WITHOUT `WS_EX_NOACTIVATE`: this window must be able
/// to receive keyboard focus so the user can type into it.
fn apply_native_focusable_style(window: &DraftConfirmWindow) {
    let Some(hwnd) = hwnd_of(window) else { return };
    unsafe {
        let ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        let augmented = ex_style | WS_EX_TOOLWINDOW.0 as isize | WS_EX_TOPMOST.0 as isize;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, augmented);
    }
}

/// Positions the window bottom-center of the primary monitor's work area
/// (`SPI_GETWORKAREA` excludes the taskbar), ~90px above the work-area
/// bottom -- mirrors the C# app's own formula so it doesn't collide with the
/// taskbar or the push-to-talk overlay. Also re-asserts topmost z-order in
/// the same call, without `SWP_NOACTIVATE`, so the window can still take
/// focus normally.
fn position_bottom_center(window: &DraftConfirmWindow) {
    let Some(hwnd) = hwnd_of(window) else { return };

    let mut rect = RECT::default();
    let ok = unsafe { SystemParametersInfoW(SPI_GETWORKAREA, 0, Some(&mut rect as *mut _ as *mut _), Default::default()) };
    if ok.is_err() {
        return;
    }

    let size = window.window().size();
    let (win_w, win_h) = (size.width as i32, size.height as i32);
    let left = rect.left + (rect.right - rect.left - win_w) / 2;
    let top = rect.bottom - win_h - 90;

    unsafe {
        let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), left, top, 0, 0, SWP_NOSIZE);
    }
}
