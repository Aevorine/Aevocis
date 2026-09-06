//! Manual proof that `osw_native::crash_reporter::install()` actually writes
//! a crash report file when this process panics, independent of any real
//! dictation flow.
//!
//! Usage: `cargo run --example crash_demo` -- this WILL exit with a panic,
//! that's the point. After running, check
//! `%LOCALAPPDATA%\Aevocis\crash-reports\` for the new report file.

fn main() {
    osw_native::crash_reporter::install();
    panic!("test panic for crash reporter verification");
}
