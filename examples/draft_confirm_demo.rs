//! Manual smoke test for `osw_native::draft_confirm::show`: shows the popup
//! pre-filled with a Chinese test string, prints whatever result comes back,
//! then exits. Requires `build.rs` to also compile
//! `ui/draft_confirm_window.slint` (see `src/draft_confirm.rs`'s doc comment)
//! -- not wired into the shipped `build.rs` by default.
//!
//! Run with `cargo run --example draft_confirm_demo`, then either press
//! Enter (with or without editing the text first) or Escape.

fn main() {
    osw_native::draft_confirm::show("这是一段测试草稿文本".to_string(), |result| {
        println!("draft result: {result:?}");
        std::process::exit(0);
    });
    slint::run_event_loop().expect("event loop error");
}
