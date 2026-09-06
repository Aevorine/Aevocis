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
    /// Unix seconds at the moment this record was created. `#[serde(default)]`
    /// so history files written before this field existed still load (they
    /// deserialize to `0`). `purge_older_than_days` deliberately treats `0`
    /// as "unknown age, never auto-delete" rather than "ancient, delete
    /// first" -- when in doubt, retention purging must never destroy data it
    /// cannot actually confirm is old.
    #[serde(default)]
    pub epoch_secs: i64,
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
        if let Err(error) = crate::storage::atomic_write(&history_path(), json.as_bytes()) {
            eprintln!("warning: unable to save history: {error}");
        }
    }
}

/// Current local time as `HH:MM`, via `GetLocalTime` (avoids pulling in a
/// full date/time crate for one timestamp format, consistent with this
/// crate's existing direct-Win32-call style elsewhere).
pub fn now_hhmm() -> String {
    let st = unsafe { GetLocalTime() };
    format!("{:02}:{:02}", st.wHour, st.wMinute)
}

/// Current time as Unix seconds, for `Record::epoch_secs` (retention purging
/// needs a real date, not just the `HH:MM` display string above).
pub fn now_epoch_secs() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

/// F23: drops entries older than `days` (by `epoch_secs`). `days == 0` means
/// "keep forever" -- a no-op, returning `records` unchanged, matching
/// `AppSettings::history_retention_days`'s documented default meaning.
pub fn purge_older_than_days(records: Vec<Record>, days: u32) -> Vec<Record> {
    if days == 0 {
        return records;
    }
    let cutoff = now_epoch_secs() - (days as i64) * 86_400;
    records.into_iter().filter(|r| r.epoch_secs == 0 || r.epoch_secs >= cutoff).collect()
}
