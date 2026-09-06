//! Controller for the "专业词典" (term dictionary) editor window
//! (`ui/term_dictionary_window.slint`): owns a `Rc<VecModel<TermRow>>` that
//! backs the window's row list, mutated in place by the add/delete/edit
//! callbacks below. Mirrors the `history_model` pattern already used in
//! `main.rs` (a `Rc<VecModel<_>>` held alongside its window, mutated from
//! callbacks) -- but unlike history, which persists every entry immediately,
//! edits here stay in memory only until "保存" is clicked; "取消" discards
//! them entirely, matching the C# reference's `TermDictionaryWindow` (edit a
//! working copy, commit to `TermDictionaryStore` only on explicit save).
//!
//! NOTE for the integrator: `TermDictionaryWindow`/`TermRow` below only exist
//! once `build.rs` actually compiles `ui/term_dictionary_window.slint` (this
//! file was verified against a *temporary* local addition of
//! `slint_build::compile("ui/term_dictionary_window.slint")` to `build.rs`,
//! reverted before commit -- see this slice's task notes / final report). The
//! `include!` below is the manual equivalent of `slint::include_modules!()`,
//! needed because that macro can only target one file per crate and
//! `main.rs` already claims it for `main_window.slint`.
include!(concat!(env!("OUT_DIR"), "/term_dictionary_window.rs"));

use std::rc::Rc;

use slint::{ComponentHandle, Model, VecModel};

use crate::term_dictionary::TermCorrection;

/// Holds the live window plus the model backing its row list. Dropping this
/// drops the window (and, transitively, the callbacks' `Rc<VecModel<_>>`
/// clones' last strong references live inside the window's own callback
/// closures, so nothing leaks once the window itself is gone).
pub struct TermDictionaryController {
    pub window: TermDictionaryWindow,
}

/// Builds and shows the term dictionary editor window, seeded from
/// `corrections` (typically `term_dictionary::load()`'s result). Wires:
/// - `add-row`: appends one blank row.
/// - `delete-row(i)` / `edit-wrong(i, text)` / `edit-correct(i, text)`:
///   mutate `rows` in place, in memory only -- nothing touches disk here.
/// - `save`: converts `rows` back into `Vec<TermCorrection>`, persists via
///   `term_dictionary::save`, then hides the window.
/// - `cancel`: hides the window without touching disk, discarding whatever
///   in-memory edits were made since it was opened.
pub fn open(corrections: Vec<TermCorrection>, on_close: impl Fn() + 'static) -> TermDictionaryController {
    let window = TermDictionaryWindow::new().expect("failed to create term dictionary window");
    let on_close: Rc<dyn Fn()> = Rc::new(on_close);

    let initial: Vec<TermRow> =
        corrections.into_iter().map(|c| TermRow { wrong: c.wrong.into(), correct: c.correct.into() }).collect();
    let rows = Rc::new(VecModel::from(initial));
    window.set_rows(rows.clone().into());

    {
        let rows = rows.clone();
        window.on_add_row(move || {
            rows.push(TermRow { wrong: "".into(), correct: "".into() });
        });
    }
    {
        let rows = rows.clone();
        window.on_delete_row(move |index| {
            let index = index as usize;
            if index < rows.row_count() {
                rows.remove(index);
            }
        });
    }
    {
        let rows = rows.clone();
        window.on_edit_wrong(move |index, text| {
            let index = index as usize;
            if let Some(mut row) = rows.row_data(index) {
                row.wrong = text;
                rows.set_row_data(index, row);
            }
        });
    }
    {
        let rows = rows.clone();
        window.on_edit_correct(move |index, text| {
            let index = index as usize;
            if let Some(mut row) = rows.row_data(index) {
                row.correct = text;
                rows.set_row_data(index, row);
            }
        });
    }
    {
        let rows = rows.clone();
        let weak = window.as_weak();
        let on_close = on_close.clone();
        window.on_save(move || {
            let corrections: Vec<TermCorrection> = rows
                .iter()
                .map(|r| TermCorrection { wrong: r.wrong.to_string(), correct: r.correct.to_string() })
                .collect();
            crate::term_dictionary::save(&corrections);
            if let Some(win) = weak.upgrade() {
                let _ = win.hide();
            }
            on_close();
        });
    }
    {
        let weak = window.as_weak();
        let on_close = on_close.clone();
        window.on_cancel(move || {
            if let Some(win) = weak.upgrade() {
                let _ = win.hide();
            }
            on_close();
        });
    }
    {
        let on_close = on_close.clone();
        window.window().on_close_requested(move || {
            on_close();
            slint::CloseRequestResponse::HideWindow
        });
    }

    window.show().expect("failed to show term dictionary window");

    TermDictionaryController { window }
}
