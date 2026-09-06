//! Panic-time crash reporting: writes a self-contained, privacy-safe crash
//! report to `%LOCALAPPDATA%\Aevocis\crash-reports\` whenever this process
//! panics -- mirrors the shipping C# app's
//! `src-reference/OpenSuperWhisper.Core/CrashReporter.cs`, which writes a
//! dedicated report file (separate from the rolling log) precisely because
//! there should be one obvious file to attach when asking for help.
//!
//! Deliberately never reads `history.json` or `settings.json` -- the panic
//! payload, its source location, and a backtrace are the only inputs, so
//! there is no path for dictated speech content to end up in a report,
//! matching the C# app's explicit design choice.
//!
//! This hook only *observes* the panic: it does not prevent it from
//! propagating/unwinding. This crate's Cargo profile does not set
//! `panic = "abort"`, so Rust's default unwind behavior still runs after
//! this hook returns, which is what lets `Drop` impls -- e.g. tray-icon's
//! `Shell_NotifyIcon(NIM_DELETE)` cleanup -- still fire during unwinding,
//! mirroring the C# app's crash-time tray-icon-cleanup fix.

use std::io;
use std::path::{Path, PathBuf};

use windows::Win32::System::SystemInformation::GetLocalTime;

/// Newest reports beyond this count are deleted on each write -- matches the
/// C# app's `CrashReporter.MaxReports`.
const MAX_REPORTS: usize = 10;

/// Installs the panic hook. Call once, as early as possible in `main()`.
///
/// Chains the previous (default) hook rather than replacing it outright, so
/// the usual "thread panicked at ..." console output is unaffected -- this
/// only adds the crash-report file as a side effect alongside the existing
/// panic diagnostics, it doesn't take them away.
pub fn install() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        // A failing crash reporter must never itself panic (that would
        // abort mid-unwind) or mask the original panic -- any I/O failure
        // here is swallowed and just logged to stderr.
        if let Err(e) = write_report(info) {
            eprintln!("crash_reporter: failed to write crash report: {e}");
        }
        previous(info);
    }));
}

fn reports_dir() -> PathBuf {
    crate::app_data_dir().join("crash-reports")
}

fn write_report(info: &std::panic::PanicHookInfo) -> io::Result<()> {
    let dir = reports_dir();
    std::fs::create_dir_all(&dir)?;

    // GetLocalTime's fields give us a full date+time directly -- no need for
    // an external date/time crate for one timestamp format, consistent with
    // this crate's existing direct-Win32-call style (see history.rs::now_hhmm).
    let st = unsafe { GetLocalTime() };
    let file_stamp = format!(
        "{:04}{:02}{:02}-{:02}{:02}{:02}",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    );
    let display_time = format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    );

    // A short suffix (not just the second-granularity timestamp): a tight
    // crash loop can produce more than one report within the same second,
    // and a filename collision would silently overwrite the earlier report
    // instead of keeping both. Sub-second time is enough entropy here; no
    // RNG crate is a dependency of this crate.
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.subsec_nanos())
        .unwrap_or(0);
    let suffix = format!("{:06x}", nanos & 0x00FF_FFFF);

    let path = dir.join(format!("crash-{file_stamp}-{suffix}.txt"));

    let payload = info.payload();
    let message = if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "<non-string panic payload>".to_string()
    };

    let location = info
        .location()
        .map(|l| format!("{}:{}:{}", l.file(), l.line(), l.column()))
        .unwrap_or_else(|| "<unknown>".to_string());

    // Captured unconditionally rather than gated on RUST_BACKTRACE: a crash
    // report that's useless without an env var the user didn't know to set
    // defeats the point of having a crash report at all.
    let backtrace = std::backtrace::Backtrace::force_capture();

    let text = format!(
        "Aevocis crash report\n\
         Time: {display_time}\n\
         Version: {}\n\
         OS: Windows ({})\n\
         Panic: {message}\n\
         Location: {location}\n\
         Backtrace:\n{backtrace}\n",
        env!("CARGO_PKG_VERSION"),
        std::env::consts::ARCH,
    );

    std::fs::write(&path, text)?;
    rotate(&dir)?;
    Ok(())
}

/// Keeps only the newest `MAX_REPORTS` files in `dir`, deleting older ones.
/// The `crash-{yyyyMMdd-HHmmss}-{suffix}.txt` naming sorts chronologically
/// as plain strings, so a lexicographic sort is sufficient -- no need to
/// stat mtimes.
fn rotate(dir: &Path) -> io::Result<()> {
    let mut files: Vec<PathBuf> = std::fs::read_dir(dir)?
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|n| n.starts_with("crash-") && n.ends_with(".txt"))
        })
        .collect();
    files.sort();
    if files.len() > MAX_REPORTS {
        for old in &files[..files.len() - MAX_REPORTS] {
            let _ = std::fs::remove_file(old);
        }
    }
    Ok(())
}
