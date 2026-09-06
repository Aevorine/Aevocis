//! Manual proof that `osw_native::update::check_latest()` actually hits the
//! real GitHub API and parses a real response, independent of any UI wiring.
//!
//! Usage: `cargo run --example update_check_demo` -- this hits the real
//! network and the real `Aevorine/Aevocis` GitHub repo. `None` is a normal,
//! expected result whenever this build is already current or the network is
//! unavailable -- see `osw_native::update::check_latest`'s doc comment.
//!
//! Deliberately does not call `download_and_relaunch`: that would download a
//! ~150MB installer and try to run it, which is not something a compile/CI
//! check should ever do.

fn main() {
    let result = osw_native::update::check_latest();
    println!("{result:?}");
}
