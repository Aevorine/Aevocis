//! Persisted user settings: `%LOCALAPPDATA%\Aevocis\settings.json`.
//!
//! Field set and defaults mirror the shipping C# app's `AppSettings`
//! (`src-reference/OpenSuperWhisper.Core/Models/AppSettings.cs`), minus the
//! fields that only make sense for the C# app's dual-engine (SenseVoice +
//! Whisper) design -- see `native-rust/SPEC.md`'s "Explicit descopes" section
//! for why `RecognitionEngine`/`ModelSize`/`AppSpecificPrompts` have no
//! equivalent here. Storage path is unified under `Aevocis` for every file
//! this app writes; the C# app split some stores across an old
//! `OpenSuperWhisper` folder and a newer `Aevocis` one, which its own
//! research flagged as an inconsistency -- deliberately not replicated here.

use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(rename_all = "lowercase")]
pub enum PushToTalkMode {
    Hold,
    Toggle,
}

impl Default for PushToTalkMode {
    fn default() -> Self {
        PushToTalkMode::Hold
    }
}

/// `VK_RCONTROL`, matching both this app's existing hardcoded default
/// (`hotkey.rs`) and the C# app's own default.
pub const DEFAULT_PUSH_TO_TALK_VK: u32 = 0xA3;
/// `MOD_CONTROL | MOD_ALT`, matching `main.rs`'s existing show/hide hotkey
/// registration.
pub const DEFAULT_SHOW_HIDE_MODIFIERS: u32 = 0x0003;
/// `VK_H`.
pub const DEFAULT_SHOW_HIDE_VK: u32 = 0x48;

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default)]
pub struct AppSettings {
    /// BCP-47-ish language hint passed to the recognizer; SenseVoice treats
    /// "auto" as native language-ID across mixed Chinese/English in one pass.
    pub language: String,
    /// "" = follow the system default input device.
    pub microphone_device_id: String,
    pub push_to_talk_virtual_key: u32,
    pub push_to_talk_mode: PushToTalkMode,
    pub auto_start_with_windows: bool,
    pub autocorrect_punctuation: bool,
    /// 0 = keep forever.
    pub history_retention_days: u32,
    pub has_seen_onboarding: bool,
    /// Process name (lowercase, no `.exe`) -> push-to-talk VK override for
    /// that app specifically.
    pub app_specific_hotkeys: HashMap<String, u32>,
    /// F11: show a draft-confirm window before injecting instead of injecting
    /// immediately.
    pub show_draft_before_inject: bool,
    pub show_hide_hotkey_modifiers: u32,
    pub show_hide_virtual_key: u32,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            language: "auto".to_string(),
            microphone_device_id: String::new(),
            push_to_talk_virtual_key: DEFAULT_PUSH_TO_TALK_VK,
            push_to_talk_mode: PushToTalkMode::Hold,
            auto_start_with_windows: false,
            autocorrect_punctuation: true,
            history_retention_days: 0,
            has_seen_onboarding: false,
            app_specific_hotkeys: HashMap::new(),
            show_draft_before_inject: false,
            show_hide_hotkey_modifiers: DEFAULT_SHOW_HIDE_MODIFIERS,
            show_hide_virtual_key: DEFAULT_SHOW_HIDE_VK,
        }
    }
}

fn settings_path() -> PathBuf {
    crate::app_data_dir().join("settings.json")
}

/// Loads persisted settings, falling back to defaults if the file is missing
/// or fails to parse (e.g. hand-edited into invalid JSON) -- a corrupt
/// settings file must never prevent the app from starting.
pub fn load() -> AppSettings {
    std::fs::read_to_string(settings_path())
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

/// Atomically persists `settings` with the shared Windows replacement helper.
pub fn save(settings: &AppSettings) {
    let path = settings_path();
    let Ok(json) = serde_json::to_string_pretty(settings) else { return };
    if let Err(error) = crate::storage::atomic_write(&path, json.as_bytes()) {
        eprintln!("warning: unable to save settings: {error}");
    }
}

/// F31: bundles the current settings.json plus the raw contents of any other
/// JSON store (terms.json, voice_commands.json, macros.json) into one file.
/// Takes raw `serde_json::Value`s rather than concrete types so this module
/// has no compile-time dependency on `term_dictionary`/`voice` -- callers in
/// `main.rs` pass whatever each store's own loader already parsed.
#[derive(Serialize, Deserialize, Default)]
pub struct SettingsBundle {
    pub settings: AppSettings,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub terms: Option<serde_json::Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub voice_commands: Option<serde_json::Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub macros: Option<serde_json::Value>,
}

pub fn export_bundle(path: &Path, bundle: &SettingsBundle) -> io::Result<()> {
    let json = serde_json::to_string_pretty(bundle)?;
    crate::storage::atomic_write(path, json.as_bytes())
}

/// Unlike `load()`, a user-initiated import surfaces its error directly
/// rather than silently defaulting -- the UI is expected to show it, matching
/// the C# app's `SettingsPortability.Import` behavior.
pub fn import_bundle(path: &Path) -> io::Result<SettingsBundle> {
    let text = std::fs::read_to_string(path)?;
    serde_json::from_str(&text).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))
}
