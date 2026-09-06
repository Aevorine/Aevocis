//! Standalone proof that the onboarding wizard (`osw_native::onboarding`)
//! runs end-to-end: shows the window, lets a human click through all 3
//! steps, and confirms `on_finished` fires exactly once when "知道了，开始用"
//! is clicked on the last step.
//!
//! Usage: `cargo run --example onboarding_demo`

fn main() {
    let _controller = osw_native::onboarding::show("右 Ctrl".to_string(), || {
        println!("onboarding finished");
        std::process::exit(0);
    });
    slint::run_event_loop().expect("event loop error");
}
