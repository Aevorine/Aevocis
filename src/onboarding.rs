//! One-time, non-modal first-run wizard: 3 steps explaining push-to-talk
//! dictation to a brand-new user. Shown once, driven by
//! `AppSettings::has_seen_onboarding` (see `src/settings.rs`) -- the caller
//! (`main.rs`) is expected to call `show` only when that flag is `false`,
//! and to persist it as `true` from the `on_finished` callback passed in
//! here.
//!
//! This module owns all three steps' actual Chinese copy (rather than
//! hardcoding it in `ui/onboarding_window.slint`), so future wording edits
//! touch exactly one place. The window itself is pure layout, driven by
//! `step-title`/`step-body`/`is-last-step`/`step` properties set from here.
//!
//! Design reference (behavior, not code): the C# app's `OnboardingWindow`.

include!(concat!(env!("OUT_DIR"), "/onboarding_window.rs"));

use std::cell::RefCell;
use std::rc::Rc;

use slint::{ComponentHandle, Weak};

/// One step's title + body text.
struct Step {
    title: &'static str,
    body: String,
}

fn build_steps(hotkey_label: &str) -> [Step; 3] {
    [
        Step { title: "按住热键", body: format!("按住 {hotkey_label} 开始说话") },
        Step { title: "说话", body: "对着麦克风清楚地说出你想输入的内容".to_string() },
        Step { title: "文字自动出现", body: "松开按键后，识别的文字会自动输入到当前光标处".to_string() },
    ]
}

/// Fire-exactly-once wrapper around the caller's finish callback: whichever
/// of skip / "知道了，开始用" (next on the last step) / direct window close
/// happens first takes it; any later path is a silent no-op.
type FinishOnce = Rc<RefCell<Option<Box<dyn FnOnce()>>>>;

/// Owns the onboarding window and its current-step state for as long as the
/// wizard is open. Dropping this before the window closes is safe -- the
/// `Weak` handles the callbacks hold degrade to no-ops, and the window keeps
/// itself alive while shown via Slint's normal reference semantics -- but
/// callers should generally keep it around (e.g. in an outer `AppState`) so
/// `current_step()` stays queryable.
pub struct OnboardingController {
    window: OnboardingWindow,
    current_step: Rc<RefCell<usize>>,
}

/// Applies `step`'s title/body/is-last-step/step properties to `window`.
fn apply_step(window: &OnboardingWindow, steps: &[Step; 3], step: usize) {
    let s = &steps[step];
    window.set_step(step as i32);
    window.set_step_title(s.title.into());
    window.set_step_body(s.body.as_str().into());
    window.set_is_last_step(step == steps.len() - 1);
}

/// Fires `on_finished` (if not already fired) and hides `window`. Shared by
/// the "知道了，开始用" (last-step `next`), "跳过", and window-close paths.
fn finish_and_hide(window_weak: &Weak<OnboardingWindow>, on_finished: &FinishOnce) {
    if let Some(cb) = on_finished.borrow_mut().take() {
        cb();
    }
    if let Some(window) = window_weak.upgrade() {
        let _ = window.hide();
    }
}

/// Shows the onboarding window starting at step 0. `hotkey_label` is
/// interpolated into step 0's body text (e.g. "右 Ctrl"). `on_finished` is
/// called exactly once, whenever the wizard ends by any path -- clicking
/// "跳过", clicking "知道了，开始用" on the last step, or the window being
/// closed directly (titlebar X / Alt+F4) -- so the caller can persist
/// `settings.has_seen_onboarding = true` and save it.
pub fn show(hotkey_label: String, on_finished: impl FnOnce() + 'static) -> OnboardingController {
    let window = OnboardingWindow::new().expect("failed to create onboarding window");
    window.set_hotkey_label(hotkey_label.as_str().into());

    let steps = Rc::new(build_steps(&hotkey_label));
    apply_step(&window, &steps, 0);

    let current_step = Rc::new(RefCell::new(0usize));
    let on_finished: FinishOnce = Rc::new(RefCell::new(Some(Box::new(on_finished))));

    {
        let window_weak = window.as_weak();
        let current_step = current_step.clone();
        let steps = steps.clone();
        let on_finished = on_finished.clone();
        window.on_next(move || {
            let Some(window) = window_weak.upgrade() else { return };
            let mut step = current_step.borrow_mut();
            if *step < steps.len() - 1 {
                *step += 1;
                apply_step(&window, &steps, *step);
            } else {
                // "知道了，开始用" on the last step.
                finish_and_hide(&window_weak, &on_finished);
            }
        });
    }

    {
        let window_weak = window.as_weak();
        let current_step = current_step.clone();
        let steps = steps.clone();
        window.on_back(move || {
            let Some(window) = window_weak.upgrade() else { return };
            let mut step = current_step.borrow_mut();
            if *step > 0 {
                *step -= 1;
                apply_step(&window, &steps, *step);
            }
        });
    }

    {
        let window_weak = window.as_weak();
        let on_finished = on_finished.clone();
        window.on_skip(move || {
            finish_and_hide(&window_weak, &on_finished);
        });
    }

    {
        let window_weak = window.as_weak();
        window.window().on_close_requested(move || {
            finish_and_hide(&window_weak, &on_finished);
            slint::CloseRequestResponse::HideWindow
        });
    }

    window.show().expect("failed to show onboarding window");

    OnboardingController { window, current_step }
}

impl OnboardingController {
    /// The underlying Slint window, e.g. for callers that want to apply
    /// native Win32 styling (see `main.rs::apply_native_overlay_style` for
    /// the pattern this app already uses elsewhere) or check visibility.
    pub fn window(&self) -> &OnboardingWindow {
        &self.window
    }

    /// Current step index (0, 1, or 2).
    pub fn current_step(&self) -> usize {
        *self.current_step.borrow()
    }
}
