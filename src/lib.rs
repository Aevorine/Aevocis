//! Library surface for the OpenSuperWhisper native Rust rewrite's first
//! vertical slice: global push-to-talk hotkey -> target-window capture ->
//! audio capture -> SenseVoice recognition -> safety re-check -> text
//! injection, plus a minimal Slint status overlay. See `src/main.rs` for how
//! these pieces are wired together, and `native/src/main.cpp` for the
//! reference design this replicates in idiomatic Rust (not copied).

pub mod app_info;
pub mod audio;
pub mod autostart;
pub mod crash_reporter;
pub mod draft_confirm;
pub mod history;
pub mod hotkey;
pub mod hotkey_capture;
pub mod inject;
pub mod onboarding;
pub mod priority;
pub mod punctuation;
pub mod recognizer;
pub mod resource_usage;
pub mod settings;
pub mod settings_window;
pub mod show_hide_hotkey;
pub mod single_instance;
pub mod storage;
pub mod target;
pub mod term_dictionary;
pub mod term_dictionary_window;
pub mod update;
pub mod voice;

use std::path::PathBuf;

// Model weights are intentionally not vendored; installed builds use the
// exe-relative `models/sensevoice` directory.

/// Resolves the SenseVoice model directory, in priority order:
/// 1. `OSW_SENSEVOICE_MODEL_DIR` env var (explicit override always wins).
/// 2. `models\sensevoice` next to the running exe -- this is the real,
///    portable path an installed copy uses (the installer places the model
///    files there), resolved via `current_exe()` rather than the process's
///    current working directory so it works regardless of how the exe was
///    launched (double-click, Start Menu shortcut, `cmd.exe` from elsewhere).
/// 3. Relative development candidates, so a checkout works without a
///    machine-specific path leaking into the binary or repository.
pub fn resolve_model_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("OSW_SENSEVOICE_MODEL_DIR") {
        return PathBuf::from(dir);
    }
    if let Ok(exe) = std::env::current_exe()
        && let Some(exe_dir) = exe.parent()
    {
        let packaged = exe_dir.join("models").join("sensevoice");
        if packaged.join("model.int8.onnx").is_file() {
            return packaged;
        }
    }
    if let Ok(current_dir) = std::env::current_dir() {
        for candidate in [
            current_dir.join("models/sensevoice"),
            current_dir.join("../src-reference/OpenSuperWhisper.App/Models/sensevoice"),
            current_dir.join("../../src-reference/OpenSuperWhisper.App/Models/sensevoice"),
        ] {
            if candidate.join("model.int8.onnx").is_file() {
                return candidate;
            }
        }
    }
    PathBuf::from("models/sensevoice")
}

/// Resolves (and ensures exists) the per-user app-data directory
/// `%LOCALAPPDATA%\Aevocis`, where `history.json` lives. Falls back to the
/// current directory if `LOCALAPPDATA` is somehow unset (never observed on
/// real Windows, but must not panic if it happens).
pub fn app_data_dir() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string());
    let dir = PathBuf::from(base).join("Aevocis");
    if let Err(error) = std::fs::create_dir_all(&dir) {
        eprintln!("warning: unable to create app data directory {}: {error}", dir.display());
    }
    dir
}
