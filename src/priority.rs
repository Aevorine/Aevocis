//! Process priority control, mirroring the shipping C# app's
//! `App.xaml.cs` process-priority calls: raised while actively dictating
//! (hotkey held) so recognition work isn't starved by other foreground
//! activity, lowered the rest of the time so this app stays a good
//! background citizen while idle.
//!
//! Deliberately never uses `HIGH_PRIORITY_CLASS` or
//! `REALTIME_PRIORITY_CLASS` -- either risks starving other processes or
//! the OS itself. `ABOVE_NORMAL_PRIORITY_CLASS` / `BELOW_NORMAL_PRIORITY_CLASS`
//! is the ceiling and floor in both directions, matching the C# app.

use windows::Win32::System::Threading::{
    ABOVE_NORMAL_PRIORITY_CLASS, BELOW_NORMAL_PRIORITY_CLASS, GetCurrentProcess, SetPriorityClass,
};

/// Raises this process's priority to `ABOVE_NORMAL_PRIORITY_CLASS`. Call
/// when active dictation starts (hotkey down). Best-effort: failure is
/// logged via `eprintln!` and otherwise ignored -- priority is a
/// nice-to-have, never load-bearing, so this never panics or returns an
/// error the caller must handle.
pub fn raise() {
    set_priority(ABOVE_NORMAL_PRIORITY_CLASS, "raise");
}

/// Lowers this process's priority to `BELOW_NORMAL_PRIORITY_CLASS`. Call
/// once at startup (idle default) and again whenever dictation
/// finishes/cancels/fails, so the app is a good citizen while idle but
/// responsive while actively recognizing speech. Same best-effort semantics
/// as `raise()`.
pub fn lower() {
    set_priority(BELOW_NORMAL_PRIORITY_CLASS, "lower");
}

fn set_priority(class: windows::Win32::System::Threading::PROCESS_CREATION_FLAGS, action: &str) {
    // SAFETY: GetCurrentProcess returns a pseudo-handle that never needs to
    // be closed, and SetPriorityClass with that handle only affects this
    // process's own scheduling priority.
    let result = unsafe { SetPriorityClass(GetCurrentProcess(), class) };
    if let Err(e) = result {
        eprintln!("priority: failed to {action} process priority: {e}");
    }
}
