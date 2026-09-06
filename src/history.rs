//! Dictation history persistence: `%LOCALAPPDATA%\Aevocis\history.json`.
//!
//! Records every non-empty recognition result regardless of what happened to
//! it afterward (inserted, blocked, or retained as a draft because the
//! target window changed) -- matching the shipping C# app's `HistoryStore`
//! semantics, which logs the recognized utterance itself, not the injection
//! outcome. A missing or corrupt file is treated as "no history yet" rather
//! than an error: this is a convenience log, not data the app depends on to
//! function.

use serde::{Deserialize, Serialize};
use windows::Win32::System::SystemInformation::GetLocalTime;

/// Oldest entries beyond this count are dropped on save, newest-first.
pub const MAX_ENTRIES: usize = 300;

#[derive(Serialize, Deserialize, Clone)]
pub struct Record {
    pub time: String,
    pub text: String,
}

fn history_path() -> std::path::PathBuf {
    crate::app_data_dir().join("history.json")
}

/// Loads persisted history, newest-first. Returns an empty list if the file
/// does not exist yet or fails to parse (e.g. hand-edited into invalid JSON).
pub fn load() -> Vec<Record> {
    std::fs::read_to_string(history_path())
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

/// Persists `records` (already newest-first), trimmed to `MAX_ENTRIES`.
pub fn save(records: &[Record]) {
    let trimmed = &records[..records.len().min(MAX_ENTRIES)];
    if let Ok(json) = serde_json::to_string_pretty(trimmed) {
        let _ = std::fs::write(history_path(), json);
    }
}

/// Current local time as `HH:MM`, via `GetLocalTime` (avoids pulling in a
/// full date/time crate for one timestamp format, consistent with this
/// crate's existing direct-Win32-call style elsewhere).
pub fn now_hhmm() -> String {
    let st = unsafe { GetLocalTime() };
    format!("{:02}:{:02}", st.wHour, st.wMinute)
}
